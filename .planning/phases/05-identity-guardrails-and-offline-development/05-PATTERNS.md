# Phase 5: Identity, Guardrails, and Offline Development - Pattern Map

**Mapped:** 2026-11-14
**Files analyzed:** 22 new/modified
**Analogs found:** 21 / 22 (all analog paths verified git-tracked)

Paths are relative to `src/` unless noted. Per CONTEXT (research Q1), `AdsIdentity` lives in `AzureDesignStudio.Services/Auth/`, not Server.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match |
|---|---|---|---|---|
| `AzureDesignStudio.Services/Auth/AdsIdentity.cs` (new) | utility (pure) | transform | `AzureDesignStudio.Services/ServiceTools.cs` L9-40 | exact |
| `AzureDesignStudio.Services/Auth/AdsCaller.cs` (new, rules) | utility | transform | `ServiceTools.cs` | role-match |
| `AzureDesignStudio.Services/ServiceTools.cs` (mod: TryGetCaller) | utility | request-response | itself | exact |
| `AzureDesignStudio.Services/DeployService.cs` (mod: roles, scope, self-approval) | service | CRUD | itself L1056, L1418, L2366 | exact |
| `AzureDesignStudio.Services/DesignService.cs` (mod: TryGetCaller) | service | CRUD | itself L25-31 | exact |
| `AzureDesignStudio.Services/Models/DesignDbContext.cs` (mod: TenantId, AdsTenantSettings, AdsSecurityEvents) | model | CRUD | itself (`PlanApprovalModel` L55, `GuardrailPolicyModel` L86, `DecomApprovalModel` L111) | exact |
| `AzureDesignStudio.Services/Migrations/Sqlite/<ts>_TenantScope.cs` (new) | migration | batch | `Migrations/Sqlite/20261114130156_Baseline.cs` | role-match |
| `AzureDesignStudio.Server/Persistence/Migrations/<ts>_TenantScope.cs` (new) | migration | batch | `Persistence/Migrations/20261114123056_NormaliseLegacyDefaults.cs` + `..._Baseline.cs` | exact |
| `AzureDesignStudio.Server/Auth/AdsPolicies.cs` (new) | config | request-response | `Server/Program.cs` L122-124 + `Services/HostedServices.cs` | partial |
| `AzureDesignStudio.Server/Program.cs` (mod: fallback policy, AllowAnonymous, Register env arg) | config | request-response | itself (L65, L122-124) | exact |
| `AzureDesignStudio.Server/Services/HostedServices.cs` (mod: Cost anonymous, caller marker) | controller | request-response | itself | exact |
| `AzureDesignStudio.Server/Services/CryptoProvider.cs` (mod) | config | request-response | itself | exact |
| `AzureDesignStudio.Server/Services/DevelopmentCryptoService.cs` (new) | service | transform | `Server/Services/CryptoService.cs` (implements `ICryptoService`) | role-match |
| `AzureDesignStudio.Services/InProcess/InProcessCallInvoker.cs` (mod: single-user marker) | middleware | request-response | itself L188-199 | exact |
| `AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs` (mod: RequestedBy/Scope in Entry) | service | event-driven | itself L49, L77, L173 | exact |
| `AzureDesignStudio/wwwroot/service-worker.published.js` (mod) | config | file-I/O | itself L14-23 | exact |
| `AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs` (mod: claim headers, tid) | test helper | request-response | itself | exact |
| `AzureDesignStudio.Server.Tests/TestSupport.cs` (mod: ForUser tid/roles/marker) | test helper | request-response | itself L26-38 | exact |
| `AzureDesignStudio.Server.Tests/Hosting/{AuthorizationSurfaceTests,IdentityRpcTests,...}.cs` (new) | test | request-response | `Hosting/ServerHostTests.cs`, `AdsServerFactory.cs`, `GrpcTestClients.cs` | role-match |
| `AzureDesignStudio.Server.Tests/Hosting/CryptoProviderTests.cs` (mod) | test | transform | itself | exact |
| `AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs` (mod/extend) | test | file-I/O | itself | exact |
| `AzureDesignStudio.Server.Tests/Persistence/{DeployDbMigrationTests,SqlServerMigrationModelTests,SqlServerMigrationTests}.cs` (mod) | test | batch | themselves | exact |
| Core.Tests `AdsIdentityTests` (new, pure) | test | transform | existing Core.Tests xUnit style | partial |

## Pattern Assignments

### `Services/Auth/AdsIdentity.cs` + `ServiceTools.TryGetCaller`
**Analog:** `AzureDesignStudio.Services/ServiceTools.cs` L6-40.

```csharp
private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
public const string UserStateKey = "ads.user";
public static ClaimsPrincipal GetUser(ServerCallContext context) =>
    context.UserState.TryGetValue(UserStateKey, out var user) && user is ClaimsPrincipal principal
        ? principal : new ClaimsPrincipal(new ClaimsIdentity());
public static string? GetUserId(ClaimsPrincipal user)
{
    var value = user.FindFirst(ObjectIdClaim)?.Value ?? user.FindFirst("oid")?.Value;
    return Guid.TryParse(value, out _) ? value : null;
}
```
Extend: add tenant URI `http://schemas.microsoft.com/identity/claims/tenantid` / `tid`, reject `Guid.Empty`, never read `sub`/NameIdentifier, scope = tid==`9188040d-6c67-4c5b-b112-36a304b66dad` ? oid : tid. Keep `GetUserId` as thin wrapper. Services must stay free of ASP.NET Core (pure `System.Security.Claims`). Single-user marker: a second `UserState` key (e.g. `ads.host.singleuser`) read in `TryGetCaller`, never a claim.

### In-band 401 / 403 (DesignService.cs, DeployService.cs)
**Analog:** `DesignService.cs` L25-31 (copy for every site; 403 variants follow DeployService L1056-1060).
```csharp
var userIdClaim = ServiceTools.GetUserId(context);
if (string.IsNullOrWhiteSpace(userIdClaim))
{
    logger.LogWarning("Access denied. userIdClaim is empty.");
    response.StatusCode = StatusCodes.Status401Unauthorized;
    return response;
}
```
```csharp
// DeployService.cs L1056-1060 (403 + coded ErrorMessage convention)
if (!string.Equals(approval.ApprovedByUserId, userIdClaim, StringComparison.Ordinal))
{
    _logger.LogWarning("Plan {PlanHash} approved by {Approver} but deployed by a different principal", planHash, approval.ApprovedBy);
    response.StatusCode = StatusCodes.Status403Forbidden;
    response.ErrorMessage = "PLAN_APPROVER_MISMATCH: only the principal that approved this plan may deploy it.";
```
New codes follow `UPPER_SNAKE: sentence` (e.g. `PLAN_APPROVER_ROLE_REQUIRED`, `PLAN_SELF_APPROVAL_REFUSED`). Same comparison sites at L1418 (stack) and ~L2366 (decom). Never throw `RpcException(Unauthenticated)` (client `DesignGrpcService.IsDeferredSaveFailure` would retry forever). Compare parsed Guids, not ordinal strings (Pitfall 5).

### DesignDbContext.cs (tenant columns, new tables)
**Analog:** same file; entities are `record`s with `[Key]`/`[StringLength]` annotations (L55-125). Add `TenantId` to the three models, composite keys via `OnModelCreating`/`HasKey`, new `DbSet<TenantSettingsModel> AdsTenantSettings`, `DbSet<SecurityEventModel> AdsSecurityEvents` next to L26-30. Guardrail row keeps explicit `Id` (see comment L88-90: no IDENTITY).

### SQL Server migration `Server/Persistence/Migrations/<ts>_TenantScope.cs`
**Analogs:** `NormaliseLegacyDefaults.cs` (hand-written Up via `migrationBuilder.Sql`, empty Down, Designer + snapshot still EF-generated) and `Baseline.cs` guards (`IF OBJECT_ID(N'T', N'U')`, `COL_LENGTH(N'T', N'Col') IS NULL`, `INDEXPROPERTY`).
```csharp
internal const string Sql = """
    DECLARE @defaultName sysname = (
        SELECT d.name FROM sys.default_constraints d
        JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
        WHERE d.parent_object_id = OBJECT_ID(N'AdsPlanApprovals') AND c.name = N'ApprovedAtUtc');
    IF @defaultName IS NOT NULL
    BEGIN
        -- EXEC accepts string concatenation but not a function call, so build the text first.
        DECLARE @drop nvarchar(400) = N'ALTER TABLE AdsPlanApprovals DROP CONSTRAINT ' + QUOTENAME(@defaultName);
        EXEC(@drop);
    END
    """;
protected override void Up(MigrationBuilder b) { b.Sql(Sql); }
protected override void Down(MigrationBuilder b) { /* Intentionally empty: rollback = restore backup */ }
```
Follow this for: guarded `ADD TenantId`, backfill `Guid.Empty`, `ALTER COLUMN NOT NULL`, PK drop by name looked up in `sys.key_constraints` (same `QUOTENAME` + `EXEC(@var)` shape), new PK `(TenantId, PlanHash)`, no leftover model-unknown default constraint. Generate Designer/snapshot with `dotnet ef migrations add`, then replace Up by hand. Prove with `src/RunSqlServerTests.sh`.

### SQLite migration `Services/Migrations/Sqlite/<ts>_TenantScope.cs`
**Analog:** `20261114130156_Baseline.cs` (guarded `CREATE TABLE IF NOT EXISTS` const `GuardedDdl`; table defs equal `dotnet ef migrations script` output). For the PK change use the EF-generated table rebuild (research Pattern 3); Desktop startup path is `Desktop/DeployDbStartup.cs` `Migrate(db, path, backupsDir)` (premigration backup stem `deploy`, keep it unchanged). Only migrate site; do not add `.Migrate()` elsewhere (SourceGuardTests).

### `Server/Services/CryptoProvider.cs` + `DevelopmentCryptoService.cs`
**Analog:** `CryptoProvider.cs` (whole file, 26 lines) and `CryptoService.cs` (implements `ICryptoService`).
```csharp
public const string Key = "Crypto:Provider"; public const string KeyVault = "KeyVault";
public static void Register(IServiceCollection services, IConfiguration configuration)
{
    var value = configuration[Key];
    if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), KeyVault, StringComparison.OrdinalIgnoreCase))
    { services.AddSingleton<ICryptoService, CryptoService>(); return; }
    throw new InvalidOperationException(
        $"{Key} is \"{value}\", which is not a crypto provider. The only accepted value is {KeyVault}.");
}
```
Add `IHostEnvironment` param and `Development` branch (see RESEARCH "Dev crypto provider"). Call site: `Program.cs` L65 `CryptoProvider.Register(builder.Services, builder.Configuration);`. No `#if` in Program.cs (SourceGuardTests). Keep Key Vault lazy.

### Auth registration (`Server/Auth/AdsPolicies.cs`, `Program.cs`, `HostedServices.cs`)
**Analog:** `Program.cs` L122-124 and `HostedServices.cs`:
```csharp
app.MapGrpcService<HostedDesignService>().EnableGrpcWeb();
app.MapGrpcService<HostedDeployService>().EnableGrpcWeb();
app.MapGrpcService<RetailPriceService>().EnableGrpcWeb();   // anonymous only by absence of metadata
```
```csharp
[Authorize]
[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]
public sealed class HostedDeployService(...) : DeployService(...);
// CallerInterceptor: context.UserState[ServiceTools.UserStateKey] = context.GetHttpContext().User;
```
Copy: make Cost explicit `.AllowAnonymous()`, add `FallbackPolicy` (authenticated) plus `AllowAnonymous()` on static/Razor/`MapFallbackToFile`. `CallerInterceptor.SetCaller` is where any server-side extras go (single-user marker must NOT be set on Server).

### In-process host marker (`InProcessCallInvoker.cs`)
**Analog:** L188-199: `_userState[ServiceTools.UserStateKey] = caller;` in `InProcessServerCallContext` ctor. Add the single-user marker key in the same place (desktop only).

### `WhatIfTokenGate.cs`
**Analog:** itself, `private sealed record Entry(string PayloadHash, DateTimeOffset Expires, bool HasDelete, ...)` L49, constructed L77 and L173. Add optional `RequestedByUserId`/`ScopeId` params additively (existing callers/tests keep compiling).

### `service-worker.published.js`
**Analog:** L14-23. Change `const offlineAssetsExclude = [ /^service-worker\.js$/ ];` to include `/^appsettings(\..+)?\.json$/`. Guard test: `JsTests/ads-pointer.test.cjs` style (`node --test`) or a `SourceGuardTests` file-read fact.

### Server.Tests helpers
**`TestAuthHandler.cs`:** header `x-test-user` -> claims list (`ObjectIdClaimType`, `name`, optional scope `ScopeClaimType`/`Server.Access`) -> `ClaimsIdentity(claims, SchemeName)`. Extend with default `tid` claim (`.../identity/claims/tenantid`), repeated `x-test-claim: type|value` header, roles via `ClaimTypes.Role`, "no tid" variant. Test-only; never in Server project.
**`TestSupport.cs` `TestServerCallContext`:** `ForUser(Guid, displayName)` builds `new(ObjectIdClaim, ...)`; `WithClaims(params Claim[])`; `WithoutIdentity()`; caller stored in `_userState[ServiceTools.UserStateKey]`. Add default tid, roles, single-user marker param (all existing tests need `tid`, Pitfall 7).
**`GrpcTestClients.CreateGrpcChannel(factory, user, withScope)`** with private `TestUserHandler : DelegatingHandler` adding headers: extend with claim-header overloads.
**`AdsServerFactory`** (`WebApplicationFactory<Program>`, env "Testing", pins `KeyVaultName` etc. via `UseSetting` + in-memory config, swaps auth scheme in `ConfigureTestServices`): reuse as is for `AuthorizationSurfaceTests` (inject `EndpointDataSource`, filter `GrpcMethodMetadata`, check `IAuthorizeData`/`IAllowAnonymous`; allow list `/Cost/GetRetailPrices`) and `DevelopmentCryptoTests` (`UseSetting("Crypto:Provider","Development")`, temp key ring dir).
**`CryptoProviderTests`:** `Registered(config)` inspects `ServiceDescriptor` without building provider; update helper to pass an `IHostEnvironment` and keep exact-message assertions in sync.
**`SourceGuardTests`:** regex-over-file pattern (`PreprocessorConditional`, `EnsureCreatedCall`, `MigrateCall`, `MigrationSites` list, `RepositoryRoot()`); add facts for service-worker exclusion and "no TestAuthHandler/x-test-user in Server".

## Shared Patterns

- **Caller from UserState only:** `ServiceTools.GetUser(context)`; services never use `GetHttpContext()` (source: ServiceTools.cs L14-19). Apply to all new service code.
- **In-band status:** responses carry `StatusCode`/`ErrorMessage` (`StatusCodes.Status40xxx`), coded `UPPER_SNAKE:` prefix (DeployService L1056-1060). Apply to 401/403/404 for role, tenant, self-approval.
- **Hosted subclass wrappers:** thin `[Authorize]` classes in `HostedServices.cs`; logic stays in Services (AGENTS.md "Add gRPC service" row). New RPCs (tenant settings) also need `InProcessCallInvoker.Add<T>` in `Desktop/MauiProgram.cs`.
- **Migrations:** one owner per site (`DatabaseBootstrapper.cs`, `DeployDbStartup.cs`); hand-guarded T-SQL for SQL Server; no startup DDL.
- **Config/guards:** no `#if` in Program.cs; central package versions; no new packages (Data Protection in shared framework); revert SDK 10.0.403 lock-file drift.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `AzureDesignStudio.Server/Auth/AdsPolicies.cs` fallback-policy helper | config | request-response | No `AddAuthorization`/`FallbackPolicy` exists in `Program.cs` (grep found no match); use RESEARCH Pattern 2 |
| Append-only audit write (`AdsSecurityEvents`) | model/service | event-driven | Closest is `AdsPlanApplyEvents`/`AdsDecomWindowEvents` (event tables in DesignDbContext/Baseline); copy table shape, not logic |

## Metadata

**Analog search scope:** Services, Server, Server.Tests, Desktop (DeployDbStartup), Discovery WhatIf, client wwwroot
**Files scanned:** ~25
**Pattern extraction date:** 2026-11-14
