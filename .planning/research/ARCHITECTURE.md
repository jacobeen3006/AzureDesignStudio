# Architecture Research

**Domain:** Brownfield .NET 10 Azure design/inventory/execution workspace — subsequent milestone (Trust Gate + Round 2 shell completion + signed MSIX)
**Researched:** 2026-10-21
**Confidence:** HIGH for repo structure and seams (read from source at `a05ef8c` + working tree); MEDIUM-HIGH for external platform behaviour (each external claim carries a URL)

**Scope rule applied:** this is *not* a greenfield architecture study. The 11-project layered solution, both hosts, the gRPC surface, the SQLite workspace, and the pure Discovery engines are assumed and not re-litigated. What follows is **where the milestone's new capabilities attach to the existing seams, in what order, and with which boundaries** — so the Trust Gate closes without forking the layering.

**Legend used throughout:** [=] exists today · [~] exists but must change · [+] must be added.

---

## Standard Architecture

### System Overview — target state for this milestone

```
HOSTS  (shape unchanged)
  WASM      `src/AzureDesignStudio/Program.cs`
  Desktop   `src/AzureDesignStudio.Desktop/MauiProgram.cs`
  MCP       `src/AzureDesignStudio.Mcp/Program.cs`   (stdio, read-only SQLite)
      │ gRPC-Web / HTTP1.1  ·  native gRPC / HTTP2 (loopback 5204)  ·  read-only SQLite
      ▼
SHARED UI + DOMAIN  — `src/AzureDesignStudio`  →  `Core`, `SharedModels`
  [=] AdsContext (canvas singleton: Diagram, CurrentDesignName, AllStencils)
  [~] AdsCommandRegistry (owner-scoped; moved to Core)
  [+] AdsShellState (scoped)     [+] AdsSelectionStore (scoped)     [+] AdsThemeService
  [=] AdsHostFeatures (host capabilities only — never per-circuit state)
      │ protobuf (`SharedModels/*.proto`)
      ▼
SERVER  — `src/AzureDesignStudio.Server`
  [~] pipeline: routing → authN → rate limit → authZ → gRPC-Web
  [+] Auth/AdsIdentity      (claim → (oid, tid); TryParse, never throws)
  [+] Auth/AdsPolicies      (FallbackPolicy = authenticated; Guardrail.Admin role policy)
  [+] Auth/AdsConcurrencyInterceptor   (per-user in-flight cap → ResourceExhausted)
  [+] Persistence/DatabaseBootstrapper (legacy baseline + Migrate(); no EnsureCreated)
  [+] Models/DesignDbContextFactory    (design-time context, no Key Vault boot)
  [+] Migrations/           (SQL Server; migration 1 = today's guarded DDL)
  [~] Services/*            (in-band status_code contract kept; claim→Guid via one helper)
      │ ARM / ARG / Retail           │ EF Core
      ▼                              ▼
  Azure control plane          SQL Server (prod) / SQLite (dev + tests)

WORKSPACE SIDECAR  — `src/AzureDesignStudio.Workspace` (SQLite; desktop writer + MCP reader)
  [~] WAL + busy_timeout + quick_check        [~] migrations (baseline = guarded DDL)
  [+] schema-version handshake for the read-only MCP window
```

### The four milestone capabilities and where each one belongs

| Capability | Layer it belongs to | Concrete seam | Why not elsewhere |
|---|---|---|---|
| Identity normalization (N11) | Server, **before** any service body | [+] `Server/Auth/AdsIdentity.cs` — `TryFrom(ClaimsPrincipal, out AdsIdentity)` with `Guid.TryParse`, accepting `oid` *and* `http://schemas.microsoft.com/identity/claims/objectidentifier`, plus `tid` | A gRPC **interceptor** cannot return the in-band status codes `DesignService`/`DeployService` already contractually return; and putting it in `Core` would drag `ServerCallContext` into a UI library |
| Payload validation (N26) | **Shared contract**, enforced twice | [+] `Core/Common/AdsDesignPayload.cs`, called from `TopMenu.SaveDiagram` (client) and `DesignService.Save`/`Load` (server) | A server-only check leaves the WASM client uploading megabytes to fail; a client-only check is bypassable. `Core` is the lowest assembly both already reference |
| Guardrail scoping (N10) | Server: **authorization policy** + **tenant-scoped row** | [~] `DeployService.SetGuardrailPolicy`/`GetGuardrailPolicy` (`DeployService.cs:1295-1360`, `GuardrailRowId = 1` at `:1261`) + [+] `Auth/AdsPolicies.cs` + `GuardrailPolicyModel.TenantId` | Middleware cannot express "only an approver may replace the do-not-touch list"; that is policy metadata on the RPC, not a pipeline stage |
| Rate limiting / limits / health (N14) | ASP.NET Core **pipeline + gRPC options** | [~] `Program.cs` (`UseRateLimiter`, `AddGrpc(o => o.MaxReceiveMessageSize)`, Kestrel cap, `MapHealthChecks`) + [+] `Auth/AdsConcurrencyInterceptor.cs` for per-user in-flight RPCs | Health endpoints must bypass the `FallbackPolicy`; message-size caps are transport, not service logic; concurrency is per-method and per-user, which only the gRPC interceptor can see |

### Server request pipeline — current vs target (exact ordering matters)

Current (`src/AzureDesignStudio.Server/Program.cs:155-194`):

```
UseWebAssemblyDebugging | UseExceptionHandler+UseHsts
UseForwardedHeaders → UseHttpsRedirection → UseRouting
→ UseAuthentication → UseAuthorization → UseGrpcWeb
→ MapGrpcService<DesignService/DeployService/RetailPriceService>().EnableGrpcWeb()
→ MapStaticAssets → MapRazorPages → MapFallbackToFile("index.html")
```

Target additions, in this order:

```
1  UseForwardedHeaders                      [=] (unchanged)
2  UseExceptionHandler + UseHsts (non-dev)  [=] (unchanged)
3  UseHttpsRedirection                      [=] (unchanged)
4  UseRouting                               [=]
5  UseAuthentication                        [=]  ← must precede the limiter: partition key reads `oid`
6  UseRateLimiter                           [+]  ← after routing (endpoint metadata) and authN (claims)
7  UseAuthorization                         [=]  ← + FallbackPolicy = RequireAuthenticatedUser
8  UseGrpcWeb                               [=]
9  MapGrpcService<DesignService>()...       [~] + .RequireRateLimiting("user")
10 MapGrpcService<RetailPriceService>()     [~] + .AllowAnonymous() + .RequireRateLimiting("anon")
11 MapHealthChecks("/health/live")          [+] .AllowAnonymous()
12 MapHealthChecks("/health/ready")         [+] .AllowAnonymous() + DbContext check
13 MapStaticAssets / MapRazorPages / MapFallbackToFile   [=] (unchanged)
```

Two ordering traps specific to this repo:

- `RetailPriceService` is anonymous **by documentation only** (`Services/RetailPriceService.cs:10-13`). Adding `FallbackPolicy` turns that comment into a build-breaking requirement unless `[AllowAnonymous]` is added in the same commit — otherwise the WASM cost estimator starts returning `Unauthenticated`.
- The concurrency interceptor must be applied via `AddGrpc(o => o.Interceptors.Add<AdsConcurrencyInterceptor>())` **before** `AddGrpc` is first configured (`Program.cs:69`); calling `AddGrpc()` again later is additive, so the interceptor belongs in the existing call.

### Component Responsibilities

| Component | Responsibility | Status | Location |
|---|---|---|---|
| WASM / Desktop / Server / MCP hosts | DI composition, platform auth, transport choice | [=] | `src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio.Desktop/MauiProgram.cs`, `src/AzureDesignStudio.Server/Program.cs`, `src/AzureDesignStudio.Mcp/Program.cs` |
| `AdsContext` | Canvas state: one `AdsDiagram`, `CurrentDesignName`, stencil catalog | [=] | `src/AzureDesignStudio/Services/AdsContext.cs` |
| `AdsCommandService` | Key → `AdsCommand` registry consumed by palette, inspector, menu | [~] owner-scoped, move registry to `Core` | `src/AzureDesignStudio/Services/AdsCommandService.cs` → `src/AzureDesignStudio.Core/Services/AdsCommandRegistry.cs` |
| `AdsShellState` | Per-circuit shell state: theme mode, density, zone collapse, palette open | [+] | `src/AzureDesignStudio/Services/AdsShellState.cs` |
| `AdsSelectionStore` | The single selection cursor (canvas today, tenant forest next) | [+] | `src/AzureDesignStudio/Services/AdsSelectionStore.cs` |
| `AdsHostFeatures` | Optional host *capabilities* (null = absent) | [=] unchanged role | `src/AzureDesignStudio/Services/AdsHostFeatures.cs` |
| `AdsDesignText` | Canonical, byte-stable `.ads.json` reader/writer, `Version = 1` | [~] add `TryRead` (legacy bare-graph tolerance) | `src/AzureDesignStudio.Core/Common/AdsDesignText.cs` |
| `AdsDesignPayload` | Server/client payload contract: version, byte cap, canonical parse | [+] | `src/AzureDesignStudio.Core/Common/AdsDesignPayload.cs` |
| `AdsIdentity` / `AdsPolicies` | Claim → typed identity; role policies | [+] | `src/AzureDesignStudio.Server/Auth/` |
| `AdsConcurrencyInterceptor` | Per-user in-flight RPC cap on long-running methods | [+] | `src/AzureDesignStudio.Server/Auth/AdsConcurrencyInterceptor.cs` |
| `DatabaseBootstrapper` | Legacy-schema detection → baseline marking → `Migrate()` | [+] | `src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs` |
| `DesignDbContextFactory` | Design-time context so `dotnet ef` never boots the Key Vault path | [+] | `src/AzureDesignStudio.Server/Models/DesignDbContextFactory.cs` |
| `WorkspaceDb` | Opens/creates the SQLite workspace; sole owner of the connection string | [~] WAL pragmas, `Migrate()`, delete ad-hoc DDL | `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs` |
| `AdsWorkspace` | MCP read-only window over the same file | [~] schema-version compatibility check | `src/AzureDesignStudio.Mcp/AdsWorkspace.cs` |
| Offline store / scan sink / change feed | Workspace writers, all via `WorkspaceDb.CreateContext()` | [=] unchanged signatures | `src/AzureDesignStudio.Workspace/*.cs` |
| Discovery engines | Pure, deterministic analysis | [=] untouched by this milestone | `src/AzureDesignStudio.Discovery/` |

### Recommended Project Structure (deltas only)

```
src/
├── Directory.Build.props                  [+] shared TFM-independent settings, nbgv, nullable, analyzers
├── Directory.Packages.props               [+] central package versions (incl. MicrosoftAspNetCoreVersion)
├── version.json                           [~] stays the single version source (hoisted, see Build Order)
├── AzureDesignStudio.Core/
│   ├── Common/AdsDesignPayload.cs         [+] payload contract (both sides)
│   ├── Common/AdsDesignText.cs            [~] TryRead + version tolerance
│   └── Services/AdsCommandRegistry.cs     [+] pure registry (owner-scoped), removed from WASM
├── AzureDesignStudio/
│   ├── Services/AdsShellState.cs          [+] scoped shell state
│   ├── Services/AdsSelectionStore.cs      [+] scoped selection cursor
│   ├── Services/AdsThemeService.cs        [+] wraps AdsTheme JS, owns mode + broadcast
│   └── Components/{CommandPalette,DesignInspector,StatusBar,ThemeToggle}.razor  [~] consume stores
├── AzureDesignStudio.Server/
│   ├── Auth/AdsIdentity.cs                [+]
│   ├── Auth/AdsPolicies.cs                [+]
│   ├── Auth/AdsConcurrencyInterceptor.cs  [+]
│   ├── Persistence/DatabaseBootstrapper.cs [+]
│   ├── Models/DesignDbContextFactory.cs   [+]
│   ├── Migrations/                        [+] baseline (guarded SQL) + later generated diffs
│   └── Services/ServiceTools.cs           [~] delete GetUserId
├── AzureDesignStudio.Workspace/
│   ├── WorkspaceDbContextFactory.cs       [+] one place builds options + pragmas
│   └── Migrations/                        [+] baseline (guarded SQL) + later diffs
├── AzureDesignStudio.Server.Tests/        [+] WebApplicationFactory + gRPC channel
├── AzureDesignStudio.UI.Tests/            [+] bUnit
└── AzureDesignStudio.E2E.Tests/           [+] Microsoft.Playwright (WASM) + WebView2 CDP (Desktop)

.github/workflows/build.yml                [~] + windows job, + server-tests job, + coverage
.github/workflows/publish.yml              [~] version/Publisher stamping fixed; signing strategy
src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest  [~] real Publisher, version from nbgv
```

**Structure rationale**

- New server concerns live in **`Auth/` and `Persistence/`**, not in `Services/`. `Services/` holds gRPC service implementations by RPC family; mixing policy, identity, and schema bootstrapping into it is how `DeployService` reached 2,210 lines.
- The payload contract lives in `Core`, not `SharedModels`: `SharedModels` is proto-generated and dependency-free, while the validator must call `AdsDesignText`/`DiagramGraph`, which are `Core` types. Adding a `Core → SharedModels` reference would invert nothing (Core does not reference it today), but adding `SharedModels → Core` would create a cycle through the generated proto types' consumers.
- Test projects are **siblings under `src/`** and join the existing `Tests` solution folder (`{0AB3BF05-4346-4AA6-1389-037BE0695223}` in `src/AzureDesignStudio.sln`), matching the one-test-project convention rather than inventing a `tests/` tree.
- The bUnit project references **`src/AzureDesignStudio` directly**. This edge already exists in the repo (`Server` → WASM at `AzureDesignStudio.Server.csproj:37`), so it is an established pattern, not a new architectural compromise — and it avoids extracting a Razor Class Library mid-gate.

---
## Architectural Patterns

### Pattern 1: One seam per concern (extend existing seams, never add a parallel one)

**What:** every new capability attaches at the narrowest existing seam that all callers already funnel through. The repo does this well today (`GrpcClientFactory` client names, `AdsHostFeatures` delegates, `IDesignOfflineStore`, `IScanSink`); the milestone must not introduce a second convention for the same concern.

**When to use:** always. The concrete applications for this milestone:

| Concern | The seam (single place) | What must *not* happen |
|---|---|---|
| DB options/connection | `WorkspaceDb` is the only writer-facing constructor; `AdsWorkspace` builds its own — [+] `WorkspaceDbContextFactory.CreateOptions(...)` used by both and by tests | A third `DbContextOptionsBuilder` in a new drawer or tool |
| Identity | One `AdsIdentity.TryFrom` replacing 26 `new Guid(...)` sites | A per-service `GetUserId` clone |
| Payload | One `AdsDesignPayload.TryValidate` | A size check in `DesignGrpcService` *and* a different one in `DesignService` |
| Commands | One registry with owner keys | Palette, inspector and menu each holding their own command list |

**Trade-offs:** a seam is a compile-time dependency that must be honoured; the cost is discipline, the payoff is that the next host or the next test project does not need to learn a second path.

### Pattern 2: Baseline-as-guarded-SQL migrations (coexistence with `EnsureCreated` + ad-hoc DDL)

**What:** migration **1** is not EF-generated `CreateTable` calls; it is the *existing* bootstrap expressed as guarded SQL (`IF OBJECT_ID(...) IS NULL CREATE TABLE ...` on SQL Server, `CREATE TABLE IF NOT EXISTS ...` on SQLite). Existing databases apply it as a no-op and get a `__EFMigrationsHistory` row; fresh databases get the real schema. Migrations 2+ are ordinary EF-generated diffs.

**Why this shape:** the codebase has three schema sources today — `EnsureCreated()` from the model, ~60 lines of T-SQL in `Program.cs:83-152`, and `CREATE TABLE IF NOT EXISTS`/`ALTER TABLE` in `WorkspaceDb.EnsureAddedColumns`/`EnsureAdditiveTables`. EF's generated `CreateTable` would collide with every existing installation (`table already exists`), and history-table surgery (inserting a fake applied row) silently skips columns that older files are missing. Guarded SQL is the only form that is correct for a fresh file **and** every legacy file, and it preserves the existing DDL verbatim instead of retyping it.

**Concrete anchors:**

- Server: move `Program.cs:83-152` into `Migrations/<ts>_InitialGuardedSchema.cs` as `migrationBuilder.Sql(...)`; delete the block, the `EnsureCreated()` at `Program.cs:83`, and the two per-service constructor calls (`DesignService.cs:20`, `DeployService.cs:40`).
- Workspace: move `EnsureAdditiveTables()`'s SQL into the SQLite baseline. The two `Scans` columns (`ManifestJson`, `CheckpointJson`) cannot be guarded in SQLite SQL (no procedural language, no `ADD COLUMN IF NOT EXISTS`), so keep a ~10-line C# repair (`WorkspaceSchemaRepair.AddLegacyColumns`) that runs **only when `__EFMigrationsHistory` is absent**, then `Migrate()`.
- Startup call site: [+] `DatabaseBootstrapper.Initialize(app.Services)` once in `Program.cs`; `WorkspaceDb` constructor does the same for SQLite.

**Trade-offs:** migration 1's guarded SQL must produce exactly the schema the model snapshot describes, or migration 2 will emit spurious `ALTER`s. That is enforced by a parity test (Pattern 2 test, below), not by hope.

**Test that makes it safe:**

```csharp
[Fact]
public async Task BaselineMigration_MatchesModel_ForFreshDatabase()
{
    // A: schema from the model
    await using var ensureCreated = NewContext(TempDb("a"));   // EnsureCreated()
    // B: schema from migrations
    await using var migrated     = NewContext(TempDb("b"));    // Database.Migrate()
    Assert.Equal(DescribeSchema(ensureCreated), DescribeSchema(migrated)); // sqlite_master / pragma_table_info
}
```

`DescribeSchema` is a test helper, not production code; for SQL Server it reads `INFORMATION_SCHEMA.COLUMNS` + `sys.indexes`.

### Pattern 3: Two-layer request limits (transport cap ≠ product contract)

**What:** the transport enforces a hard ceiling, the domain enforces the product rule, and both produce distinct, diagnosable failures.

| Layer | Mechanism | Failure the caller sees | Value |
|---|---|---|---|
| Transport | `AddGrpc(o => o.MaxReceiveMessageSize = 16 MB)`, Kestrel `MaxRequestBodySize`, `AddRateLimiter` policies | `ResourceExhausted` / HTTP 429 | Protects the process; cannot tell a design from a deploy payload |
| Domain | `AdsDesignPayload.TryValidate` in `DesignService.Save` | in-band `status_code = 413`-class + stable reason | Explains *which* limit and lets the client show a fix |
| Concurrency | `AdsConcurrencyInterceptor` (`SemaphoreSlim` keyed by `oid` + method) | `RpcException(ResourceExhausted)` | Stops one user opening 20 twenty-minute deploys |

**Why not one layer:** a 16 MB gRPC cap applied to an 8 MB design gives "resource exhausted" with no remediation; a 4 MB domain cap alone lets a caller send 200 MB before the service runs.

### Pattern 4: Scoped UI state beside the singleton canvas context

**What:** `AdsContext` stays a singleton (one canvas per process by design). New shell concerns are **scoped** services that *subscribe* to it rather than extending it:

```
AdsContext (singleton)  ── SelectionChanged ──►  AdsSelectionStore (scoped)
        ▲                                              │ Changed
        │ Diagram, CurrentDesignName                   ├──► DesignInspector
        └──────────────────────────────────────────────┴──► StatusBar
```

**Why scoped:** shell state is per-circuit (Desktop `BlazorWebView` circuit; WASM root scope). Putting theme/selection/collapse state onto `AdsContext` would make it survive sign-out and layout recreation, and would make the store untestable in isolation.

**Why a store at all:** `DesignInspector.razor` and `StatusBar.razor` each re-derive selection today by subscribing to `diagram.SelectionChanged` and calling `diagram.GetSelectedModels()` — the same wiring twice, and both capture `adsContext.Diagram` in `OnInitialized`, so a replaced diagram leaves them bound to a dead object (`AdsContext.Diagram` has a public setter). A store fixes the duplication *and* gives the Round 2 tenant forest the shared cursor `docs/top-100-ui-improvements.md` §2A requires.

**Trade-offs:** one more indirection for two components that could have subscribed directly. Accepted because the forest, cost rail, and change-plan scope all need the same cursor, and because a store is the only place a *re-bind* can live.

### Pattern 5: Owner-scoped command registration

**What:** `AdsCommandRegistry.RegisterRange(object owner, IEnumerable<AdsCommand>)` + `RemoveOwner(object owner)`; `TopMenu` implements `IDisposable` and removes its own commands.

**Why:** today `Program.cs:72` and `MauiProgram.cs:118` register `AdsCommandService` as a **singleton**, `TopMenu.razor.cs:43` registers closures over its own instance methods, and `TopMenu` implements no `Dispose`. Palette and inspector can therefore invoke a command whose target component is disposed. Owner keys make the lifetime explicit and make the registry a pure, testable type.

### Pattern 6: Provider and environment from configuration, not compilation symbols

**What:** `Database:Provider` (`Sqlite` | `SqlServer` | `InMemory`) + `Database:ConnectionString`, and `IsDevelopment()` for reflection/dev tools. Delete `#if DEBUG` around provider selection and schema bootstrap (`Program.cs:30-31`, `:58`, `:83-152`, `:183`).

**Why it is a prerequisite, not a nicety:** EF migrations cannot be generated or applied against the InMemory provider, and `AzureDesignStudio.Server.Tests` cannot exercise a relational schema while `Debug` means InMemory. This single change is what makes N17 and N3 testable at all. It also fixes the documented divergence where Debug and Release are different applications.

---

## Data Flow

### A. Save a design (after N26)

```
TopMenu.SaveDiagram                     [=] exists, [~] one line changes
  DataModelFactory.SaveDiagramToDto     [=]
  AdsDesignText.Write(graph, name)      [~] (was bare JsonSerializer.Serialize — no version field)
  AdsDesignPayload.TryValidate(text)    [+] client-side rejection with the same reason codes
      │
      ▼ gRPC-Web / native HTTP2  SaveDesignRequest{name, data}
DesignService.Save
  RpcException(Unauthenticated)         ← framework, from [Authorize]/FallbackPolicy (never reaches body)
  AdsIdentity.TryFrom(context)          [+] missing/invalid claim → status_code 401 (not a throw)
  ServiceTools.IsValidName              [=] name rules
  AdsDesignPayload.TryValidate(data)    [+] → 400-class + stable code; oversized → 413-class
  quota count ≥ 10                      [=] → 507 (existing contract)
  upsert AdsDesigns                     [=] (Add vs Update)
      │
      ▼
SQL Server (or SQLite in dev/tests) via DesignDbContext
```

**Load path:** `DesignService.Load` returns the stored string; `TopMenu.LoadDiagram` [+] calls `AdsDesignText.TryRead` first (versioned envelope), falling back to `JsonSerializer.Deserialize<DiagramGraph>` for rows written before this milestone. That fallback is the whole legacy-compatibility story and must be covered by a fixture row, because every existing row in production is a bare `DiagramGraph`.

### B. Identity per RPC (after N11)

```
HTTP request
  → Authentication (JwtBearer, MapInboundClaims pinned explicitly)
  → Authorization (FallbackPolicy = authenticated; [RequiredScope] per service)
  → gRPC service body
      AdsIdentity.TryFrom(context.GetHttpContext().User, out var id)
        ├─ oid claim  OR  .../objectidentifier claim     both accepted
        ├─ Guid.TryParse → false  → 401 in-band / PermissionDenied where the service throws
        └─ tid claim captured for tenant scoping (guardrails, approvals)
```

Fail-closed rules to preserve: `DesignService`/`DeployService` keep returning **in-band** `status_code` values. Do **not** convert a malformed subject into `RpcException(Unauthenticated)` — `DesignGrpcService.IsDeferredSaveFailure` treats `Unauthenticated` as "keep the design offline and retry later", so a permanently malformed claim would queue saves forever. A 401 in-band status produces a visible error instead.

### C. Guardrail policy read/write (after N10)

```
Write: SetGuardrailPolicy
  [Authorize(Policy = AdsPolicies.GuardrailsAdmin)]    [+] roles claim from Entra app role
  AdsIdentity.TryFrom → requires tid                   [+] no tenant → 403
  effective row = AdsGuardrailPolicies where TenantId == tid
    (fallback: legacy row Id=1, TenantId = NULL = inherited default)
  upsert with UpdatedBy/UpdatedByUserId/UpdatedAtUtc   [=] same columns as today

Read:  GetGuardrailPolicy → same resolver, read-only
Apply: deploy/what-if → resolver(tenant of the requesting identity) — never a client-supplied copy [=]
```

The `TenantId = NULL` legacy row is the compatibility contract: every tenant inherits it, so behaviour is unchanged on day one, and an admin who writes a tenant policy diverges deliberately.

### D. Workspace database open (after N17/N18)

```
Desktop MauiProgram.cs:58
  WorkspaceDb(path)
    options = WorkspaceDbContextFactory.CreateOptions(connString)   [+] single seam
    using db = CreateContext()
    Database.Migrate()                    [~] was EnsureCreated + ALTER + CREATE IF NOT EXISTS
      └─ if history table absent → WorkspaceSchemaRepair.AddLegacyColumns() first
    PRAGMA journal_mode=WAL               [+] persistent per file
    PRAGMA busy_timeout=5000              [+] per connection (interceptor on ConnectionOpened)
    PRAGMA foreign_keys=ON, synchronous=NORMAL
    PRAGMA quick_check                    [+] startup integrity; failure surfaces, never swallowed
    PRAGMA auto_vacuum=INCREMENTAL        [+] requires one VACUUM to take effect on existing files

MCP AdsWorkspace(path)    ReadOnly + shared cache today
    [+] reads __EFMigrationsHistory max id and compares to WorkspaceSchema.MinimumSupported
    [+] if ReadOnly open fails on a WAL file, retry ReadWrite + PRAGMA query_only=ON
       (SQLite cannot open a WAL database purely read-only unless the -shm file is
        already present and writable: https://sqlite.org/wal.html)
```

All workspace writers (`SqliteDesignOfflineStore`, `WorkspaceScans`, `WorkspaceChanges`, `WorkspaceDecomWindows`, `TopologyPositions`, `WorkspaceScanSink`, `TenantScanSession`) go through `WorkspaceDb.CreateContext()`, so the pragma/migration change is single-seam. `AdsWorkspace` is the only deliberate exception and must never write.

### E. Shell state and selection

```
ThemeToggle ──cycle──► AdsThemeService ──► AdsShellState.Changed ──► ThemeToggle/CommandPalette
                              │
                              └─► AdsTheme.cycle() (wwwroot/js/ads.js) + localStorage 'ads.theme'
                                  + data-ads-theme attribute; both hosts load ads.js
                                  (wwwroot/index.html, Desktop/wwwroot/app.html)

Canvas selection ──► AdsSelectionStore.Changed ──► DesignInspector, StatusBar, (future forest)
Command palette ──► AdsCommandRegistry.TryGet(key) ──► TopMenu handler
```

**Boundary rule:** `AdsHostFeatures` answers *"what can this host do?"* (tenant scan, live name checks). `AdsShellState`/`AdsSelectionStore` answer *"what is this circuit showing?"*. Nothing new goes into `AdsHostFeatures` this milestone; nothing host-specific goes into the shell stores.

### F. CI → signed MSIX

```
push/PR ──► build.yml
  linux  : build Server, WASM, Mcp, Core.Tests + offline tests + Docker + CodeQL   [=]
  windows: [+] dotnet build Desktop -c Release (cache NuGet + maui-windows workload)
  tests  : [+] dotnet test Server.Tests (SQLite) / UI.Tests (bUnit) / Core.Tests
  sqlsrv : [+] service container mcr.microsoft.com/mssql/server → migration parity tests

tag v* ──► publish.yml
  stamp  : [+] version from `nbgv get-version -p Version` (4-part), anchored to <Identity …>
           [~] today's `-replace 'Version="\d+\.\d+\.\d+\.\d+"'` is unanchored: the pattern matches
               three places in the manifest (Identity version + both MinVersion attributes,
               verified with `grep -oP`), so stamping also rewrites the minimum OS version
  publish: dotnet publish -p:WindowsPackageType=MSIX -p:RuntimeIdentifier=win-x64 [=]
  sign   : [+] Publisher in Package.appxmanifest must equal the signing certificate Subject
           (today "CN=Azure Design Studio" is a placeholder)
  verify : [+] install on a clean Windows runner/VM, launch, assert window title + no console errors
```

---
## Build Order

Order is chosen by **failure class retired per unit of change**, and by which changes would otherwise be rewritten. Three rules drive it: (1) a schema source change must precede any entity change; (2) a test project must exist before the code it is supposed to defend is called done; (3) nothing in the Desktop/CI track blocks on server work.

```
S0 land tree
  └─► S1 build policy
        ├─► S2 server bootstrap ─► S3 migrations + WAL ──────────────┐
        │        └─► S4 identity + crypto ─► S5 limits + payload ────┴─► S6 guardrail scoping
        │                                                                (needs S3 + S4 + S5)
        ├─► S8 shell state            (independent of server work)
        └─► S9 Windows CI + MSIX      (needs S1; signing decision needs S0)

S7 test suites: skeleton created with S2; one suite lands with each of S3, S4, S5, S6.
```

### S0 — Land the slice (no architecture change)

Commit the working tree in three chunks (core / shell / docs), remove `bd2.dll` + `bd_served.dll`, untrack agent scratch, push, take a `git bundle`. **Why first:** 120 commits and ~54 paths exist only on one machine; every later step produces diffs that must be reviewable against a clean base.

### S1 — Build policy and versioning (N47 partial)

[+] `global.json` (pin `10.0.401`, `rollForward: latestPatch`), `src/Directory.Build.props` (nullable, analyzers, `LangVersion`), `src/Directory.Packages.props` (CPM incl. one `MicrosoftAspNetCoreVersion`). Hoist `Nerdbank.GitVersioning` so **every** project — including `AzureDesignStudio.Desktop`, which has it today only transitively from the WASM project — reports the same version.

**Why here:** S3 and S7 add four packages (`Microsoft.EntityFrameworkCore.Design`, `Microsoft.AspNetCore.Mvc.Testing`, `bunit`, `Microsoft.Playwright`) and one new project consuming nbgv. Adding them before centralisation bakes in the drift CONCERNS already records (`10.0.12` vs `10.0.13`).

**Proof:** `dotnet build src/AzureDesignStudio.Desktop` resolves one version for `Microsoft.Extensions.*`; `nbgv get-version -p Version` returns a 4-part version.

### S2 — Server bootstrap restructure (no behaviour change)

[~] `Program.cs`: provider from `Database:Provider`/`Database:ConnectionString`; `#if !DEBUG` Key Vault block becomes `if (!builder.Environment.IsDevelopment())` + a `DatabaseBootstrapper` call site; [+] `public partial class Program { }`; [+] `ProjectReference` **Server → Core** (today only transitive via the WASM project, which is how `Server` accidentally depends on the entire browser app's asset graph). [+] `Models/DesignDbContextFactory.cs`.

**Why here:** S3 cannot generate migrations while `DEBUG` selects InMemory, and S7 cannot host the app without `Program` being reachable. Doing S2 and S3 in one PR would mix an untestable refactor with an irreversible schema change.

**Proof:** `dotnet run` with `Database:Provider=Sqlite` creates a file DB and serves gRPC; the offline suite still passes.

### S3 — Migrations + WAL, both databases (N17 + N18, one PR)

Server: [+] `Migrations/` baseline (guarded SQL moved from `Program.cs:83-152`), [+] `Persistence/DatabaseBootstrapper.cs` (`Migrate()` + legacy baseline handling), [~] `dotnet-tools.json` `dotnet-ef` **6.0.4 → 10.0.x** (currently pins EF 6 against EF Core 10 packages — verified), [+] `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets=all`, delete all `EnsureCreated()` calls.

Workspace: [+] `WorkspaceDbContextFactory` (options + pragma interceptor), [+] `Migrations/` baseline, [~] `WorkspaceDb` ctor (`Migrate()` + `quick_check` + WAL), delete `EnsureAddedColumns`/`EnsureAdditiveTables` after the migration captures them, [~] `MauiProgram.cs:58` unchanged call site, [~] `AdsWorkspace` version handshake.

**Why here:** S6 adds a column to `AdsGuardrailPolicies`; doing that before migrations means writing a *fourth* hand-maintained DDL statement. After S3 it is migration 2.

**Proof:** a committed `workspace.legacy.db` fixture upgrades with row counts unchanged; a containerised SQL Server built from the checked-in legacy DDL (`tests/fixtures/legacy-server-schema.sql`) upgrades with row counts unchanged; parity test from Pattern 2 passes for both providers; concurrent read during a scan write no longer throws `SQLITE_BUSY`.

### S4 — Identity + local crypto (N11 + N12)

[+] `Server/Auth/AdsIdentity.cs`; [~] replace `ServiceTools.GetUserId` (delete it) and every `new Guid(userIdClaim)`; add `MapInboundClaims` explicitly next to `NameClaimType` (`Program.cs:21-27`). [+] `LocalCryptoService`/`NullCryptoService` behind `ICryptoService`, selected by environment, refusing dev keys outside Development.

**Why here:** S5's limits partition by `oid`, S6's policies need `tid`, and S7's integration tests need to forge tokens with both claim shapes. It is also the smallest change that removes a whole class of 500s.

**Proof:** missing claim → in-band 401; non-GUID `oid` → 401 (not `RpcException`); subscription registration succeeds in Development with no Key Vault.

### S5 — Limits, health, payload contract (N14 + N26)

[~] `Program.cs` pipeline exactly as ordered above; [+] `Auth/AdsConcurrencyInterceptor.cs`; [+] `Core/Common/AdsDesignPayload.cs`; [~] `TopMenu.SaveDiagram` to write the versioned envelope; [~] `DesignService.Save`/`Load` to validate; [~] `AdsDesignText` `TryRead` for legacy rows.

**Why here:** it needs S2's pipeline and S4's identity (partition key). It also *creates* the 16 MB transport cap that S6's guardrail writes must fit under.

**Proof:** 20 MB payload → `ResourceExhausted`; 5 MB design → in-band 413-class code; burst → 429; `/health/live` and `/health/ready` answer unauthenticated; a pre-milestone design row still loads.

### S6 — Guardrail scoping + approvals (N10)

[~] `GuardrailPolicyModel` (+`TenantId`, nullable = inherited default), [+] `Auth/AdsPolicies.cs` (`FallbackPolicy` + `Guardrails.Admin` role policy + `AllowAnonymous` on `RetailPriceService`), [~] `SetGuardrailPolicy`/`GetGuardrailPolicy` to resolve by tenant, [~] `PlanApprovalModel`/`DecomApprovalModel` (+`TenantId`), [~] same-tenant check on apply.

**Why here:** last of the server items because it needs S3 (schema), S4 (tenant claim) and S5 (fallback policy). It is also the one item whose failure mode is a cross-tenant data exposure, so it lands on a tested base.

**Proof:** non-admin authenticated caller → 403; two tenants cannot read or overwrite each other's policy; the legacy `Id=1` row still governs a tenant that has never written one.

### S7 — Test suites (attach alongside S2–S6, never after)

| Project | Kind | References | Covers | Runs |
|---|---|---|---|---|
| `AzureDesignStudio.Core.Tests` [=] | existing | Core, Workspace, Discovery | payload contract, registry, migrations on SQLite, engine regressions | every PR, all OSes |
| `AzureDesignStudio.Server.Tests` [+] | `Microsoft.NET.Sdk`, xunit | Server + `Microsoft.AspNetCore.Mvc.Testing` + `Grpc.Net.Client` | identity 401/403, quota, payload limits, guardrail authZ, token single-use, approval ownership, migration upgrade | every PR (SQLite); SQL Server subset on the container job |
| `AzureDesignStudio.UI.Tests` [+] | `Microsoft.NET.Sdk.Razor`, xunit + `bunit` 2.x | `src/AzureDesignStudio` | palette filter/keyboard, inspector selection, status bar counts, theme cycle, command registry lifetime | every PR |
| `AzureDesignStudio.E2E.Tests` [+] | xunit + `Microsoft.Playwright` | none (drives processes) | WASM: boot, stencil drop, link, undo/redo, export, palette, theme, zero console errors. Desktop: same over WebView2 CDP | WASM every PR; Desktop nightly + Windows PRs |

**Why the server suite starts at S2, not S7:** a test project created after the security work is written cannot drive the design of `Program.cs`'s testability (`partial class Program`, provider by config, auth override). The first three tests should exist before S3 lands.

**Server test wiring — the hazards, concretely:**

- Host: `WebApplicationFactory<Program>` with `WithWebHostBuilder(b => b.ConfigureTestServices(...))`; supply dummy `AzureAd:ClientId`/`TenantId`/`Scopes` so `AddMicrosoftIdentityWebApi` binds, then replace `AuthenticationOptions.DefaultAuthenticateScheme`/`DefaultChallengeScheme` with a `TestAuthHandler`.
- Channel: `GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() })` — the documented approach for gRPC over `TestServer` (https://learn.microsoft.com/en-us/aspnet/core/grpc/test-services).
- Database: `Database:Provider=Sqlite` with a temp file per test class and `SqliteConnection.ClearAllPools()` in `Dispose` — the exact convention already used by `WorkspaceTests`/`ChangeFeedTests`.
- Don't assert on `EnsureCreated`-shaped schema; assert on the migrated schema.

**UI test wiring:** `AdsContext` must be seeded through its existing seam (`StencilLoader = () => Task.FromResult(stencils)`) rather than the `"AzureDesignStudio.Root"` HTTP client; register `AddAntDesign()`, a stub `IMapper`, `IApplicationInsights`, `AuthenticationStateProvider`, and bUnit's JSInterop in `Strict` mode with `AdsTheme.*` handlers. Components must be asserted through rendered markup and store events, never through `diagram` internals.

**E2E wiring, and the one thing not to do:** do **not** add an auth bypass to production code. The WASM suite authenticates nothing and covers the unauthenticated canvas/export/palette/theme surface; authenticated save/load is covered by `Server.Tests`; the Desktop suite uses `AdsHostAuth`/`AdsTokenBroker` seams plus a seeded local workspace (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, `playwright.chromium.connectOverCDP`).

### S8 — Round 2 shell state (independent track)

Order inside: (1) registry → `Core` + owner keys + `TopMenu.Dispose`; (2) `AdsSelectionStore` adopted by `StatusBar`/`DesignInspector`; (3) `AdsShellState` + `AdsThemeService` adopted by `ThemeToggle`/`MainLayout`; (4) palette consumes the registry only.

**Why independent:** none of it touches the server, and it removes the two lifetime bugs (`TopMenu` closures, duplicated selection wiring) that the assessment flagged as live. Per `docs/next-phases-plan.md`, only the already-started shell surfaces continue before the gate closes; no new surfaces (forest, cost rail) start here.

**Proof:** bUnit tests for registry ownership (register → remove → palette no longer lists it) and for store re-bind after `AdsContext.Diagram` replacement; manual: drop a stencil after a drawer remount.

### S9 — Windows CI + signed MSIX (track parallel to S2–S6)

1. [+] `windows-latest` job in `build.yml` building `src/AzureDesignStudio.Desktop -c Release`, `continue-on-error: true` for the first runs, caching NuGet + the `maui-windows` workload (the assessment measured a warm local Release build exceeding 11 minutes — budget from measurement, not from the plan's 20-minute target).
2. [+] vulnerable-package gate (`dotnet list package --vulnerable --include-transitive`) with one dated allowlist entry for AutoMapper 12.0.1.
3. [~] `publish.yml`: replace the unanchored manifest regex with `nbgv`'s 4-part `Version`; decide the certificate strategy; add a clean-machine install assertion.

**Why the signing decision must be explicit, not implicit:** `Package.appxmanifest` declares `Identity Publisher="CN=Azure Design Studio"`, which is not a real certificate subject. A package signed with any other subject cannot be installed, no matter how valid the signature. Options, with the constraint that matters:

| Option | Constraint | Verdict for this milestone |
|---|---|---|
| Self-signed cert, trust installed on pilot machines | Tester must import the `.cer`/`.pfx` into `LocalMachine\TrustedPeople`; packages are per-machine-trust, not portable (https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide) | [=] acceptable for a controlled pilot; document the trust step |
| OV code-signing certificate (PFX) | ~$300–500/yr; PFX custody in secrets (already the workflow's shape) | [=] fallback if Artifact Signing is unavailable |
| Azure Artifact Signing (formerly Trusted Signing) | Org validation; **individual developers only in USA/Canada**, orgs in USA/CA/EU/UK; needs a certificate profile + *Trusted Signing Certificate Profile Signer* role; `azure/trusted-signing-action@v0` (same guide) | preferred if eligibility holds — no PFX to custody |

In every case the manifest `Publisher` must be set to the certificate's subject **before** the pilot build, and the version must increase monotonically across installs.

### Dependency summary

| Step | Hard depends on | Blocks |
|---|---|---|
| S0 | — | everything (reviewability) |
| S1 | S0 | S3, S7, S9 |
| S2 | S1 | S3, S5, S7 (server) |
| S3 | S2 | S6, SQLite migration tests |
| S4 | S2 | S5 (partition key), S6 (tenant), S7 (auth tests) |
| S5 | S2, S4 | S6 (fallback policy) |
| S6 | S3, S4, S5 | — |
| S7 | S2 (skeleton), grows per step | gate exit |
| S8 | S0 | Round 2 completion |
| S9 | S1, S0 | MSIX deliverable |

---

## Test topology (why the single-project layout survives)

The milestone adds three projects but **does not** re-partition the 755 existing cases. The split is by *what must be hosted*, not by layer:

- `Core.Tests` stays the home of everything that can run in-process with no host: engines, models, serializers, the payload contract, the command registry, and SQLite migration tests (a temp file is not a host).
- `Server.Tests` exists because a gRPC service body cannot be exercised without the ASP.NET pipeline (auth, fallback policy, rate limiter, message caps, interceptors). Anything that only needs `new DesignService(ctx, logger)` belongs in `Core.Tests` — but `Core.Tests` does not reference Server today and should not start; the service-level tests live with the host.
- `UI.Tests` exists because bUnit owns the render tree; it must not become a home for logic that could be a plain class. The registry moves to `Core` precisely so its tests stay framework-free.
- `E2E.Tests` exists because nothing else proves the composition (both hosts, both HTML entry points, service worker, theme bootstrap).

**Traits:** `Category=LiveAzure` (existing, excluded), [+] `Category=SqlServer` (container only), [+] `Category=E2E` (nightly/Windows), [+] `Category=Windows` (Desktop-only). The PR gate is `Category!=LiveAzure&Category!=SqlServer&Category!=E2E&Category!=Windows`, which keeps the current sub-3-second feedback loop and puts cost where it buys coverage.

**Coverage:** `coverlet.collector` is already referenced (`Core.Tests.csproj:21`) and never invoked. Wire `--collect:"XPlat Code Coverage"` into the test jobs and gate *new-code* coverage at 80 % only after S7's suites exist — a threshold set before the suites exist either fails constantly or is set to zero.

---

## Scaling Considerations

This milestone must not add state that assumes one replica; the cluster runs two (`src/Configurations/main/deployment.yaml:8`) and the in-process `WhatIfTokenGate` already breaks deploys at that count.

| Concern | Today | After this milestone | What breaks first |
|---|---|---|---|
| Rate limiting | none | in-memory token bucket **per pod** | Effective limit is `N × configured` at two replicas — acceptable for abuse control, wrong as a quota. Do not advertise it as a quota. |
| Concurrency cap | none | in-memory semaphore per pod | Same `N ×` effect; fine for protecting one process. |
| What-if tokens | in-process `Dictionary` (`WhatIfTokenGate`) | unchanged by this milestone | ~half of deploys fail at two replicas. Either pin `replicas: 1` in the manifest or move the gate to the DB — decide explicitly, do not leave it implicit. |
| Guardrails | one global row | one row per tenant + inherited default | First genuinely multi-replica-safe change (state is already in SQL). |
| Designs | `nvarchar(max)` per row, 10/user by count query | + 4 MB logical cap | Concurrent saves can still exceed the quota by one; a unique index on `(UserId, Name)` is the fix and needs a migration (S3 is the enabler). |
| Workspace | rollback journal, `Cache=Shared`, concurrent writers | WAL + `busy_timeout` | Unbounded growth stays the real limit; retention (N19) is out of this milestone but its absence is why WAL is necessary, not sufficient. |
| MSIX distribution | tag-triggered, optionally signed | signed + versioned | Certificate expiry and Publisher/subject mismatch silently produce uninstallable packages — assert install in CI. |

**Scaling priorities:** (1) stop adding in-process state; (2) make schema changes reviewable before they become urgent; (3) keep the WASM cold-start budget honest — the desktop gate does nothing for a browser host that still prints "Loading. It may take a minute or two."

---

## Anti-Patterns

### Anti-Pattern 1: Adding a fifth schema source

**What people do:** to add `TenantId` to `AdsGuardrailPolicies`, append one more `IF COL_LENGTH(...) ALTER TABLE` statement to `Program.cs`.
**Why it's wrong:** the block already has six guarded statements and no version, no rollback, and no test; a forgotten guard means the column never appears on an existing database, and the app fails at query time, not startup.
**Do this instead:** land S3 first; every subsequent entity change is a generated migration with a legacy-fixture test.

### Anti-Pattern 2: Normalizing identity inside each RPC

**What people do:** keep `ServiceTools.GetUserId` and add `Guid.TryParse` at the 26 call sites.
**Why it's wrong:** 26 chances to forget; the tenant id is needed by the very next feature (guardrails, approvals); and a per-service fix drifts in its error contract.
**Do this instead:** one `AdsIdentity.TryFrom(context)`, with an analyzer-level review rule (a simple `grep` gate in CI: no `new Guid(` in `src/AzureDesignStudio.Server`).

### Anti-Pattern 3: Treating `AdsHostFeatures` as a settings bag

**What people do:** add `ThemeMode`, `SelectedNodeId`, or `Density` to `AdsHostFeatures` because both hosts already construct it.
**Why it's wrong:** `AdsHostFeatures` is a *capability* descriptor captured once at host build; a null member means "this host cannot do X". Values that change per interaction would never re-render consumers, and the WASM empty registration would silently read as "feature absent".
**Do this instead:** per-circuit state in `AdsShellState`; host capability only when the *host* differs.

### Anti-Pattern 4: Making the command registry scoped and calling the lifetime fixed

**What people do:** change `AddSingleton<AdsCommandService>` to `AddScoped` and declare the disposed-owner bug solved.
**Why it's wrong:** the circuit scope outlives `TopMenu` remounts, so a disposed component's closures remain reachable; and two components in one circuit still overwrite each other's keys silently.
**Do this instead:** owner keys + `RemoveOwner` in `TopMenu.Dispose`, plus a test that asserts removal.

### Anti-Pattern 5: Hiding a WAL/migration incompatibility behind a retry

**What people do:** wrap the MCP's SQLite open in a retry loop until it succeeds, or open read-write everywhere.
**Why it's wrong:** the failure is deterministic (a WAL database cannot be opened read-only without a writable `-shm`), so retries turn a clear error into a hang, and a read-write open erodes the "this server never mutates the workspace" guarantee the MCP contract is built on.
**Do this instead:** explicit fallback — `ReadOnly`, then `ReadWrite` + `PRAGMA query_only=ON`, with the mode reported in the tool error text.

---

## Integration Points

### External services

| Service | Integration pattern | Notes |
|---|---|---|
| Microsoft Entra ID | `AddMicrosoftIdentityWebApi` (server), MSAL/WAM (desktop), MSAL.js (WASM) | Roles arrive as `roles` claims only if the API app registration defines app roles; `tid` must be read from the token, not from config, or cross-tenant isolation is fiction |
| Azure Key Vault | `CryptoService` (RSA-OAEP), configuration source in non-Development | S4 must keep the production path unchanged and add a dev provider; the OAEP plaintext ceiling (~190 bytes at RSA-2048) is an existing constraint, not something this milestone fixes |
| Azure Resource Manager / Resource Graph | `DeployService`, `Discovery` transports | Out of scope for this milestone except that the concurrency cap must not break the 20-minute deploy stream |
| GitHub Actions | `build.yml`, `publish.yml`, CodeQL | Windows job and SQL Server service container are the only new primitives |
| MSIX signing (Artifact Signing or OV cert) | `signtool` / `azure/trusted-signing-action@v0` | Publisher must equal the certificate subject; version must be monotonic |
| NuGet | per-project `PackageReference` → CPM after S1 | A lock file decision belongs with S1, not with the test projects |

### Internal boundaries (and what may cross them)

| Boundary | Communication | Rule for this milestone |
|---|---|---|
| Shared UI ↔ Server | protobuf only (`SharedModels`) | No new message fields this milestone; the payload contract is a JSON string inside `SaveDesignRequest.data` |
| Shared UI ↔ Desktop host | `AdsHostFeatures`, `AdsHostAuth`, `IDesignOfflineStore`, `IScanSink` | Any new Desktop-only surface is a delegate, never a project reference |
| Server ↔ Core | [+] new direct reference (S2) | Server may use Core's domain/contracts; Core must never reference Server, Workspace, or Discovery |
| Core ↔ Workspace | `IDesignOfflineStore` contract in Core, implementation in Workspace | Do not move workspace entities into Core to reuse them in the payload validator |
| Desktop ↔ MCP ↔ workspace file | the SQLite file itself | Multi-process coordination is a *schema/pragma contract*, not a code contract — the version handshake is the only guard |
| Test projects ↔ production | `ProjectReference` + public seams | If a test needs `InternalsVisibleTo` on Server, the seam is wrong; make the helper public in `Server/Auth` instead |

---

## Confidence and Open Questions

| Area | Confidence | Basis | Open question the roadmap must close |
|---|---|---|---|
| Where each server capability attaches | HIGH | Read the exact files; pipeline order is visible in `Program.cs:155-194` | Does gRPC-Web preserve `grpc-timeout` so a client deadline cancels server work? Verify with one integration test before claiming "deadlines are live" |
| Migration coexistence (baseline-as-guarded-SQL) | HIGH for SQLite, MEDIUM for SQL Server | SQLite guarded DDL is the existing text; SQL Server guard syntax is the existing text too, but a legacy SQL Server schema built by `EnsureCreated` must be diffed against the baseline before migration 2 is generated | SQL Server migration parity needs a container and a checked-in legacy schema fixture — no such fixture exists yet |
| WAL + MCP read-only | HIGH (documented limit), MEDIUM (workaround) | sqlite.org/wal.html | Whether `ReadWrite + query_only=ON` is acceptable to the "MCP never writes" contract — decide and document in the MCP tool surface |
| Windows CI + MSIX | HIGH for the mechanics, MEDIUM for duration and eligibility | Learn signing guide; assessment's 11-minute local build | Build duration on a cached `windows-latest` runner and Artifact Signing eligibility (individual devs: USA/Canada only) |
| bUnit over a WASM app project | MEDIUM | Precedent: Server already references the WASM project | First bUnit project must prove it builds on Linux CI without dragging Blazor static-web-asset work into the test output |
| Shell state split (`AdsShellState`/`AdsSelectionStore`) | MEDIUM-HIGH | Duplicated subscription wiring is visible in both components; the forest requirement is stated in `docs/top-100-ui-improvements.md` §2A | Whether the tenant forest can be built on the same store without a second cursor — a Round 2 Phase 1 design question, not a gate question |

**Not verified in this pass:** no build, test, or packaging command was run (research-only assignment). Desktop Release build duration, MSIX signing, and the SQL Server migration path all remain unmeasured by this document.

---

## Sources

**Repo evidence (read directly; HIGH confidence):** `src/AzureDesignStudio.Server/Program.cs:18-194`, `Services/DesignService.cs:20-120`, `Services/ServiceTools.cs`, `Services/DeployService.cs:1261-1360`, `Models/DesignDbContext.cs`, `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs (WorkspaceDb ctor + EnsureAddedColumns/EnsureAdditiveTables)`, `src/AzureDesignStudio.Mcp/AdsWorkspace.cs`, `src/AzureDesignStudio.Core/Common/AdsDesignText.cs`, `src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio.Desktop/MauiProgram.cs:29-143`, `src/AzureDesignStudio/Components/TopMenu.razor.cs:1-50,320-500`, `Services/AdsCommandService.cs`, `Components/{StatusBar,DesignInspector,ThemeToggle}.razor`, `Shared/MainLayout.razor`, `Pages/Index.razor`, `.github/workflows/{build,publish}.yml`, `src/version.json`, `src/AzureDesignStudio.Server/.config/dotnet-tools.json`, `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest`, `src/AzureDesignStudio.sln`. Cross-checked with `.planning/codebase/{ARCHITECTURE,CONCERNS,TESTING}.md`, `docs/v3-plan.md`, `docs/assessment-2026-10-21.md`, `docs/next-phases-plan.md`, `docs/top-100-ui-improvements.md`.

**External (consulted 2026-10-21):**

- MSIX signing options, self-signed trust step, Artifact Signing eligibility and `azure/trusted-signing-action` — https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide (HIGH)
- gRPC integration testing over `TestServer` with `TestServer.CreateHandler()` — https://learn.microsoft.com/en-us/aspnet/core/grpc/test-services (HIGH)
- SQLite provider migration limitations (no in-place `ALTER COLUMN`, rebuilds only for model artifacts, no idempotent scripts, `__EFMigrationsLock`) — https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations (HIGH)
- WAL databases cannot be opened purely read-only without a writable `-shm` — https://sqlite.org/wal.html (HIGH)
- `ApplicationDisplayVersion` does not flow into the Windows `Package.appxmanifest` — https://github.com/dotnet/maui/issues/18571 (MEDIUM; corroborated by the Learn publish guidance stating the MSIX package is configured by the manifest)
- bUnit current major version (2.x, 2.12.5 at research time) — https://www.nuget.org/packages/bunit/ (HIGH for the version, MEDIUM for the bUnit + WASM-project-reference combination; validated by the existing Server → WASM project reference precedent)

---
*Architecture research for: Azure Design Studio — subsequent milestone (Trust Gate, Round 2 shell, signed MSIX)*
*Researched: 2026-10-21*


