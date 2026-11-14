# Phase 5: Identity, Guardrails, and Offline Development - Research

**Researched:** 2026-11-14
**Domain:** ASP.NET Core gRPC authn/authz on Entra ID tokens, tenant-scoped EF persistence (SQL Server + SQLite migrations), Data Protection dev crypto, Blazor WASM service-worker precache
**Confidence:** HIGH on current-code facts and token-claim shapes (reproduced locally); MEDIUM on migration mechanics (not yet executed); LOW on two behaviours flagged as Open Questions (self-approval model, Desktop host semantics)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Personal accounts and guardrail administration**
- A personal Microsoft account (MSA) is the administrator of its own single-user tenant scope and can manage its own guardrail policy without an app role; organizational tenants still require the `Guardrail.Admin` app role - **Reversibility:** costly - defines who may change guardrails and shapes the tenant-scoping model that stored rows depend on

**Approval and self-approval**
- Approval requires `Deploy.Approver`, and self-approval is refused as a separate rule; a `Guardrail.Admin` can switch on a per-tenant "allow self-approval" setting (default off) and every change to that setting is audited - **Reversibility:** costly - adds a persisted tenant setting and an audit event type

**Anonymous RPC list**
- Exactly one gRPC method may be called without authentication: `Cost.GetRetailPrices` (public Azure retail prices, as today). Every other method requires authentication; a reflection-based test enumerates all methods and fails on any other unauthenticated one - **Reversibility:** one-way - the anonymous list becomes a public contract that clients rely on

**Locked by the roadmap implementation note**
- New server `Auth/AdsIdentity.TryFrom`: accepts the mapped `oid` claim and the legacy `.../objectidentifier` URI claim through `Guid.TryParse`, validates `tid` the same way, never uses `sub`, never flips `MapInboundClaims`, and answers with an in-band typed 401 rather than throwing `RpcException` (no deferred-save retry loop starts) - **Reversibility:** costly - identity derivation determines which stored rows a user sees
- `AdsPolicies` fallback plus `Guardrail.Admin` and `Deploy.Approver` app roles; a tenant column on the guardrail and approval rows added through Phase 4 migrations, with the legacy `Id=1`/NULL-tenant row retained as the inherited default policy - **Reversibility:** one-way - persisted schema change on user data, applied by migration
- `ICryptoService` with a DPAPI/Data-Protection DEBUG implementation selected by `Crypto:Provider` (Phase 3 left a one-value `KeyVault` seam); Production refuses development keys and still requires Key Vault; exclude `appsettings*.json` from the service-worker precache so role/scope changes are not sticky

**Decided autonomously**
- [Identity] Q: "Where do identity and policy classes live" -> Selected: "`src/AzureDesignStudio.Server/Auth/` for AdsIdentity and the policy registration, with pure parsing kept free of ASP.NET types so it is unit-testable" - file layout is a local refactor
- [Identity] Q: "How is the 401 shaped in-band" -> Selected: "reuse the existing in-band response/status convention of the services (typed status in the response message), no new proto field unless the existing responses cannot carry it" - internal choice, proto change would be additive
- [Crypto] Q: "DEBUG crypto implementation key storage" -> Selected: "ASP.NET Core Data Protection with a file-system key ring under the dev user profile, selected only when `Crypto:Provider` is the development value and the environment is not Production" - reversible, behind the Phase 3 seam
- [Tenancy] Q: "How a request's tenant reaches the services" -> Selected: "through the same `UserState` caller mechanism (`ServiceTools.UserStateKey`) extended with the tenant id" - internal plumbing
- [Testing] Q: "How are tenant isolation and role checks tested" -> Selected: "Server.Tests with the Phase 3 `AdsServerFactory` and an extended test auth handler that can emit chosen claims" - test-local

### Claude's Discretion
None stated in CONTEXT.md beyond the "Decided autonomously" items above (which are revisable; see Open Question 1 for the one that this research recommends changing: where `AdsIdentity` lives).

### Deferred Ideas (OUT OF SCOPE)
None - discussion stayed within phase scope. (Phase 2 was skipped and is out of scope.)
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| IDA-01 | Missing/malformed `oid`/`tid` -> typed 401, never a 500 | `ServiceTools.GetUserId` already returns null for non-GUID oid and every RPC answers 401 in-band; `tid` is not read anywhere. Add one typed `AdsIdentity` that requires both; reject `Guid.Empty`; keep in-band (client treats `RpcException(Unauthenticated)` as "retry later") |
| IDA-02 | Legacy `.../objectidentifier` URI claim still reads existing designs/subscriptions/quota | Production host maps inbound claims (verified): `oid` arrives ONLY as the URI form. Existing rows are keyed by `UserId` Guid from that claim; do not add tenant to those keys |
| IDA-03 | Deny-by-default RPCs, reflection test, explicit anonymous list | Today: no `FallbackPolicy`, `Cost/GetRetailPrices` is open only because it carries no metadata (verified). `EndpointDataSource` + `GrpcMethodMetadata` enumerates all 21 endpoints |
| IDA-04 | Only `Guardrail.Admin` reads/mutates guardrail policy; others 403 | Guardrail policy is ONE global `Id=1` row any authenticated user can overwrite today. Role check belongs in the shared service body (Desktop runs it in-process); method-level `[Authorize]` on overrides is also honoured by the host (verified) |
| IDA-05 | Guardrail policies and approvals tenant-scoped | Add scope id to `AdsGuardrailPolicies` (nullable, NULL = inherited default), `AdsPlanApprovals`, `AdsDecomApprovals`; approval PK is `PlanHash` alone today and `ApprovePlan` removes-and-replaces any existing row (cross-caller overwrite) |
| IDA-06 | `Deploy.Approver` to approve; self-approval a separate rule | The WhatIf gate records no requester and deploy currently REQUIRES approver == deployer; a requester must be recorded to make "self-approval" meaningful (Open Question 2) |
| CRED-01 | Register/exercise a subscription in DEBUG with no Key Vault/Azure creds | `DevelopmentCryptoService` over ASP.NET Core Data Protection (in the shared framework, no new package) registered by `CryptoProvider` for `Crypto:Provider=Development` when environment is not Production |
| CRED-02 | Production refuses dev keys, still requires Key Vault | `CryptoProvider.Register` gains the host environment and throws at startup; Key Vault service stays lazy (boot without a vault must keep working for `Database__Provider=InMemory` smoke runs) |
</phase_requirements>

## Summary

Most of IDA-01/IDA-02 is already half-true in the shipped code, which changes the shape of the work. `ServiceTools.GetUserId` already accepts the URI claim and `oid`, runs `Guid.TryParse`, and every one of its 19 call sites (15 in DeployService, 4 in DesignService) answers a typed 401 in-band. What is missing is (a) `tid`, (b) a single typed identity that carries user, tenant scope and roles to the services, (c) deny-by-default (there is no `FallbackPolicy`), (d) any role or tenant logic, and (e) the dev crypto provider. The work is therefore a controlled generalisation of an existing in-band contract plus a persisted-schema change, not a greenfield auth layer.

Three findings drive the plan and are not in the roadmap note. First, **personal Microsoft accounts all share one `tid`** (`9188040d-6c67-4c5b-b112-36a304b66dad`), so "tenant" cannot simply be `tid`: the tenant scope key must be `tid` for organisational tokens and the user's own `oid` for personal ones, otherwise every MSA user on a server would share one guardrail policy and one approval namespace. Second, **the shared service classes also run inside the Desktop app** (single user, MSAL principal with `oid`/`tid` and no role claims), so role and self-approval checks cannot assume Entra app roles unless the host can declare "single-user host"; a service-body check plus a host-set UserState marker is the only design that keeps the primary target product working. Third, **today an approval can only be consumed by the same principal that created it** (`ApprovedByUserId == caller`), and the WhatIf token gate records no requester, so "self-approval refused" is undefined until a requester is recorded; with the decided default-off setting, the existing one-click approve-then-deploy UI is blocked on a hosted server until a Guardrail.Admin enables self-approval (Open Question 2).

**Primary recommendation:** Put a pure `AdsIdentity.TryFrom(ClaimsPrincipal)` in the host-neutral Services project (the services and Desktop need it), have both the server `CallerInterceptor` path and the Desktop invoker feed the services through `UserState`, enforce roles/tenant/self-approval in the service bodies with in-band 401/403, enforce deny-by-default with a gRPC-endpoint convention plus a `FallbackPolicy`, add the tenant column via hand-guarded SQL Server and EF-generated SQLite migrations with composite approval keys, and add `Crypto:Provider=Development` as a Data-Protection-backed implementation refused in Production.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Token validation, claim mapping | API / Backend (Server, JwtBearer + Microsoft.Identity.Web) | - | Signature, issuer, audience, scope are validated before any service code; claim mapping is a host setting (left at default) |
| Claim -> typed identity (oid, tid, scope key, roles) | API / Backend, but code in host-neutral Services | Desktop host builds the principal | Services (shared with Desktop) must read tenant and roles; ARCHITECTURE.md's "Server/Auth" placement cannot be read by `DeployService` (see Open Question 1) |
| Deny-by-default for RPCs | API / Backend (endpoint conventions + `FallbackPolicy`) | Test project (reflection guard) | Authorization metadata lives on endpoints; Desktop has no endpoints |
| Role checks (`Guardrail.Admin`, `Deploy.Approver`), self-approval, tenant scoping | Service bodies (Services project) | Server registers policy names only for the fallback | One implementation serves Server and Desktop; in-band 403 matches the project's response contract |
| Tenant-scoped persistence, tenant settings, audit | Database / Storage (SQL Server prod, SQLite deploy.db) | - | Schema change by migration on both contexts |
| What-if requester/scope binding | API / Backend (in-memory `WhatIfTokenGate`) | - | The gate is the only place a preview's author is known |
| DEBUG crypto | API / Backend (`CryptoProvider` + `DevelopmentCryptoService`) | - | Behind the Phase 3 `ICryptoService` seam; Desktop keeps `CredentialStoreSecretProtector` |
| Service-worker precache exclusion | Browser / Client (WASM `service-worker.published.js`) | CDN/Static | `offlineAssetsExclude` regex list is evaluated in the browser |
| App-role definitions, role assignment | Entra (manual / docs) | - | App registration manifest `appRoles`; documentation task in `docs/entra-setup.md` |

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.Identity.Web | 3.15.1 (already pinned) [VERIFIED: src/Directory.Packages.props:54 `<PackageVersion Include="Microsoft.Identity.Web" Version="3.15.1" />`] | JwtBearer + Entra validation, `[RequiredScope]` | Already in use; do not change claim mapping |
| Grpc.AspNetCore | 2.66.0 (already pinned) [VERIFIED: src/Directory.Packages.props:19] | gRPC endpoints, `GrpcMethodMetadata` for the reflection test | Already in use |
| Microsoft.AspNetCore.DataProtection | in the `Microsoft.AspNetCore.App` shared framework, 10.0.14 on this machine [VERIFIED: `ls` of the shared framework directory lists `Microsoft.AspNetCore.DataProtection.dll`, `...Abstractions.dll`, `...Extensions.dll`] | DEBUG `ICryptoService` key ring | Server is `Microsoft.NET.Sdk.Web`; no package reference, no lock-file change |
| Microsoft.EntityFrameworkCore.* | 10.0.12 (already pinned) [VERIFIED: src/Directory.Packages.props:39-44] | Migrations on both contexts | Phase 4 pattern |
| dotnet-ef | 10.0.13 installed [VERIFIED: `dotnet ef --version`] | Generate migration/designer/snapshot | Phase 4 plans generated Baseline this way |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.AspNetCore.Mvc.Testing | already in Server.Tests | `AdsServerFactory` | All new server tests |
| xUnit | already in Server.Tests / Core.Tests | Tests | All new tests |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Service-body role checks (in-band 403) | Named `[Authorize(Policy=...)]` role policies on Hosted overrides | Policy attributes on overrides DO reach endpoint metadata (verified), but they answer `PermissionDenied`, cannot express the MSA rule or self-approval without DB access, and do not exist on Desktop. Use only as optional defence in depth; one source of truth stays in the service |
| Global `FallbackPolicy` | `RequireAuthorization()` on each `MapGrpcService` | Fallback is true deny-by-default (covers Phase 8 endpoints) but requires `AllowAnonymous()` on `MapStaticAssets`, `MapRazorPages`, `MapFallbackToFile`, reflection (already `RequireAuthorization`) and later health. Recommended: do both - gRPC convention helper AND fallback |
| Composite approval PK `(TenantId, PlanHash)` | Keep `PlanHash` PK, refuse when an existing row belongs to another scope | Refusal leaks existence and lets one scope block another's identical hash; composite key is the clean fix but needs a PK rebuild on SQLite and a dynamic-name PK drop on SQL Server |

**Installation:** no new packages. `[VERIFIED: Data Protection is in the shared framework; probe above]`

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| (none added) | - | - | - | - | n/a | No external package is installed by this phase |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

Implementation must not add a NuGet package (DPAPI/Data Protection ship in the framework). If planning decides otherwise, run the legitimacy gate first.

## Architecture Patterns

### System Architecture Diagram

```
Browser (WASM, MSAL)          Desktop (MAUI, MSAL account / az CLI fallback)
   | Bearer access token            | DesktopIdentity.GetAsync() -> ClaimsPrincipal(oid, tid; NO roles)
   v                                v
Server: UseAuthentication           InProcessCallInvoker (creates InProcessServerCallContext,
 JwtBearer+Identity.Web              UserState["ads.user"] = principal)  + NEW host marker
 MapInboundClaims=true                |
 -> principal with URI claim types    |
   |                                  |
   v                                  |
UseAuthorization                      |
 - FallbackPolicy = authenticated     |
 - [Authorize]+[RequiredScope] Hosted |
 - Cost endpoint .AllowAnonymous()    |
   |                                  |
   v                                  |
CallerInterceptor: UserState["ads.user"] = HttpContext.User
   |                                  |
   +-------------+--------------------+
                 v
   Services (shared, host-neutral): ServiceTools.TryGetCaller(context)
     AdsIdentity.TryFrom(principal) --fail--> in-band 401 (StatusCode field, no RpcException)
        UserId  (oid | URI claim, Guid, not empty)
        TenantId(tid | tenantid URI, Guid, not empty)
        ScopeId = TenantId (org)  |  UserId (tid == consumer tenant)   <- tenant scope key
        Roles   (roles | ClaimTypes.Role)  + host marker SingleUser
        CanAdministerGuardrails = Guardrail.Admin | personal | SingleUser
        CanApprove               = Deploy.Approver | personal | SingleUser
                 |
   +-------------+------------------------------+----------------------------+
   v             v                              v                            v
 Design RPCs   Deploy: Get/SetGuardrailPolicy   Deploy: WhatIf/StackWhatIf    Deploy: ApprovePlan / ApproveDecomWindow
 (UserId only) 403 unless CanAdminister;        records RequestedBy +         403 unless CanApprove;
               row by ScopeId, fallback to      ScopeId in WhatIfTokenGate;   self-approval rule (requester==caller &&
               NULL-scope Id=1 row on read      policy resolved by ScopeId    !tenant setting) -> 403 separate code;
               Set writes tenant row + audit                                  row keyed (ScopeId, hash)
                 |                                                                   |
                 v                                                                   v
   AdsGuardrailPolicies (Id=1 NULL scope = default)            AdsPlanApprovals / AdsDecomApprovals (TenantId in PK)
   AdsTenantSettings (AllowSelfApproval)                       AdsSecurityEvents (append-only audit)
```

### Recommended Project Structure

```
src/AzureDesignStudio.Services/
├── Auth/AdsIdentity.cs            # pure: TryFrom(ClaimsPrincipal), ScopeId, Roles, AdsRoles consts, ConsumerTenantId
├── Auth/AdsCaller.cs              # AdsIdentity + host marker (SingleUser); AdsAuthorization rules (pure functions)
├── ServiceTools.cs                # + TryGetCaller(ServerCallContext, out AdsCaller); GetUserId kept as a thin wrapper until removed
├── Models/DesignDbContext.cs      # + TenantId columns, AdsTenantSettings, AdsSecurityEvents; composite keys
├── Migrations/Sqlite/             # + TenantScope migration (EF-generated: table rebuild for the PK change)
src/AzureDesignStudio.Server/
├── Auth/AdsPolicies.cs            # policy names, FallbackPolicy registration, MapAdsGrpcService<T>(anonymous) helper
├── Services/CryptoProvider.cs     # + Development value, IHostEnvironment parameter
├── Services/DevelopmentCryptoService.cs
├── Persistence/Migrations/        # + TenantScope migration (hand-guarded T-SQL Up, EF snapshot/designer)
src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs   # Entry gains RequestedByUserId + ScopeId (additive optional params)
src/AzureDesignStudio.Server.Tests/Hosting/   # TestAuthHandler claims header, AuthorizationSurfaceTests, IdentityTests, CryptoProvider tests
src/AzureDesignStudio/wwwroot/service-worker.published.js       # offlineAssetsExclude += appsettings
docs/entra-setup.md            # appRoles manifest section, role assignment, "sign out and back in"
```

### Pattern 1: One typed identity, in-band failure

**What:** Replace the 19 hand-rolled call sites of `GetUserId` + `new Guid(...)` blocks with one call that returns user, scope and capabilities, or a reason that becomes the existing `status_code = 401` answer.
**When to use:** Every Design and Deploy RPC (`GetCredentialAsync`/`new Guid(userIdClaim)` sites included).
**Example:**
```csharp
// Source: derived from the current helper, src/AzureDesignStudio.Services/ServiceTools.cs:9-40 [VERIFIED: read this session]
// Current code, verbatim:
//   private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
//   var value = user.FindFirst(ObjectIdClaim)?.Value ?? user.FindFirst("oid")?.Value;
//   return Guid.TryParse(value, out _) ? value : null;
public static bool TryFrom(ClaimsPrincipal user, out AdsIdentity identity)
{
    // Claim types by source (all verified, see "Token claim shapes"):
    //   Server (MapInboundClaims=true): .../identity/claims/objectidentifier, .../identity/claims/tenantid, ClaimTypes.Role
    //   Desktop (BuildPrincipal / FromAccessToken): "oid", "tid"; no roles
    var oid = Pick(user, OidUri, "oid");          // never ClaimTypes.NameIdentifier (that is `sub`)
    var tid = Pick(user, TidUri, "tid");
    if (!Guid.TryParse(oid, out var userId) || userId == Guid.Empty ||
        !Guid.TryParse(tid, out var tenantId) || tenantId == Guid.Empty)
    { identity = default!; return false; }
    // If both spellings are present and disagree, refuse (ambiguous), do not pick one.
    var scope = tenantId == ConsumerTenantId ? userId : tenantId;   // MSA: single-user scope
    ...
}
```

### Pattern 2: Deny-by-default endpoint surface

**What:** Every gRPC endpoint gets `RequireAuthorization()` through one registration helper; `Cost` is the single `AllowAnonymous()`; a `FallbackPolicy` backs it up; static/Razor/fallback endpoints are explicitly anonymous.
**Example (reflection test core, reproduced this session on the real host):**
```csharp
// Probe output on the unmodified host [VERIFIED: ran this session, 21 gRPC endpoints]:
//   gRPC - /Design/Save|GetSaved|Load|Delete          auth=[(default)] anon=False
//   gRPC - /Deploy/SaveSubscriptionInfo ... /Deploy/ReleaseDecomWindow (16 methods) auth=[(default)] anon=False
//   gRPC - /Cost/GetRetailPrices                      auth=[] anon=False      <- open only by absence
//   FallbackPolicy null? True
var ds = factory.Services.GetRequiredService<EndpointDataSource>();
var grpc = ds.Endpoints.Where(e => e.Metadata.GetMetadata<GrpcMethodMetadata>() != null);
foreach (var e in grpc)
{
    var protectedByMetadata = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();
    var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() != null;
    // pass: protectedByMetadata && !anonymous  OR  e.DisplayName in AnonymousList (and anonymous)
}
// Also assert AuthorizationOptions.FallbackPolicy is not null, and that every service descriptor in the
// SharedModels assembly has at least one mapped endpoint or is on an explicit "not exposed" list.
```

### Pattern 3: Migration shape for the tenant column

**What:** One new migration per context, applied after Phase 4's backup-before-migrate (SQLite deploy.db backup is automatic via `DeployDbStartup`; SQL Server relies on the documented backup + `Database:Migrate`).

| Table | Change | Legacy rows |
|-------|--------|-------------|
| `AdsGuardrailPolicies` | add `TenantId uniqueidentifier NULL`; unique filtered index on `TenantId WHERE TenantId IS NOT NULL` (SQL Server filter / SQLite partial index) | `Id=1` row keeps `TenantId = NULL` = inherited default; no data touched |
| `AdsPlanApprovals` | add `TenantId NOT NULL`, change PK to `(TenantId, PlanHash)`; add `RequestedByUserId nvarchar(64) NULL` | existing rows backfilled with `Guid.Empty` (never matches a real scope = pending legacy approvals must be re-approved; fail closed) |
| `AdsDecomApprovals` | same: `TenantId NOT NULL`, PK `(TenantId, ExecutionHash)` | backfilled `Guid.Empty` |
| `AdsTenantSettings` (new) | `TenantId` PK, `AllowSelfApproval bit NOT NULL` (default false in code, not in DDL), `UpdatedBy`, `UpdatedByUserId`, `UpdatedAtUtc` | none |
| `AdsSecurityEvents` (new, append-only) | `Id uniqueidentifier` PK, `TenantId`, `EventType nvarchar(32)`, `Principal`, `PrincipalUserId`, `Detail`, `AtUtc` | none |

SQL Server `Up` must follow the Baseline convention (guarded T-SQL via `migrationBuilder.Sql`, `COL_LENGTH`/`OBJECT_ID` guards) and must NOT leave a model-unknown default constraint behind (that is exactly the drift `NormaliseLegacyDefaults` had to remove). Pattern: `ADD ... NULL`, `UPDATE ... SET TenantId = '00000000-0000-0000-0000-000000000000'`, `ALTER COLUMN ... NOT NULL`, then drop the PK by looking its name up in `sys.key_constraints` (a database that shipped before migrations may carry a system-generated PK name; the Baseline comment says existing tables "keep whatever names they were created with"), then `ADD CONSTRAINT PK_AdsPlanApprovals PRIMARY KEY (TenantId, PlanHash)`. [ASSUMED: T-SQL sequence not executed this session; Phase 4's `RunSqlServerTests.sh` container harness exists to prove it, and Docker is available.]

SQLite Server (`Database:Provider=Sqlite|InMemory`) has NO migrations and uses `EnsureCreated` (`DatabaseBootstrapper.Initialize`), so model changes need no migration there. The Desktop `deploy.db` migrates through `SqliteDesignDbContext` migrations under `src/AzureDesignStudio.Services/Migrations/Sqlite` and a premigration backup; a PK change on SQLite is a table rebuild, which `dotnet ef migrations add` generates. Existing tests that must be updated: `SqlServerMigrationModelTests` and `SqlServerMigrationTests` hold hard-coded table lists; `DeployDbMigrationTests` seeds `AdsPlanApprovals (PlanHash, ...)` rows without a tenant.

**`UniqueIndexGuard` applicability:** it is a SQLite-only dedupe for the Workspace database (`Specs` lists `Designs`, `ChangeFeed`), run before `CREATE UNIQUE INDEX`. It does NOT apply here: the new unique index is on a brand-new nullable column (all NULL, no duplicates possible), and the new composite primary keys are supersets of keys that were already unique. No dedupe spec row is needed. [VERIFIED: read UniqueIndexGuard.cs:1-80]

### Anti-Patterns to Avoid
- **Reading `sub` or `ClaimTypes.NameIdentifier` as the user id:** the probe shows `sub` arrives as `.../claims/nameidentifier`; using it silently gives every user an empty design list.
- **`tid` as the tenant key for personal accounts:** all MSA users would share one policy and one approval namespace.
- **Adding tenant to design/subscription keys:** designs, `AzureSubscriptions` and the 10-design quota are keyed by `UserId` only; leave them so IDA-02 holds. `AzureSubscriptionModel.TenantId` is the Azure service principal's tenant, NOT the caller's scope - do not reuse or rename it.
- **Returning `RpcException(Unauthenticated)` for a malformed claim:** `DesignGrpcService.IsDeferredSaveFailure` treats it as "keep offline and retry", so a permanently bad token would queue saves forever.
- **`#if DEBUG` in `Program.cs`:** `SourceGuardTests.ProgramHasNoPreprocessorConditionals` forbids it; select the dev provider by configuration + environment.
- **Role claims injected into the Desktop principal as a shortcut:** hosts must mark themselves via `UserState`, not via claim types a token could carry.
- **Global `FallbackPolicy` without anonymous exceptions:** it applies to endpoint-based static assets and the SPA fallback; Blazor WASM index/boot files would 401.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Encrypting stored secrets in DEBUG | Custom AES/DPAPI wrapper | ASP.NET Core Data Protection (`IDataProtectionProvider`, file-system key ring) | Key rotation, authenticated encryption, purpose strings; already in the shared framework |
| Listing every RPC for IDA-03 | A hand-maintained method list | `EndpointDataSource` + `GrpcMethodMetadata` (reproduced) plus service-descriptor reflection for unmapped services | A new method appears automatically |
| JWT claim parsing | Manual base64 decode on the server | The validated `ClaimsPrincipal` from JwtBearer | Signature/issuer/audience already enforced; the Desktop CLI path is the only unverified decode (local token) |
| Role evaluation | `string.Contains` on a roles blob | `ClaimsPrincipal.IsInRole` + a second read of `roles`; one pure rules class | `IsInRole` works for the mapped role claim (verified) |
| Migration of PK/columns | Ad-hoc `ALTER` at startup | EF migrations (SQL Server guarded T-SQL, SQLite generated) | Phase 4 forbids startup DDL; `SourceGuardTests` enforce one migration site |

**Key insight:** the dangerous part of this phase is not parsing a claim; it is that three hosts (hosted server, hosted-then-WASM client, in-process Desktop) share the same service code with different identity sources and different trust models, so every capability decision must be made from one typed value the host cannot get wrong.

## Runtime State Inventory

Not a rename/refactor phase, but the schema change touches persisted state; the equivalent inventory:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | SQL Server `ads-main`: `AdsGuardrailPolicies` (maybe the single `Id=1` row), `AdsPlanApprovals`, `AdsDecomApprovals` (short-lived but unexpired rows may exist), `AdsDesigns`/`AzureSubscriptions` keyed by `UserId` Guid. Desktop `deploy.db`: same tables | Data migration: backfill approvals with `Guid.Empty` tenant; guardrail row unchanged; designs/links untouched |
| Live service config | Entra app registration (`appRoles` not yet defined; `docs/entra-setup.md` has no roles section) | Manual + docs: add `Guardrail.Admin`, `Deploy.Approver` app roles to the server API registration, assign per tenant in Enterprise applications |
| OS-registered state | None for this phase. Dev key ring directory is created on first DEBUG use | None |
| Secrets/env vars | `Crypto__Provider` (new accepted value), `KeyVaultName`/`KeyVaultKeyName` unchanged | Add `Crypto__Provider=Development` to the `AzureDesignStudio.Server` launch profile only (Development env); never to the Docker profile |
| Build artifacts | `src/AzureDesignStudio/obj/.../service-worker-assets.js` is regenerated on build; new `Server`/`Services` migration files; `packages.lock.json` should not change (no package) | Revert SDK 10.0.403 lock-file drift (`git checkout -- '*packages.lock.json'`) before committing |

## Common Pitfalls

### Pitfall 1: Personal accounts share one tenant
**What goes wrong:** Scope keyed by `tid` puts every MSA user in `9188040d-6c67-4c5b-b112-36a304b66dad`; one MSA user rewrites the do-not-touch list for all of them.
**Why:** The consumer tenant id is a constant for all personal accounts [CITED: https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference - "For sign-ins to the personal Microsoft account tenant ... the value is `9188040d-6c67-4c5b-b112-36a304b66dad`"].
**Avoid:** `ScopeId = (tid == ConsumerTenantId) ? oid : tid`; test with two MSA callers.
**Warning signs:** Two personal accounts reading each other's guardrail JSON.

### Pitfall 2: Desktop has no roles
**What goes wrong:** Role checks in the shared `DeployService` lock the Desktop user out of their own guardrails and approvals (MSAL `BuildPrincipal` yields only `ClaimTypes.Name`, `preferred_username`, `oid`, `tid`) [VERIFIED: MsalAccountService.cs:256-268 and DesktopIdentity.cs:83-112].
**Avoid:** host marker in `UserState` (Desktop invoker sets "single-user host"); roles and self-approval implied for that host. Test through `InProcessTests`.

### Pitfall 3: Approver == deployer is enforced today
**What goes wrong:** Adding "self-approval refused" without a requester and without relaxing `PLAN_APPROVER_MISMATCH` makes every deploy impossible; adding it without recording the requester makes the rule vacuous.
**Evidence:** `if (!string.Equals(approval.ApprovedByUserId, userIdClaim, StringComparison.Ordinal))` -> `PLAN_APPROVER_MISMATCH` at DeployService.cs:1056, :1418 and (decom) :2366 [VERIFIED: grep this session]. The gate's `Entry` record has no user field [VERIFIED: WhatIfTokenGate.cs:47-49].
**Avoid:** Open Question 2 decision before planning tasks.

### Pitfall 4: Roles are not retroactive
**What goes wrong:** A freshly assigned `Guardrail.Admin` still gets 403 until a new access token is issued; group-based assignment may never emit `roles` [CITED: .planning/research/PITFALLS.md "App roles are not retroactive" summarising Microsoft docs]. The 403 message should name the missing role; docs say "sign out and back in".

### Pitfall 5: Guid comparison drift
**What goes wrong:** `ApprovedByUserId` is stored as the raw claim text and compared ordinally. If the new code normalises (`Guid.ToString("D")`) while a stored value or a claim has other casing/format (`Guid.TryParse` accepts `N`, braces, uppercase), approvals stop matching.
**Avoid:** compare parsed `Guid`s (parse both sides) for identity comparisons; store `ToString("D")` going forward; reject `Guid.Empty`.

### Pitfall 6: FallbackPolicy breaks the SPA
**What goes wrong:** With a fallback requiring authentication, `MapStaticAssets()`, `MapRazorPages()`, `MapFallbackToFile("index.html")` answer 401 and the WASM app never boots. Static-files middleware that runs before authorization is unaffected, endpoint-based assets are not [CITED: https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies - "Static files served by static file middleware before the authorization middleware aren't protected by the fallback policy"; endpoint behaviour for MapStaticAssets is ASSUMED].
**Avoid:** `.AllowAnonymous()` on those three plus a host test hitting `/` and `/_framework/blazor.boot.json`-style path anonymously; Phase 8 health will do the same.

### Pitfall 7: Existing tests lack `tid`
`TestServerCallContext.ForUser` and `TestAuthHandler` emit only the object-id claim [VERIFIED: TestSupport.cs:31-38, TestAuthHandler.cs:34-40]. Requiring `tid` fails every existing Design/Deploy test. Change the two helpers once (default fixed tenant, optional roles/extra claims, "no tid" variant) in the first plan.

### Pitfall 8: Decom approvals are approvals too
`ApproveDecomWindow`/`ExecuteDecomWindow` use `AdsDecomApprovals` keyed by `ExecutionHash`. Leaving them unscoped leaves the same overwrite hole IDA-05 closes for plans. Scope them in the same migration.

### Pitfall 9: Service worker caches config cache-first
`appsettings.json` is in the precache manifest and matches `offlineAssetsInclude` `/\.json$/`; `onFetch` serves cache first [VERIFIED: obj/Release/net10.0/service-worker/service-worker-assets.js.publish contains `"url": "appsettings.json"`; service-worker.published.js:14-15, 22]. Add `/^appsettings(\..+)?\.json$/` to `offlineAssetsExclude` (the existing entry `/^service-worker\.js$/` shows urls are relative with no leading slash). The exclusion is in the browser; guard it with a Node or xUnit source test that reads the file (existing JsTests directory runs under `node --test`). Existing browsers keep their old cache until the next service-worker version activates - document it.

## Code Examples

### Token claim shapes (reproduced)

```
// Probe: Microsoft.Identity.Web 3.15.1 + JwtBearer, same registration as Program.cs lines 29-35.
// Output (this session):
MapInboundClaims=True UseSecurityTokenValidators=False
RoleClaimType=http://schemas.microsoft.com/ws/2008/06/identity/claims/role NameClaimType=name
// token claims in: oid, tid, roles=Guardrail.Admin, scp=Server.Access, name, sub
// claims out:
http://schemas.microsoft.com/identity/claims/objectidentifier = <oid>
http://schemas.microsoft.com/identity/claims/tenantid         = <tid>
http://schemas.microsoft.com/ws/2008/06/identity/claims/role  = Guardrail.Admin
http://schemas.microsoft.com/identity/claims/scope            = Server.Access
name = T
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier = <sub>
IsInRole(Guardrail.Admin)=True
```
[VERIFIED: ran a file-based probe with the pinned package versions; the probe lives only in the session scratchpad.] Consequence: on the hosted server `oid` is only ever the URI claim and `tid` only the `tenantid` URI claim; accept the short names too (Desktop and a future `MapInboundClaims=false`), read roles from both `ClaimTypes.Role` and `"roles"`.

### Dev crypto provider

```csharp
// CryptoProvider.cs today (verbatim): public const string Key = "Crypto:Provider"; public const string KeyVault = "KeyVault";
// [VERIFIED: CryptoProvider.cs:12-13]
public static void Register(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
{
    var value = configuration[Key]?.Trim();
    if (string.IsNullOrEmpty(value) || value.Equals(KeyVault, StringComparison.OrdinalIgnoreCase))
    { services.AddSingleton<ICryptoService, CryptoService>(); return; }          // lazy vault, unchanged

    if (value.Equals(Development, StringComparison.OrdinalIgnoreCase))
    {
        if (environment.IsProduction())      // CRED-02: refuse before anything is registered
            throw new InvalidOperationException($"{Key} is \"{Development}\", which is never allowed in the Production environment. Use {KeyVault}.");
        services.AddDataProtection().SetApplicationName("AzureDesignStudio.Dev")
                .PersistKeysToFileSystem(new DirectoryInfo(DevKeyRingPath(configuration)));
        services.AddSingleton<ICryptoService, DevelopmentCryptoService>();       // purpose "AzureDesignStudio.SubscriptionSecret.v1"
        return;
    }
    throw new InvalidOperationException($"{Key} is \"{value}\", which is not a crypto provider. Accepted values: {KeyVault}, {Development}.");
}
```
The existing `CryptoProviderTests` assert an exact error text and call `Register(services, configuration)` with two arguments; they need updating together. Stored dev ciphertext should carry a prefix (for example `dev1:`) so a Key Vault `Decrypt` of a dev blob (or the reverse) fails into the existing "unreadable link -> 404, link again" path instead of a silent mis-decrypt. [ASSUMED: prefix design; existing catch covers `FormatException`/`CryptographicException`, VERIFIED: DeployService.cs:210-216.]

### Self-approval as two independent rules

```csharp
// ApprovePlan, after identity + hash validation
if (!caller.CanApprove)                                  // Deploy.Approver | personal scope | single-user host
    return Deny(403, "PLAN_APPROVER_ROLE_REQUIRED: the Deploy.Approver app role is required to approve a plan.");
var settings = await tenantSettings.GetAsync(caller.ScopeId, ct);
if (peek.RequestedByUserId == caller.UserId && !(settings.AllowSelfApproval || caller.IsSingleUserHost))
    return Deny(403, "PLAN_SELF_APPROVAL_REFUSED: the requester cannot approve their own plan; a Guardrail.Admin can allow self-approval for this tenant.");
```

### Setting change is audited atomically

```csharp
settings.AllowSelfApproval = request.AllowSelfApproval;
db.AdsSecurityEvents.Add(new SecurityEvent(scope, "SelfApprovalChanged", principal, userId, $"{old} -> {new}", UtcNow));
await db.SaveChangesAsync(ct);   // one SaveChanges: setting and audit row commit together or not at all
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Attribute-only `[Authorize]` per class | `FallbackPolicy` + explicit `AllowAnonymous` | ASP.NET Core 3+/docs "Require global user authentication" | Secure by default; new endpoints protected without remembering attributes [CITED: learn.microsoft.com policies page] |
| `JwtSecurityTokenHandler` | `JsonWebTokenHandler` (`UseSecurityTokenValidators=False`) | .NET 8 | Still maps inbound claims by default (verified `MapInboundClaims=True`) |

**Deprecated/outdated:** the roadmap/ARCHITECTURE phrase "MapInboundClaims pinned explicitly" and "Current ... FallbackPolicy = authenticated" describe a target, not the code: `Program.cs` sets neither [VERIFIED: Program.cs:29-35 and probe `FallbackPolicy null? True`]. Do not "pin" by flipping it; leave it at the default and test the mapped shape.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Personal-account access tokens for this API carry `oid` and `tid` (the app already keys designs on `oid`, so existing MSA users imply yes), and MSAL `HomeAccountId.ObjectId` for MSA parses as a Guid | Summary, Pattern 1 | MSA users get 401 everywhere. Mitigation: first plan includes a manual check with a real MSA token; docs note `oid` can be absent when the service principal is missing from the tenant (Microsoft Q&A, unverified) |
| A2 | `tid == 9188040d-...` can only be issued for a consumer sign-in, so using it to grant single-user admin cannot be spoofed by an org tenant | Pitfall 1, Security | A forged scope grants admin; relies on JwtBearer issuer validation staying enabled. Do not set `ValidateIssuer=false` |
| A3 | MSA users should ALSO imply `Deploy.Approver` (decision text only covers guardrail admin) | Open Question 2 | If not, MSA users cannot approve and so cannot deploy on a hosted server |
| A4 | The Desktop host should treat roles and self-approval as implied (single-user local app) | Pitfall 2, Open Question 2 | If wrong, the primary product cannot approve its own deploys |
| A5 | Composite approval keys and a guarded T-SQL PK rebuild work on legacy and fresh SQL Server databases (not executed) | Pattern 3 | Migration fails or schema drifts; proven by the Phase 4 container harness (`RunSqlServerTests.sh`) |
| A6 | `MapStaticAssets`/Razor/`MapFallbackToFile` are subject to `FallbackPolicy` | Pitfall 6 | If not, extra `AllowAnonymous()` calls are harmless |
| A7 | Dev ciphertext prefix and "refuse dev provider when a real vault name is configured" are acceptable extras | Code Examples | Extra friction for developers with a user-secrets vault; drop the second tripwire if unwanted |
| A8 | Decom approvals need tenant scope and `Deploy.Approver`, but not the separate self-approval rule (no requester exists for decom) | Pitfall 8, Open Question 3 | Scope creep vs. leaving a hole |
| A9 | A non-GUID-case difference in stored `ApprovedByUserId` is unlikely (Entra emits lowercase `D` format) | Pitfall 5 | Stored legacy approvals fail to match; mitigated by parsing both sides |

## Open Questions (RESOLVED)

> All five questions were answered in 05-CONTEXT.md ("Added after research" and the Decided autonomously block) on 2026-11-14.

1. **Where does `AdsIdentity` live?** RESOLVED: AdsIdentity in Services, per the CONTEXT Decided-autonomously entry "Research Q1".
   - Known: CONTEXT puts it in `Server/Auth/`; `DeployService`/`DesignService` live in `AzureDesignStudio.Services`, which Desktop runs in-process and which cannot reference the Server.
   - Unclear: whether the user wants the Server copy to stay thin.
   - Recommendation: pure `AdsIdentity`/`AdsCaller` in Services; `Server/Auth/` holds `AdsPolicies` and the endpoint helper. This revises an autonomous (not user-locked) decision; confirm.

2. **What does "self-approval" mean with the current approve-then-deploy flow?** RESOLVED: CONTEXT "Added after research" (implied roles; RequestedByUserId and scope recorded; requester may deploy an approval made by someone else).
   - Known: `WhatIf` records no requester; approval is consumable only by its approver; the web UI approves and deploys in one click by the same user (CodeDrawerTemplate.razor.cs:424-436). The setting defaults off.
   - Unclear: (a) should the WhatIf caller become the `RequestedByUserId` and be the one allowed to deploy an approval made by someone else; (b) should MSA scopes imply `Deploy.Approver` and Desktop imply both roles and self-approval.
   - Recommendation: (a) yes - record `RequestedByUserId` + scope in the gate entry and in the approval row; deploy requires caller == requester (or == approver for legacy/NULL-requester rows); (b) yes to both, with the per-tenant setting still honoured for MSA. Consequence to state in the release notes: on a hosted server with default settings, today's one-click UI is blocked at approval until a Guardrail.Admin enables self-approval or a second Deploy.Approver approves the shared plan hash.

3. **Decommission approvals:** RESOLVED: CONTEXT "Decom approvals: tenant scope plus Deploy.Approver only". recommend tenant scope + `Deploy.Approver`, no separate self-approval rule (approver is also executor by design today). Confirm.

4. **Guardrail policy override semantics:** RESOLVED: CONTEXT "Guardrail override semantics" (replace and merge both supported per tenant, default replace; interpretation to confirm). a tenant row replaces (does not merge with) the `Id=1` default, so a tenant admin can write `{}` and drop inherited protections. Confirm that is intended for the inherited default.

5. **Guardrail change audit:** RESOLVED: CONTEXT Decided-autonomously entry "Research Q5" (audit policy and setting changes). the decided scope audits only the self-approval setting. Cheap to also write `GuardrailPolicyChanged` events; recommended, not required.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.403 (pin 10.0.401, `rollForward latestPatch`) | revert lock drift |
| dotnet-ef | migration generation | yes | 10.0.13 | - |
| Docker | `src/RunSqlServerTests.sh` (SQL Server 2022 container, amd64 emulation on Apple silicon) | yes (OrbStack, daemon answering) | 29.8.2 | skip SQL Server tests (they skip without `ADS_TEST_SQLSERVER`) |
| Node | `node --test` JsTests / service-worker guard | yes | v26.13.0 | xUnit source guard reading the file |
| Real MSA / org Entra tokens | confirming A1, roles | no | - | `checkpoint:human-verify` task |

**Missing dependencies with no fallback:** none for automated work.
**Missing with fallback:** real Entra tokens - manual verification checkpoint.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit 2.9 (Server.Tests, Core.Tests) |
| Config file | none beyond csproj; Server.Tests has `AdsServerFactory` (env "Testing", Sqlite temp file) |
| Quick run command | `dotnet test src/AzureDesignStudio.Server.Tests --no-restore -p:RunAOTCompilation=false --filter "FullyQualifiedName~Identity\|FullyQualifiedName~Authorization\|FullyQualifiedName~CryptoProvider"` |
| Full suite command | `dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false` and `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`; SQL Server migration tests via `src/RunSqlServerTests.sh`; `node --test src/AzureDesignStudio/JsTests/*.test.cjs` |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| IDA-01 | table-driven pure tests: missing/non-GUID/empty oid or tid, both spellings, conflicting spellings, `sub`-only -> no identity | unit | `--filter AdsIdentityTests` | ❌ Wave 0 |
| IDA-01 | via real host, header-driven claims: every Design/Deploy RPC with bad claim returns in-band 401, never `RpcException`; `DesignGrpcService` with a fake offline store makes zero `SaveAsync` calls | integration | `--filter IdentityRpcTests` | ❌ Wave 0 |
| IDA-02 | token with only the URI claim reads/saves/deletes designs and links created with the same Guid; quota counts the same rows | integration | `--filter LegacyClaimTests` | ❌ Wave 0 |
| IDA-03 | enumerate `EndpointDataSource` gRPC endpoints; each protected or on `{ "/Cost/GetRetailPrices" }`; `FallbackPolicy` set; unauthenticated call to every protected method -> `Unauthenticated`; Development-environment host also covered (reflection service) | integration | `--filter AuthorizationSurfaceTests` | ❌ Wave 0 |
| IDA-04 | Get/Set guardrail: org caller without role -> 403; with role -> 200; MSA caller (tid = consumer) -> 200 on own scope; two MSA callers isolated | integration | `--filter GuardrailAuthorizationTests` | ❌ Wave 0 |
| IDA-05 | two tenants write distinct policies and approvals for the same plan hash; neither reads or overwrites the other; legacy `Id=1` row governs a tenant with no row; tenant row replaces it | integration + migration | `--filter TenantIsolationTests`; `DeployDbMigrationTests`; `SqlServerMigrationTests` (container) | ❌ Wave 0 / extend |
| IDA-06 | approve without `Deploy.Approver` -> 403 code A; requester==approver with setting off -> 403 code B (independent); setting on -> allowed; setting change writes an audit event in the same commit; Desktop `InProcessTests` still approves | integration | `--filter ApprovalRulesTests`, `InProcessTests` | ❌ Wave 0 / extend |
| CRED-01 | Development provider: SaveSubscriptionInfo 200, LoadSubscriptionInfo returns it, decrypt round trip, with no vault names and no Azure credential; key ring in a temp directory | integration | `--filter DevelopmentCryptoTests` | ❌ Wave 0 |
| CRED-02 | `Register` with Production + Development throws before registering; Production + KeyVault with no vault still boots and `SaveSubscriptionInfo` answers 503; unknown value still rejected | unit + integration | `--filter CryptoProviderTests` | ✅ extend (signature changes) |
| (support) | service worker excludes `appsettings*.json`; `appsettings.json` still present in the assets manifest | source guard | `node --test src/AzureDesignStudio/JsTests/*.test.cjs` or `SourceGuardTests` | ❌ Wave 0 |
| (support) | no `x-test-user`/`TestAuthHandler` in `src/AzureDesignStudio.Server`; no `#if` in Program.cs | source guard | `--filter SourceGuardTests` | ✅ exists |

### Sampling Rate
- **Per task commit:** the quick filter above.
- **Per wave merge:** full Server.Tests plus Core.Tests offline.
- **Phase gate:** full suites plus `src/RunSqlServerTests.sh` green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] Extend `TestAuthHandler` with a repeated `x-test-claim: <type>|<value>` header and default `tid` (03-RESEARCH already recommended this shape)
- [ ] Extend `TestServerCallContext.ForUser`/`WithClaims` with default tid, roles, single-user marker, and a "no tid" variant
- [ ] `AdsIdentityTests`, `AuthorizationSurfaceTests`, `TenantIsolationTests`, `ApprovalRulesTests`, `DevelopmentCryptoTests`, `IdentityRpcTests`
- [ ] Update `SqlServerMigrationModelTests`/`SqlServerMigrationTests` table lists and the legacy fixture expectations; `DeployDbMigrationTests` seeds
- [ ] Framework install: none

## Security Domain

### Applicable ASVS Categories (level 1, `security_enforcement` true, block on high)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes | Existing JwtBearer + Microsoft.Identity.Web; keep issuer/audience validation; test auth scheme only in Server.Tests |
| V3 Session Management | no (stateless bearer) | - |
| V4 Access Control | yes | Deny-by-default endpoints, service-body role checks, tenant-scoped rows, cross-tenant answers as "not found" |
| V5 Input Validation | yes | `Guid.TryParse` + non-empty, policy JSON validated (`GuardrailPolicy.FromJson`), existing 64 KiB cap |
| V6 Cryptography | yes | Data Protection (never hand-rolled); Key Vault RSA-OAEP unchanged; Production refuses dev provider |
| V7/V8 Logging/Data protection | yes | Audit events; never log secrets; dev key ring under user profile (plaintext at rest on macOS/Linux, documented) |

### Known Threat Patterns

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Cross-tenant overwrite of approval via identical plan hash | Tampering | Composite key `(TenantId, PlanHash)`; peek in the gate bound to scope |
| Any authenticated user rewrites the global do-not-touch list (today) | Elevation of privilege | Role check + per-scope row |
| Personal-account scope collision | Elevation / Info disclosure | scope = oid for consumer tid |
| Forged `tid`/roles | Spoofing | Only from the validated principal; never from request fields; issuer validation on |
| Role claim injected on Desktop/Server by header/claim | Spoofing | Host marker via `UserState`, set only by host code; test auth handler only in tests |
| Self-approval / role misconfiguration restoring it | Elevation | Two independent checks (role, then requester != approver unless setting) |
| Dev keys in production | Information disclosure | Environment check at startup + prefix on dev ciphertext |
| Dev key ring leakage | Information disclosure | File under user profile, not repo; documented plaintext-at-rest caveat |
| CSV-style/log injection via principal display name | Tampering | Principal display text already built from `name` claim; keep truncating to column length (`ApprovedBy` is 320) |
| App-only token holding a role | Spoofing | `[RequiredScope]` needs `scp` which app-only tokens lack; keep it |

## Project Constraints (from CLAUDE.md / AGENTS.md)

No `CLAUDE.md` exists in the repo (checked `./CLAUDE.md`, `./.claude/CLAUDE.md`). Directives from `AGENTS.md` that bind this phase:
- Services read the caller from `context.UserState[ServiceTools.UserStateKey]`, never `GetHttpContext()`; the Services project must stay free of ASP.NET Core (Mac Catalyst has none).
- New gRPC methods: proto in SharedModels + Services + `HostedServices.cs` subclass + `MapGrpcService` in Server + `InProcessCallInvoker.Add<T>` in Desktop (applies if `Get/SetTenantSettings` RPCs are added).
- Versions only in `Directory.Packages.props`; csproj declares `Include=`; locked restore in CI - no package is added, so lock files must not change; revert SDK 10.0.403 drift.
- Server builds: Release needs `-p:RunAOTCompilation=false` for Server.Tests.
- A claim whose "none" must not be inferred from zero rows - n/a here; but approval/audit must fail closed.
- PITFALL: Kestrel `Listen*` replaces URL endpoints - not touched.
- Commit planning artifacts with `docs(<phase>):` messages; do not hand-edit `STATE.md` counters.
- `SourceGuardTests`: no preprocessor conditionals in Program.cs; `EnsureCreated`/`Migrate` only at the listed sites; no test auth in the Server project.
- Desktop deploy.db backups are private (stem `deploy`); a new deploy.db migration relies on `DeployDbStartup` and must keep that.

## Sources

### Primary (HIGH confidence)
- In-repo files read this session: `src/AzureDesignStudio.Services/{ServiceTools,DeployService,DesignService,LocalDesignService,RetailPriceService,ICryptoService,StatusCodes}.cs`, `Models/DesignDbContext.cs`, `InProcess/InProcessCallInvoker.cs`, `src/AzureDesignStudio.Server/{Program.cs,Services/*,Persistence/*}`, Server migrations Baseline/NormaliseLegacyDefaults, Services SQLite Baseline, `Desktop/{DesktopIdentity,MsalAccountService,MauiProgram,DeployDbStartup}.cs`, `Discovery/Iac/WhatIf/WhatIfTokenGate.cs`, `Workspace/UniqueIndexGuard.cs`, `AzureDesignStudio/{Program.cs,AzureDesignStudio.csproj,Services/DesignGrpcService.cs}`, `wwwroot/service-worker*.js`, `Server.Tests` harness files, `docs/entra-setup.md`, `docs/database-operations.md`
- Reproduced locally: (1) claim-mapping probe with Microsoft.Identity.Web 3.15.1 (output above); (2) temporary Server.Tests probe enumerating the 21 gRPC endpoints, fallback policy null, and method-level `[Authorize]` on an override reaching endpoint metadata (probe file removed, `git status` clean)
- https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference (oid, tid, roles, sub definitions; consumer tenant GUID)
- https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies (FallbackPolicy semantics)

### Secondary (MEDIUM confidence)
- `.planning/research/{ARCHITECTURE,PITFALLS}.md` (earlier milestone research; partly describes target state, see State of the Art)
- WebSearch result summarising a Microsoft Q&A thread that `oid` can be missing when the service principal is not provisioned in the tenant (not authoritative)

### Tertiary (LOW confidence)
- MSA `oid` behaviour for access tokens to a custom API (A1); MapStaticAssets under FallbackPolicy (A6); SQL Server PK-rebuild T-SQL (A5)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - no new packages; framework presence verified
- Architecture: HIGH for current-state facts and claim shapes (reproduced); MEDIUM for the Desktop/host-marker and requester designs (Open Questions)
- Pitfalls: HIGH for items with file/line evidence; MEDIUM for MSA token behaviour and SQL Server DDL

**Research date:** 2026-11-14
**Valid until:** 2026-12-14 (30 days; stable stack, but re-check if Microsoft.Identity.Web is bumped)
