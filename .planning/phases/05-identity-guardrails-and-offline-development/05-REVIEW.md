---
phase: 05-identity-guardrails-and-offline-development
reviewed: 2026-11-14T00:00:00Z
depth: standard
files_reviewed: 33
files_reviewed_list:
  - src/AzureDesignStudio.Services/Auth/AdsIdentity.cs
  - src/AzureDesignStudio.Services/Auth/AdsCaller.cs
  - src/AzureDesignStudio.Services/ServiceTools.cs
  - src/AzureDesignStudio.Services/InProcess/InProcessCallInvoker.cs
  - src/AzureDesignStudio.Services/DeployService.cs
  - src/AzureDesignStudio.Services/DesignService.cs
  - src/AzureDesignStudio.Services/Models/DesignDbContext.cs
  - src/AzureDesignStudio.Services/Models/TenantModels.cs
  - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114153430_TenantGuardrails.cs
  - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114160052_TenantApprovals.cs
  - src/AzureDesignStudio.Server/Program.cs
  - src/AzureDesignStudio.Server/Auth/AdsPolicies.cs
  - src/AzureDesignStudio.Server/Services/HostedServices.cs
  - src/AzureDesignStudio.Server/Services/CryptoProvider.cs
  - src/AzureDesignStudio.Server/Services/DevelopmentCryptoService.cs
  - src/AzureDesignStudio.Server/Persistence/Migrations/20261114153438_TenantGuardrails.cs
  - src/AzureDesignStudio.Server/Persistence/Migrations/20261114160048_TenantApprovals.cs
  - src/AzureDesignStudio.Server/Properties/launchSettings.json
  - src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs
  - src/AzureDesignStudio.Discovery/Unmanaged/GuardrailPolicy.cs
  - src/AzureDesignStudio.SharedModels/deploy_model.proto
  - src/AzureDesignStudio/Services/DeployGrpcService.cs
  - src/AzureDesignStudio/wwwroot/service-worker.published.js
  - src/AzureDesignStudio/JsTests/service-worker-precache.test.cjs
  - src/AzureDesignStudio.Desktop/DeployDbStartup.cs
  - src/AzureDesignStudio.Desktop/DesktopIdentity.cs
  - src/AzureDesignStudio.Desktop/MsalAccountService.cs
  - docs/identity-and-guardrails.md
  - docs/entra-setup.md
  - docs/database-operations.md
  - AGENTS.md
  - src/AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs
findings:
  critical: 1
  warning: 8
  info: 6
  total: 15
  necessity: 0
status: issues_found
---

# Phase 05: Code Review Report

**Reviewed:** 2026-11-14
**Depth:** standard
**Files Reviewed:** 33 (production code in depth, tests and docs skimmed for vacuous assertions and factual contradictions)
**Status:** issues_found

## Summary

The identity core is sound. `AdsIdentity.TryFrom` rejects an empty GUID and disagreeing claim spellings. The single-user marker lives in a per-call server-side `UserState` dictionary that no gRPC metadata can write. Every Design and Deploy RPC calls `TryGetCaller` first, and `CallerInterceptor` fails closed (no principal gives in-band 401) for any future streaming shape. Approvals, tokens and tenant settings are keyed by scope. The hand-written T-SQL runs inside the migration transaction and is idempotent. The audit rows share one `SaveChanges` with the change they record.

I found one cross-tenant disclosure: the inherited default guardrail row is returned verbatim, including its author, to any caller who counts as a guardrail administrator, and that includes every personal Microsoft account. The other findings are fail-open defaults in the token gate, the "Production only" crypto refusal, unsanitised display names in audit text, gaps in the separation-of-duties model, and doc/code contradictions. No tests were found to be vacuous in the files skimmed.

Checked and found sound, so you need not re-check them:
- `Guid.Empty` can never be a caller scope.
- Mixed tid/oid spellings fail.
- The role comparison is ordinal over both `roles` and `ClaimTypes.Role`.
- `TestAuthHandler` exists only in Server.Tests, and `SourceGuardTests` pins that.
- The reflection endpoint requires authorization.
- The SPA, static assets and Razor Error page are explicitly anonymous.
- A stack token and a deployment token refuse each other.
- A requester who is not the approver cannot deploy someone else's approval without that person's cooperation.

## Critical Issues

### CR-01: Inherited default guardrail row (policy JSON and its author) is readable by every caller, including any personal Microsoft account

**File:** `src/AzureDesignStudio.Services/DeployService.cs:1710-1717, 1828-1840`
**Issue:** `GetGuardrailPolicy` returns `FindEffectiveGuardrailRow(caller.ScopeId)`. When the scope has no row of its own, that is the NULL-tenant default row, and the response carries its `Json`, `UpdatedBy` and `UpdatedAt` unchanged. `CanAdministerGuardrails` is true for every personal account (`AdsCaller.cs:37-38`). On a hosted server with `TenantId: common`, anyone with a free Microsoft account can sign in, call `Deploy/GetGuardrailPolicy`, and read three things:
- the operator's do-not-touch list, which holds resource ids, subscription ids and tag rules;
- the author's display name and object id (`UpdatedBy` is `name <oid>`);
- the update time.

After upgrade this row is the old single global policy, which any authenticated user of the old build could write. Its provenance may therefore be another tenant, so this is a cross-tenant disclosure. An org tenant's `Guardrail.Admin` sees the same data.

**Fix:** Do not return author or timestamp for an inherited row, and say it is inherited.
```csharp
var row = await FindEffectiveGuardrailRow(caller.ScopeId, ct);
response.StatusCode = StatusCodes.Status200OK;
response.Json = row?.Json ?? "";
var inherited = row is not null && row.TenantId is null;
response.UpdatedBy = inherited ? "" : row?.UpdatedBy ?? "";
response.UpdatedAt = inherited ? "" : row?.UpdatedAtUtc.ToString("O") ?? "";
```
If tenants do not need the default's body (they only need to know it applies), return an empty `Json` plus an "inherited default applies" marker field. Also decide whether the pre-upgrade row should be treated as operator data at all, or re-attributed by the operator before multi-tenant exposure. Note that `docs/identity-and-guardrails.md` describes it as "operator-seeded".

## Warnings

### WR-01: Tenant isolation of the what-if gate is opt-in (fail-open default)

**File:** `src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs:106-122, 150-168, 221-232`
**Issue:** The scope check is `scopeId is { } caller && entry.ScopeId != caller`. `scopeId` is an optional `Guid?` that defaults to `null`, and `null` skips the check entirely, in `ValidateAndConsume` (both overloads) and in `ValidateAndConsumeForStack`. The unscoped `TryPeekPlan(planHash, out ...)` overloads (lines 106-122) still return any scope's preview. Production passes the scope today (`DeployService.cs:896, 1140, 1492`), but the isolation guarantee of this phase depends on every future caller remembering an optional argument. An entry issued with `ScopeId = null` (the default of `Issue`) is also consumable by anyone who passes `null`.
**Fix:** Make the scope a required parameter of `Issue`, `IssueForStack`, `ValidateAndConsume*` and `TryPeekPlan`, and delete the unscoped overloads. Keep any unscoped variant `internal` for tests only. At minimum, treat a mismatch of `entry.ScopeId` against `scopeId` as a refusal, including `null` against non-null.

### WR-02: Development crypto provider is refused only when the environment is literally "Production"

**File:** `src/AzureDesignStudio.Server/Services/CryptoProvider.cs:36-40`
**Issue:** `environment.IsProduction()` is the only guard. A host started with `ASPNETCORE_ENVIRONMENT=Staging`, `UAT`, `Prod` or any other name accepts `Crypto:Provider=Development`. That stores linked service-principal secrets under a key ring that is plaintext XML on Linux/macOS, and the dev ring is shared with whatever else runs as that user. Staging hosts commonly hold real subscription links. `CryptoProviderTests.DevelopmentIsAllowedOutsideProduction` pins `Staging` as allowed, so this is deliberate, but it does not match the stated intent ("Development keys never protect production secrets").
**Fix:** Allow it only for `IsDevelopment()`, plus an explicit opt-in such as `Crypto:AllowDevelopmentProvider=true` for test hosts, and update the test and docs.
```csharp
if (!environment.IsDevelopment() && !configuration.GetValue<bool>("Crypto:AllowDevelopmentProvider"))
    throw new InvalidOperationException(...);
```

### WR-03: Token display name is written unsanitised into audit rows, approvals and logs

**File:** `src/AzureDesignStudio.Services/Auth/AdsCaller.cs:21-34` (consumed by `DeployService.cs:915, 1887, 1923, 2055, 2547`)
**Issue:** `Principal` is `name <oid>`, where `name` is the token's `name` claim after `Trim()` only. The claim is user- or tenant-admin-controlled (a personal account sets its own). It may contain:
- control characters, including CR/LF;
- text such as `Alice <11111111-...>`, which yields `Alice <victim-guid> <real-guid>`;
- unpaired surrogates, because `name[..room]` can split a pair.

The text lands in `ApprovedBy`, `UpdatedBy`, `AdsSecurityEvents.Principal`, the `Approved by` UI string and plain-text log lines. The `PrincipalUserId` column is accurate, but every human-readable surface shows the spoofable text. This is the audit-integrity surface the phase added.
**Fix:** Normalise in `Principal`: drop `char.IsControl`, replace `<` and `>` with spaces, collapse whitespace, and cut on a text-element boundary.
```csharp
var clean = new string(name.Where(c => !char.IsControl(c) && c is not ('<' or '>')).ToArray());
```

### WR-04: An approval does not bind the stack policy, so the separation-of-duties rule can be bypassed after approval

**File:** `src/AzureDesignStudio.Services/DeployService.cs:1464-1494, 1383-1388`
**Issue:** Approvals are keyed by (scope, plan hash), where the hash is scope + template + parameters only. `CreateStack` accepts that approval and then checks only that the caller is the recorded requester (`DeployerRefusal`) and that the stack token matches the stack body. Nothing ties the approval to the deny mode, the unmanage actions or the bypass flag. A requester can have approver B approve the hash from a plain What-If (no stack settings visible), then run `StackWhatIf` with `denyWriteAndDelete`, `bypassOutOfSync` or similar and call `CreateStack`. The approver never reviewed those settings, which is exactly what two-person approval is meant to prevent.
**Fix:** Record the stack body hash with the approval, or refuse `CreateStack` unless the approval was produced from a stack preview that carries `body.PlanHash`, for example by storing the stack-plan hash on `PlanApprovalModel` and comparing it in `CreateStack`.

### WR-05: Replace mode lets any tenant administrator (every personal account) discard the operator's inherited rules, and the tenant picks the mode itself

**File:** `src/AzureDesignStudio.Services/DeployService.cs:1731-1757, 1911-1920, 2014-2031`
**Issue:**
- In the default `replace` mode, a scope's first `SetGuardrailPolicy` creates an own row and silently stops inheriting every default rule. The body is not seeded from the default, and nothing warns the admin.
- `SetTenantSettings` lets the same administrator choose `replace`, so the operator default can never be enforced on a tenant that does not want it.
- `merge` is opt-in, and the NULL-tenant row can never be edited or deleted through any RPC. After the upgrade, the legacy global policy becomes immutable in merge mode, and only SQL can remove a stale rule.

For a hosted multi-tenant server the "inherited default" is advisory, which contradicts the wording of the AGENTS.md pitfall and the operator guide.
**Fix:** Make this explicit in the operator doc (default rules bind only tenants that opt into `merge`), or add an operator-only `enforce` flag on the default row. Also have the Set response, or the audit event, say that inherited rules stopped applying when the first own row is created.

### WR-06: The approval's requester is "the newest preview", and approvals are overwritten silently

**File:** `src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs:130-142`; `src/AzureDesignStudio.Services/DeployService.cs:896-934`
**Issue:**
- `TryPeekPlan` returns the newest live preview for a plan hash, so the recorded `RequestedByUserId` is whoever ran What-If last. Any same-scope user who knows the payload can re-run What-If between the real requester's preview and the approval. The approval then names them as the only principal allowed to deploy (`PLAN_REQUESTER_MISMATCH` for the real requester), and the approver was never told the requester changed. That is a hijack or denial of the approval, and it also lets a self-approval-restricted approver launder a requester.
- `ApprovePlan` removes any existing approval for (scope, hash) and writes a new one with no audit event. A second approver silently replaces the first approver's record.

**Fix:** Bind the approval to the token, not the hash. Have the approval request carry the what-if token (or its hash) and take the requester from that exact entry, so a later preview by someone else cannot change it. Append a `PlanApprovalReplaced` security event, or refuse re-approval of an existing approval.

### WR-07: The operator recipe puts the bearer token on the grpcurl command line, contradicting its own rule

**File:** `docs/identity-and-guardrails.md:89-93` and the grpcurl examples
**Issue:** The text says "Never paste a token into a command line", but the examples use `-H "Authorization: Bearer $ADS_TOKEN"`. The shell expands the value before grpcurl starts, so it is visible in `ps` and the process table for the whole call. The doc admits this in a later paragraph and sends the reader to C#. grpcurl has `-expand-headers`, which expands `${NAME}` inside the tool, so the secret never reaches argv.
**Fix:** Use `-expand-headers -H 'Authorization: Bearer ${ADS_TOKEN}'` with single quotes, and drop the "visible in the process list" caveat. If `-expand-headers` is not acceptable, remove the grpcurl example rather than document a leak next to a rule against it.

### WR-08: A blanket `DbUpdateException` retry hides real failures and leaks an unhandled RPC fault

**File:** `src/AzureDesignStudio.Services/DeployService.cs:1893-1901, 2022-2031`
**Issue:** Both Set RPCs catch every `DbUpdateException`, clear the tracker and run the whole upsert once more. The intent is a concurrent-first-write race. A genuine failure (value truncated, the max+1 Id collision between two scopes, connection loss) is retried and then escapes as an unhandled exception. gRPC turns that into `Unknown`, which the client wrapper reads as a transport failure, whereas every other refusal in this phase is an in-band status. The audit-failure path (the event insert fails, so the change is not stored) takes the same exit, with no log line saying an audit write failed.
**Fix:** Wrap the retry so the second failure is caught, logged (`LogError`) and answered as 500/503 in-band. Retry only on a unique-key violation, for example by checking the inner `SqlException.Number` 2601/2627 or SQLite error 19, not on every `DbUpdateException`.

## Info

### IN-01: Dead branch: `PLAN_APPROVER_MISMATCH` cannot occur in production

**File:** `src/AzureDesignStudio.Services/DeployService.cs:960-969`; `docs/identity-and-guardrails.md` response-code table
**Issue:** `DeployerRefusal` falls back to "only the approver may deploy" for an approval with no `RequestedByUserId`. Approvals created after the upgrade always carry a requester (`ApprovePlan` copies it from a scope-matched gate entry, and the service always passes a requester to `Issue`/`IssueForStack`). Approvals that predate it are backfilled into the empty-GUID scope, which no caller can have, so the lookup never finds them. The branch is unreachable, yet the operator guide lists the code as a response the operator may see.
**Fix:** Remove the branch (treat a missing requester as refuse) and the table row, or note that it exists only for hand-seeded rows.

### IN-02: `MapRazorPages().AllowAnonymous()` makes every future Razor page anonymous

**File:** `src/AzureDesignStudio.Server/Program.cs:141`
**Issue:** The deny-by-default fallback policy is defeated for Razor Pages as a class. The only page today is `/Error`.
**Fix:** `builder.Services.AddRazorPages(o => o.Conventions.AllowAnonymousToPage("/Error"))` and map `app.MapRazorPages()` without the blanket call.

### IN-03: Excluding `appsettings*.json` from the precache may break offline cold start of the PWA

**File:** `src/AzureDesignStudio/wwwroot/service-worker.published.js:15`
**Issue:** The worker serves cache first and the network otherwise. With `appsettings.json` no longer cached, an offline cold start must fetch it from the network. I could not confirm from the repo whether the .NET 10 WebAssembly loader treats a failed `appsettings.json` fetch as fatal. If it does, the offline-capable PWA cannot start offline, and it also loses the AzureAd authority and client id. The test pins the exclusion but not the offline behaviour.
**Fix:** Verify an offline cold start in a browser. If it fails, keep the file cached but revalidate it (network first, cache fallback for `appsettings*.json` in `onFetch`).

### IN-04: Dev key-ring directory handling

**File:** `src/AzureDesignStudio.Server/Services/CryptoProvider.cs:44-52, 58-69, 83-91`
**Issue:**
- An existing directory is "left alone" even when it is world-readable or world-writable (for example `Crypto:DevelopmentKeyRingPath=/tmp/keys`). Another local user could read the plaintext key XML or plant a key.
- A relative `DevelopmentKeyRingPath` resolves against the current working directory, so a different launch directory gives a different ring and unreadable secrets.
- The directory is created lazily when the singleton is first resolved, so a bad path fails the first RPC, not startup.

**Fix:** On Unix, check the mode of an existing directory and refuse group/other access. `Path.GetFullPath` the configured value. Resolve the singleton once at startup.

### IN-05: Documentation inaccuracies

**File:** `docs/database-operations.md:206`; `docs/identity-and-guardrails.md:19`
**Issue:**
- database-operations.md says the generated `Down()` methods are "empty for these migrations". That is true for SQL Server only. The SQLite `TenantGuardrails`/`TenantApprovals` `Down()` methods are populated. `TenantApprovals.Down` drops `TenantId` and re-adds a single-column key, which fails if two scopes approved the same hash, and `TenantGuardrails.Down` drops `AdsSecurityEvents` and its audit data.
- identity-and-guardrails.md says roles come from the `roles` claim. `AdsIdentity.TryFrom` also accepts `ClaimTypes.Role`, which is what the inbound claim mapping produces.

**Fix:** Correct both statements, or empty the SQLite `Down()` bodies to match.

### IN-06: `ReleaseDecomWindow` has no role or approval gate, and `ApproveDecomWindow` does not validate `window_id` length

**File:** `src/AzureDesignStudio.Services/DeployService.cs:2840-2900, 2518-2523`
**Issue:**
- `ReleaseDecomWindow` removes window-owned locks for any authenticated caller whose linked credential can delete them. Approving and executing are gated this phase; releasing is not. Please confirm this is intended.
- `ApproveDecomWindow` accepts any non-blank `window_id`, but the column is `StringLength(64)`. On SQL Server a longer value raises an unhandled truncation `DbUpdateException` instead of a 400. This is pre-existing, but the method was rewritten in this phase.

**Fix:** Decide whether release needs `CanApprove`. Reject `window_id.Length > 64` with 400 `DECOM_WINDOW_INVALID`.

---

_Reviewed: 2026-11-14_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
