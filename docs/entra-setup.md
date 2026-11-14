# Entra App Registration & Desktop Build Guide

Azure Design Studio signs users in with **Microsoft Entra ID** (work/personal
accounts; B2C is no longer used). Three pieces consume the registration:

| Piece | Client id used | Audience of the token |
|---|---|---|
| WASM browser app | `AzureAd:ClientId` in `src/AzureDesignStudio/wwwroot/appsettings.json` | server API scope |
| MAUI desktop app | `AzureAd:ClientId` in Desktop `appsettings.json` (beside the exe) | server API scope |
| Server (validation) | `ServerScope` = `api://<server-app-id>/Server.Access` | its own JWT keys |

## 1. Register the server API app

1. Entra admin center → **App registrations** → *New registration* (single tenant
   or multi-tenant, as your org requires). Note the **Application (client) ID** — this is the *server app id*.
2. **Expose an API**:
   - Application ID URI: `api://<server-app-id>`
   - Add a scope: `Server.Access` — *Admins and users*, "Access the ADS server".
3. **Authentication** (for WASM): add platform *Single Page Application*,
   redirect URI `https://<your-host>/authentication/login` (plus
   `http://localhost:5203/authentication/login` for local runs).
4. Token configuration: optional claims as needed; the server validates the
   `scp` claim against `Server.Access` (`RequiredScope` in `Program.cs`).

## 2. Register the client app(s)

You can reuse the server app for clients or register a separate SPA/native app:

- **WASM**: platform *Single Page Application*; add the same redirect URIs;
  under *API permissions*, add the `Server.Access` scope from step 1.
- **Desktop**: platform *Mobile & desktop* (public client — no secret).
  MSAL uses the **Windows broker (WAM)** automatically; the **device code
  flow** is the fallback, so no redirect URI is required. Add the
  `Server.Access` API permission.

## 3. Configure the app

`src/AzureDesignStudio/wwwroot/appsettings.json` (WASM):

```json
{ "AzureAd": { "Authority": "https://login.microsoftonline.com/common",
               "ClientId": "<client app id>",
               "ValidateAuthority": true },
  "ServerScope": "api://<server-app-id>/Server.Access" }
```

Desktop `appsettings.json` (copied next to the exe, same shape) adds
`ServerUrl` — the native gRPC endpoint, e.g. `https://ads.example.com:5001` or
`http://localhost:5203` for development.

The repository ships **placeholders** (`<client id>`,
`api://<server client id>/Server.Access`). With placeholders the apps still
work anonymously: sign-in fails gracefully and designs can be exported as
ARM/Bicep files; the desktop app additionally keeps designs in its offline
cache. Save/load/deploy against the server requires the real ids.

Server-side (`appsettings`/Key Vault in production):

- `AzureAd:Instance/TenantId/ClientId/Audience` matching the server app,
- `Authority` `https://login.microsoftonline.com/common` (or the tenant),
- the JWT signing key source (`ServerScopeKey` / DataProtection keys as configured in `Program.cs`).

## 4. Build & package the desktop app

Prereq: .NET 10 SDK with `dotnet workload install maui-windows`.

```powershell
# Run unpackaged (development default, WindowsPackageType=None)
dotnet build src/AzureDesignStudio.Desktop
src\AzureDesignStudio.Desktop\bin\Debug\net10.0-windows10.0.19041.0\win-x64\AzureDesignStudio.Desktop.exe

# Build an MSIX installer
dotnet publish src/AzureDesignStudio.Desktop -c Release -f net10.0-windows10.0.19041.0 `
  -p:WindowsPackageType=MSIX -p:RuntimeIdentifier=win-x64 -p:WindowsAppSDKSelfContained=true
```

The MSIX lands under `bin\Release\...\Publish\win-x64\` (`.msix` / `.appx`,
plus an install PowerShell script). The manifest
(`Platforms/Windows/Package.appxmanifest`) uses
`Publisher="CN=Azure Design Studio"`, so a locally built package must be
**signed** (and the cert trusted) before Windows will install it — see
`.github/workflows/publish.yml` (`MSIX_PFX_BASE64` / `MSIX_PFX_PASSWORD`
secrets sign automatically; without them the artifact is uploaded unsigned).
For Store distribution change Identity/Publisher to your Store identity via
Partner Center packaging.

The publisher identity is decided: Azure Artifact Signing (formerly Trusted
Signing) with individual validation, certificate subject `CN=Jakub Talaj`
(Phase 1, 2026-10-21). The manifest's future `Publisher` must equal that
subject verbatim; `PublisherDisplayName` may remain "Azure Design Studio".
The manifest still carries the placeholder `Publisher="CN=Azure Design
Studio"` and is not edited in Phase 1. Phase 11 makes the manifest
`Publisher` equal the certificate subject and asserts it in CI, because a
mismatch fails installation with `0x8007000B`.

## 5. Offline behaviour (desktop)

- Designs saved while the server is unreachable are written under
  `%LocalAppData%\AzureDesignStudio…signs\*.adsdesign.json`.
- They are listed alongside server designs and always win over the server
  copy until they sync (next successful save/list/load pushes them).
- Deleting a design removes both copies.

## 6. App roles, tenants and approvals

The server decides who may change guardrails and who may approve a deployment plan from two **app roles** on
the server API app registration, and it keeps every organization's data apart by tenant. This section sets the
roles up; `docs/identity-and-guardrails.md` explains every rule and refusal in full.

| Role (`value`) | Lets the user |
|---|---|
| `Guardrail.Admin` | read and change the tenant's guardrail policy and the two tenant settings (allow self-approval, guardrail policy mode) |
| `Deploy.Approver` | approve a What-If plan and approve a decommission window |

### Define the roles

On the server API app registration (step 1), open **App roles** (or edit the **Manifest**) and add these two
entries to `appRoles`. Generate a fresh GUID for each `id`; the ones below are placeholders.

```json
"appRoles": [
  {
    "allowedMemberTypes": [ "User" ],
    "displayName": "Guardrail administrator",
    "id": "<new guid for Guardrail.Admin>",
    "description": "Reads and changes the tenant guardrail policy and tenant settings.",
    "isEnabled": true,
    "value": "Guardrail.Admin"
  },
  {
    "allowedMemberTypes": [ "User" ],
    "displayName": "Deploy approver",
    "id": "<new guid for Deploy.Approver>",
    "description": "Approves What-If plans and decommission windows.",
    "isEnabled": true,
    "value": "Deploy.Approver"
  }
]
```

`value` is exactly what the token carries in its `roles` claim, and it is **case-sensitive**: `Guardrail.Admin`
and `Deploy.Approver`, nothing else. A role can be assigned to a user or, with Microsoft Entra ID P1 or P2, to
a group (nested groups are not supported for app role assignment). A role that arrives through a group may
fail to show up in the token, so check the token of a test user before relying on it.

### Assign a role

1. Entra admin center, **Enterprise applications**, open the **server app** (the enterprise application that
   belongs to the registration from step 1).
2. **Users and groups**, **Add user/group**, pick the user (or group) and the role, **Assign**.
3. If the enterprise application has **Assignment required** switched on, users without any assignment
   cannot sign in to the server at all; leave it off unless you want that.

### Roles are not retroactive

A token only carries the roles that were assigned **before it was issued**. After assigning or removing a role,
the user signs out of Azure Design Studio and signs back in to get a new token; until then the server still
refuses with `GUARDRAIL_ADMIN_REQUIRED` or `PLAN_APPROVER_ROLE_REQUIRED` and the message says so. The
browser app's service worker no longer precaches `appsettings*.json`, so a configuration change is picked up
from the network; a browser that already holds the old cache refreshes it when the next service worker version
activates.

### Other callers

- **Work or school account:** the roles above are required, and the data is scoped to the tenant (`tid`).
- **Personal Microsoft account:** there is no organization to assign roles in. A personal account administers
  its **own** scope (scope key = its own object id) and counts as `Deploy.Approver` for it. Approving a plan
  it requested itself is still governed by the tenant setting "allow self-approval" of that scope, which the
  personal account turns on itself with the call recipe in the operator document.
- **Desktop app:** it runs every service in-process as a single-user host and counts as administrator and
  approver, and may approve its own plans. No role assignment is needed or used.

See `docs/identity-and-guardrails.md` for the response codes, the tenant settings and how an administrator
changes them.
