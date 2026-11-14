# Identity, roles, tenant scope and guardrails (operator guide)

This document is for the person who runs an Azure Design Studio server (or supports the desktop app). It says how
the server decides who a caller is, which app roles it enforces, which data is kept per tenant, what every refusal
means, and how an administrator changes the two tenant settings. Role setup in Microsoft Entra ID is in
`docs/entra-setup.md` (section 6); database upgrade handling is in `docs/database-operations.md`.

Nothing in this document contains a credential. Placeholders are written in angle brackets.

## How a caller is identified

Every Design and Deploy call derives its caller from the validated token, in one place
(`src/AzureDesignStudio.Services/Auth/AdsIdentity.cs`):

- The user is the `oid` claim and the tenant is the `tid` claim. Each is accepted in its short spelling (`oid`,
  `tid`) or its long URI spelling (`http://schemas.microsoft.com/identity/claims/objectidentifier`,
  `http://schemas.microsoft.com/identity/claims/tenantid`), must be a non-empty GUID, and must agree when both
  spellings are present. The subject claim (`sub`) is never used: it differs per application.
- Roles come from the `roles` claim.
- A token without a usable `oid` and `tid` is refused in-band with status **401** and the message
  `IDENTITY_INVALID: the token carries no usable oid and tid.` The gRPC call itself succeeds; the response
  carries the code. Remedy: sign in again; if it persists the token was not issued for a work, school or personal
  account (for example an application-only token), which is not supported.
- **Scope key.** Data is separated by scope. For an organization the scope is the tenant id (`tid`). Every
  personal Microsoft account shares one tenant, `9188040d-6c67-4c5b-b112-36a304b66dad` (a public constant of
  Microsoft Entra), so for a personal account the scope is the user's own object id (`oid`). One personal account
  therefore never sees another's guardrail policy, approvals or settings.

The desktop app is a different kind of caller. It runs the same services in-process, and the host declares itself
a single-user host out of band (never through a claim), so no token or role is involved.

## Who may do what

| Caller | Administer guardrails and tenant settings | Approve a plan or decommission window | Approve a plan it requested itself |
|---|---|---|---|
| Organization user | only with the `Guardrail.Admin` role | only with the `Deploy.Approver` role | only when the tenant setting "allow self-approval" is on (and the user holds `Deploy.Approver`) |
| Personal account | yes, for its own scope | yes, for its own scope | only when that scope's "allow self-approval" is on; the account turns it on itself |
| Desktop host | yes | yes | yes |

Roles are not retroactive: a token carries the roles assigned before it was issued, so after an assignment the
user signs out and back in (`docs/entra-setup.md`, section 6).

## Response codes

Refusals are answered in-band: the response carries an HTTP-style `status_code` and an `error_message` starting
with the code.

| Code | Status | Meaning | Remedy |
|---|---|---|---|
| `IDENTITY_INVALID` | 401 | The token has no usable `oid` and `tid`. | Sign in again; use a supported account type. |
| `GUARDRAIL_ADMIN_REQUIRED` | 403 | Reading or changing the guardrail policy needs `Guardrail.Admin` (or a personal account's own scope, or the desktop host). | Have an administrator assign the role, then sign out and back in. |
| `TENANT_SETTINGS_ADMIN_REQUIRED` | 403 | Reading or changing the tenant settings needs `Guardrail.Admin`. | Same as above. |
| `TENANT_SETTINGS_INVALID` | 400 | `guardrail_policy_mode` is not `replace` or `merge`. | Send one of the two values. |
| `PLAN_APPROVER_ROLE_REQUIRED` | 403 | Approving a plan needs `Deploy.Approver`. Checked first, before the plan is looked up. | Assign the role, sign in again. |
| `PLAN_SELF_APPROVAL_REFUSED` | 403 | The principal that requested the review cannot approve it, and the tenant has not allowed self-approval. | Another `Deploy.Approver` approves it, or a `Guardrail.Admin` allows self-approval for the tenant. |
| `PLAN_REQUESTER_MISMATCH` | 403 | Only the principal that requested the review may deploy it (someone else approved it, and the requester deploys). | The requester starts the deployment. |
| `PLAN_APPROVER_MISMATCH` | 403 | An approval stored without a requester can only be deployed by its approver. | The approver deploys, or approve the plan again. |
| `DECOM_APPROVER_ROLE_REQUIRED` | 403 | Approving a decommission window needs `Deploy.Approver`. There is no self-approval rule for decommission: the approver also executes. | Assign the role, sign in again. |

A What-If review is visible only to the scope that created it. Approving a plan hash that only another tenant's
review holds fails with 400 `WHAT_IF_STALE` ("no live What-If preview exists for this plan; run What-If first"),
the same answer as for an unknown or expired plan, so the existence of another tenant's plan is never revealed.

## Tenant settings

Each scope has two settings, stored per tenant and changed only by an administrator. A scope that never set them
runs on the defaults.

| Setting | Field | Default | Effect |
|---|---|---|---|
| Allow self-approval | `allow_self_approval` | off | When on, the principal that requested a What-If review may also approve it. |
| Guardrail policy mode | `guardrail_policy_mode` | `replace` | `replace`: the tenant's own guardrail policy is the whole list. `merge`: the inherited default policy and the tenant's own policy both apply. |

**There is no settings screen yet.** None is planned before Phase 9, so an administrator changes the two
settings with the call recipe below. Every change is recorded in the append-only table `AdsSecurityEvents`, one
event per changed setting (`SelfApprovalChanged`, `PolicyModeChanged`) and one for each guardrail policy change
(`GuardrailPolicyChanged`), written in the same commit as the change. An event holds the scope, the principal and
what changed (the old and new setting values; for a policy only the length and a short hash of the new body),
never a secret.

## Changing the tenant settings (administrators)

### What you need

- The server's gRPC address: the HTTPS endpoint of the server, which speaks HTTP/2 (`https://<server-host>`;
  `https://localhost:7203` for a local development server).
- A bearer token for the `Server.Access` scope of the server app registration (`api://<server-app-id>/Server.Access`),
  issued to a user who holds `Guardrail.Admin` (or to a personal account, for its own scope).
- The token is passed through the environment variable `ADS_TOKEN`. **Never paste a token into a command line or a
  file**: it would land in shell history, process lists and logs. One way to fill the variable without the value
  appearing anywhere is
  `export ADS_TOKEN="$(az account get-access-token --scope api://<server-app-id>/Server.Access --query accessToken -o tsv)"`,
  which works when the Azure CLI is an authorized client application of the server app registration (Expose an
  API, Authorized client applications). Otherwise use the token the signed-in application already holds. Clear it
  afterwards with `unset ADS_TOKEN`. Tokens are short-lived.

### Read first, then write both

A Set **replaces both settings** with what you send, so always read, change the one value you mean to change, and
write both back. Sending only one field resets the other to its zero value (self-approval off, and an empty mode
is rejected).

1. `Deploy/GetTenantSettings` (no request fields)
2. `Deploy/SetTenantSettings` with `allow_self_approval` and `guardrail_policy_mode`

The `Deploy` service has no proto package, so the full method names are `Deploy/GetTenantSettings` and
`Deploy/SetTenantSettings`. The request and response messages are in
`src/AzureDesignStudio.SharedModels/deploy_model.proto`.

### Example with grpcurl

Run from the repository root so `-import-path` finds the proto file. `grpcurl` reads the header value from the
shell variable; `-insecure` is only for a local development certificate and must not be used against a real
server. `-emit-defaults` makes it print `false` instead of omitting it.

```bash
# Read
grpcurl -emit-defaults -import-path src/AzureDesignStudio.SharedModels -proto deploy_model.proto \
  -H "Authorization: Bearer $ADS_TOKEN" -d '{}' \
  <server-host>:443 Deploy/GetTenantSettings

# Write both settings back (here: allow self-approval, keep mode replace)
grpcurl -emit-defaults -import-path src/AzureDesignStudio.SharedModels -proto deploy_model.proto \
  -H "Authorization: Bearer $ADS_TOKEN" \
  -d '{"allow_self_approval": true, "guardrail_policy_mode": "replace"}' \
  <server-host>:443 Deploy/SetTenantSettings
```

The shell expands `$ADS_TOKEN` before `grpcurl` starts, so on a shared machine the value can be seen by other
local users in the process list while the command runs. Use the C# example below on a shared machine.

### Example in C#

For code that references the SharedModels project (`Grpc.Net.Client` is already a dependency of it):

```csharp
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

var token = Environment.GetEnvironmentVariable("ADS_TOKEN")
    ?? throw new InvalidOperationException("Set ADS_TOKEN first.");
using var channel = GrpcChannel.ForAddress("https://<server-host>");
var client = new Deploy.DeployClient(channel);
var headers = new Metadata { { "Authorization", $"Bearer {token}" } };

var current = await client.GetTenantSettingsAsync(new Empty(), headers);
if (current.StatusCode != 200) { Console.WriteLine(current.ErrorMessage); return; }

var saved = await client.SetTenantSettingsAsync(new TenantSettingsRequest
{
    AllowSelfApproval = true,                              // the value being changed
    GuardrailPolicyMode = current.GuardrailPolicyMode,     // written back unchanged
}, headers);
Console.WriteLine($"{saved.StatusCode} {saved.ErrorMessage}");
```

Code that already holds a `GrpcClientFactory` (the client application's services) uses the wrapper pair on
`DeployGrpcService` (`src/AzureDesignStudio/Services/DeployGrpcService.cs`), which signs the call with the user's
own token:

```csharp
var current = await deployGrpcService.GetTenantSettingsAsync();          // null: the server could not be asked
if (current is { StatusCode: 200 })
{
    var saved = await deployGrpcService.SetTenantSettingsAsync(
        allowSelfApproval: true, guardrailPolicyMode: current.GuardrailPolicyMode);
}
```

### Reading the answer

| Answer | Meaning |
|---|---|
| status 200 | The stored values are in the response, with `updated_by` and `updated_at`. |
| 403 `TENANT_SETTINGS_ADMIN_REQUIRED` | Not an administrator, or the role was assigned after the token was issued: sign in again. |
| 400 `TENANT_SETTINGS_INVALID` | `guardrail_policy_mode` must be `replace` or `merge`. |
| 401 `IDENTITY_INVALID` | The token has no usable `oid` and `tid`. |

A transport failure or a missing token (no response at all) is not an answer about the settings; fix the
connection first.

### The desktop host

The desktop app already counts as an administrator and may approve its own plans, but it has no screen to change
the policy mode today. Its policy mode therefore stays `replace` until the Phase 9 settings screen uses the
wrapper pair above. Nothing about the desktop app needs the call recipe.

## INTERPRETATION TO CONFIRM: guardrail override mode

When the question was asked whether a tenant's own guardrail policy replaces the inherited default or is merged
with it, the answer given was "Option 1 and option 2 are available to choose". It was read as **both modes being
selectable per tenant**, with `replace` as the default and `merge` as the alternative. This is an
interpretation and needs the owner's confirmation; it is recorded here so it can be corrected cheaply.

- `replace` (default): the tenant's guardrail policy is the complete list. A tenant with its own policy is judged
  by it alone, even when it is empty. A tenant with no policy of its own is judged by the inherited default policy.
- `merge`: the rules of the inherited default policy always apply, and the tenant's own rules are added; the
  tenant can only add rules, never remove one of the default.

If the reading is wrong, change it in exactly these places:

1. The mode vocabulary: `src/AzureDesignStudio.Services/Models/TenantModels.cs` (`GuardrailPolicyModes`).
2. The resolution: `LoadGuardrailPolicy` in `src/AzureDesignStudio.Services/DeployService.cs`, and the pure
   `GuardrailPolicy.Merge`.
3. The stored setting: the `GuardrailPolicyMode` column of the tenant settings (a change to the column or its
   default is a new migration for SQL Server and for SQLite).

Removing `merge` (only `replace`) is the cheapest correction: stop accepting the value in `GuardrailPolicyModes`.
Making `merge` the only behavior also changes what existing tenants' stored policies mean, which is why it is
costly.

## Upgrade consequences

- **Approvals pending at upgrade must be re-approved.** Approvals are now keyed by scope and plan hash. The
  `TenantApprovals` migration moves every approval that exists at upgrade into the empty-GUID scope, a scope no
  caller can have, so none of them matches any caller. Nothing is lost, but each plan has to be run through
  What-If again and re-approved by an approver of its tenant.
- **The inherited default policy stays.** The pre-upgrade guardrail row is kept as the default (no tenant) and
  applies to every scope that has no policy of its own. A tenant's first change creates that tenant's own row
  and, under `replace`, stops inheriting.
- **One-click approve-then-deploy on a hosted server stops at approval by default.** The principal that requested
  the review cannot approve it unless self-approval is allowed, and the requester is the one who deploys. Either
  a second `Deploy.Approver` approves the shared plan (the requester then deploys it), or a `Guardrail.Admin`
  turns on "allow self-approval" for the tenant. A personal account that ran the What-If must enable that setting
  for its own scope before approving its own plan. The desktop host is unaffected.
- Database steps: `docs/database-operations.md` (the `TenantGuardrails` and `TenantApprovals` migrations).

## Anonymous surface

Every gRPC method requires an authenticated user with the `Server.Access` scope except one:
`Cost.GetRetailPrices`, the public retail price lookup. The test
`src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs` lists that method literally and fails
when any other method becomes anonymous, so adding a service without protecting it is caught.

## Browser cache of configuration

The browser app's service worker (`src/AzureDesignStudio/wwwroot/service-worker.published.js`) does not precache
`appsettings*.json`. A change to the client's identity or scope configuration therefore reaches the browser from
the network. A browser that already holds an older cache drops it when the next worker version activates.

## Development crypto provider

The server stores linked subscription secrets encrypted. In production the key lives in Azure Key Vault
(`Crypto:Provider` = `KeyVault`, the default). For offline work the server can use a local key ring instead:

- Select it with `Crypto__Provider=Development` (the `Crypto:Provider` setting). The Development launch
  profiles of the server project set it, so `dotnet run` needs no vault.
- It works **only outside Production**. A Production host with this value refuses to start; Production still
  needs Key Vault.
- The key ring lives under `%LOCALAPPDATA%\AzureDesignStudio\dev-keys` on Windows and `~/.local/share/AzureDesignStudio/dev-keys`
  on macOS and Linux. Set `Crypto:DevelopmentKeyRingPath` (`Crypto__DevelopmentKeyRingPath`) to use another folder.
- On Windows the keys are protected with DPAPI (current user). On macOS and Linux the keys are **plaintext XML**
  in a folder only your user can open. This is a developer-only mode; treat the folder like a password file.
- Switching providers makes linked subscriptions unreadable: a secret encrypted by one provider cannot be read by
  the other (development secrets are prefixed `dev1:`). Calls that need such a link answer 404 until the
  subscription is linked again.
