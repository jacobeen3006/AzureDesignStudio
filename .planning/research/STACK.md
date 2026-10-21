# Stack Research

**Domain:** Brownfield .NET 10 desktop + server — Trust Gate hardening and signed MSIX delivery
**Researched:** 2026-10-21
**Confidence:** HIGH for package/version facts and Microsoft-documented behavior; MEDIUM for the judgement calls flagged inline (gRPC rejection contract, CSP hash pipeline, xunit v2 vs v3)

> **Scope.** This file is a **delta against `.planning/codebase/STACK.md`** (mapped at `a05ef8c`, 2026-10-21). It does not restate the existing Blazor WASM / MAUI / gRPC / EF Core / Azure SDK stack. It covers only what this milestone adds or must change: Windows CI + MSIX, EF Core migrations + SQLite durability, server hardening, the three new test layers, and the AutoMapper exit.

Each recommendation carries a **Conf.** column: **H** = verified against package metadata or Microsoft primary docs today; **M** = verified facts, judgement call in the choice; **R** = repo-evidence-based, not externally verified.

---

## 0. Delta summary (read this first)

| Item | Today | Change | Why it is unavoidable |
|---|---|---|---|
| AutoMapper | 12.0.1 + `NoWarn=NU1903` in `src/AzureDesignStudio/AzureDesignStudio.csproj` and `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj` | **Remove** AutoMapper; emit mappings from the existing Roslyn generator | 12.0.1 is affected by CVE-2026-32933; the only patched versions (15.1.1/16.1.1) are RPL-1.5 + paid commercial licence |
| `dotnet-ef` | 6.0.4 in `src/AzureDesignStudio.Server/.config/dotnet-tools.json` | 10.0.13, manifest moved to `src/.config/` | EF tools 6 cannot read an EF 10 model |
| Schema bootstrap | `EnsureCreated()` + hand-rolled `ALTER TABLE`/`CREATE TABLE` in `WorkspaceDbContext` ctor and `Program.cs:73-157` | `Migrations/` for both contexts + `Migrate()` | No upgrade/rollback story; baseline required before any schema change |
| SQLite mode | `Data Source=…;Cache=Shared` (`WorkspaceDbContext.cs:84-89`, `AdsWorkspace.cs:22-26`) | WAL + `busy_timeout` + `foreign_keys`, `Cache` removed | Mixing shared cache and WAL is explicitly discouraged by Microsoft.Data.Sqlite docs |
| ASP.NET pipeline | `UseHsts`/`UseHttpsRedirection` only (`Program.cs:160-192`) | Rate limiter, request timeouts, size caps, health endpoints, CSP headers | All in-framework in .NET 10 — zero new runtime dependencies |
| Desktop CI | Only `.github/workflows/publish.yml` (tags) | `windows-latest` PR job + signed release | N1/N2 gate; MSIX must be verifiable |
| Tests | `Core.Tests` only (xUnit 2.9.2 + coverlet) | `Server.Tests`, `Components.Tests`, `E2E.Tests` | 3,000 server lines and every Razor component have no coverage |
| Build inputs | No `global.json`, no CPM, no lock files | `global.json`, `Directory.Packages.props`, `packages.lock.json` | Runner drift and `setup-dotnet` caching both need them |

Confidence: **H**

---

## 1. Recommended Stack

### 1.1 Windows CI, MAUI build, signed MSIX

| Technology | Version | Purpose | Why Recommended | Conf. |
|---|---|---|---|---|
| `actions/setup-dotnet@v4` | n/a | SDK selection | Already used in both workflows; pin via `global.json` instead of relying on `10.0.x` | H |
| `global.json` (`sdk.version`) | `10.0.400`, `rollForward: latestFeature` | Deterministic SDK | `windows-latest` (Windows Server 2025 image) ships SDKs 10.0.112 → **10.0.401**; Ubuntu runners lag differently. Pin the feature band, allow patch roll-forward | H |
| `dotnet workload restore` | SDK 10.0.4xx | Installs `maui-windows` for .NET 10 | The `windows-2025` image only carries VS 2022 with **MAUI 9.0.111** workloads — the net10 MAUI packs are *not* present. `publish.yml` already installs `maui-windows`; switch to `dotnet workload restore <desktop csproj>` so the workload set comes from the project, not a hard-coded id | H |
| `signtool.exe` (Windows SDK) | SDK `10.0.26100.0` (preinstalled) | Package signing + verification | Present at `%ProgramFiles(x86)%\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe` on the runner; no install step | H |
| **Azure Artifact Signing** via `azure/trusted-signing-action@v0` | v0 | Production MSIX signing | Microsoft's documented production path; reputation binds to verified identity, not a per-cert thumbprint. Requires `id-token: write` + OIDC federated credential — no long-lived PFX secret | H |
| PFX + `signtool sign /fd SHA256 /tr … /td SHA256` | n/a | **Pilot fallback only** | Acceptable when Artifact Signing is not available (region/eligibility). Requires the `.cer` to be installed into `TrustedPeople` on every pilot machine | H |
| `actions/upload-artifact@v4` + `signtool verify /pa /v` | v4 | Artifact integrity gate | Upload must be gated on verification, and the SHA-256 manifest must ship beside the `.msix` | H |
| `MakeAppx.exe` (Windows SDK) | `10.0.26100.0` | Manifest inspection in CI | `MakeAppx unpack` → assert `Identity/@Publisher` matches the signing certificate subject before signing | M |

**Repo wiring — MSIX**

- `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest` has `Publisher="CN=Azure Design Studio"` and `Identity Name="AzureDesignStudio.Desktop"`. **Both are identity-critical:** switching to Artifact Signing requires rewriting `Publisher` to the verified identity's distinguished name, and `Identity/@Name` must never change or installed copies stop upgrading. Add a CI assertion that the signer's subject equals the manifest `Publisher`.
- `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` sets `<WindowsPackageType>None</WindowsPackageType>` globally. Keep it — packaged CI runs must pass `-p:WindowsPackageType=Package` (the value the MAUI docs use; `MSIX` is the legacy alias `publish.yml` currently passes) so unpackaged test runs stay the default.
- `publish.yml` stamps the manifest by regexing `Version="\d+\.\d+\.\d+\.\d+"`. Replace with an XML-node edit plus a validation that the tag is a 4-part version, and pass `-p:ApplicationDisplayVersion=<display>` from `nbgv get-version`. `src/version.json` already drives `Nerdbank.GitVersioning` 3.6.146 — use it as the single version source; the regex silently mis-stamps if the manifest formatting changes.
- `publish.yml` publishes with `-p:RuntimeIdentifier=win-x64`. MAUI docs require `-p:RuntimeIdentifierOverride=win-x64` to avoid WindowsAppSDK issue #3337 during packaging. Switch to `RuntimeIdentifierOverride` and keep `-p:WindowsAppSDKSelfContained=true -p:SelfContained=true` (pilot machines get no runtime prerequisite; cost is a ~150 MB+ package).
- `publish.yml` signs **after** publish with `signtool`. That is compatible with the MSIX format, but the publish step must not attempt its own signing: pass `-p:AppxPackageSigningEnabled=false` (or a `PackageCertificateKeyFile`) explicitly rather than relying on the default. Post-publish signing is what makes Artifact Signing usable, since the dlib plugin signs a finished file.
- Add `desktop` job to `.github/workflows/build.yml` on `windows-latest`, **not** a replacement for the Ubuntu job. Sequence: restore → `dotnet workload restore` → `dotnet build -c Release -f net10.0-windows10.0.19041.0` → pack unsigned MSIX → upload artifact. Signing stays tag-only. Budget: the workload install dominates (~3-6 min); the build itself is a few minutes.
- Cache: do **not** cache the MAUI workload (it installs into the `setup-dotnet`-provisioned `DOTNET_ROOT`, which is recreated per run). Cache NuGet through `actions/setup-dotnet`'s `cache: true`, which requires lock files — see §1.6. Fallback is `actions/cache` keyed on `hashFiles('src/**/*.csproj', 'src/version.json', 'global.json')` over `~/.nuget/packages`.

### 1.2 EF Core 10 migrations, SQLite durability and backup

| Technology | Version | Purpose | Why Recommended | Conf. |
|---|---|---|---|---|
| `Microsoft.EntityFrameworkCore.Design` | 10.0.13 | Design-time services for `dotnet ef` | Must match the 10.0.13 runtime packages; `PrivateAssets=all` | H |
| `dotnet-ef` (tool) | 10.0.13 | Migration generation/apply | Current tool band; 6.0.4 in the repo cannot read an EF 10 model | H |
| `Microsoft.EntityFrameworkCore.SqlServer` | 10.0.13 | Server provider | Bump from 10.0.12 with the rest of the EF set | H |
| `Microsoft.Data.SqlClient` | **7.1.1** (direct pin, Server only) | SQL client | EF 10's SqlServer provider floors SqlClient at 6.1.7, which adds `Microsoft.Identity.Client.Broker` and inflates the Linux container. Pin 7.1.x — the documented mitigation when the app does not use Entra ID auth through SqlClient (this server uses a connection string) | H |
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.13 | Workspace + MCP provider | Bump from 10.0.12 | H |
| `Microsoft.EntityFrameworkCore.InMemory` | 10.0.13 | Server service-layer tests only | Fine for `DesignService`/`DeployService` behavior; **not** for migrations | H |

**Repo wiring — migrations**

- **Server** (`src/AzureDesignStudio.Server`): add `IDesignTimeDbContextFactory<DesignDbContext>`. This is not optional here: `Program.cs:27-44` throws in non-DEBUG without `KeyVaultName` and constructs a `DefaultAzureCredential` → `SecretClient` at startup. Without a design-time factory, `dotnet ef` executes that path. The factory must force `UseSqlServer` **and** the same `MigrationsAssembly`; otherwise DEBUG's `UseInMemoryDatabase` makes the tool emit an empty model.
- **Workspace** (`src/AzureDesignStudio.Workspace`): add `IDesignTimeDbContextFactory<WorkspaceDbContext>` returning a throwaway file path under `Path.GetTempPath()` (avoid `:memory:` — it lifetime-binds to the open connection and confuses the tool's `--no-build` flows). Migrations live at `src/AzureDesignStudio.Workspace/Migrations/`.
- **Delete the bootstrap**: `WorkspaceDbContext` ctor (`EnsureCreated`, `EnsureAddedColumns`, `EnsureAdditiveTables`) and the `ExecuteSqlRaw` DDL block at `Program.cs:73-157`. `EnsureCreated()` and migrations are mutually exclusive — `EnsureCreated` never writes `__EFMigrationsHistory`, so a database created that way can never be migrated afterwards. Keeping both is the single most likely way to brick an existing `workspace.db`.
- **Baseline an existing database** with the standard adoption path (EF has no "baseline" verb): generate `InitialCreate`, verify the generated schema equals the live schema, then for each existing database create the provider's history table and insert the baseline `MigrationId` **in the same transaction** as any manual reconciliation — SQLite: `CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL PRIMARY KEY, "ProductVersion" TEXT NOT NULL);`; SQL Server: `CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL PRIMARY KEY, [ProductVersion] nvarchar(32) NOT NULL);`. Only fresh databases run `Up()`. Do this for the SQLite workspace and for the SQL Server database before `Migrate()` is enabled anywhere.
- **SQL Server in production** runs with 2 replicas (`src/Configurations/main/deployment.yaml`). Do not call `Migrate()` from `Program.cs` there — two replicas race. Generate a migration bundle (`dotnet ef migrations bundle --target-runtime linux-x64`) and run it as an init container / release step. Keep an in-process `Migrate()` only for the single-process desktop workspace.
- **Workspace (single user, desktop)** calls `Migrate()` at startup in `WorkspaceDb`. It takes a write lock; with WAL + `busy_timeout` a concurrent MCP reader is fine, but the desktop must not migrate while the MCP server holds a long read transaction (see `PRAGMA wal_checkpoint` behaviour).

**Repo wiring — SQLite durability**

- Connection string (`WorkspaceDbContext.ConnectionString`, `AdsWorkspace._connectionString`) becomes:
  `new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 30, Pooling = true }`.
  **Remove `Cache = SqliteCacheMode.Shared` in both places** — Microsoft's connection-string docs explicitly caution that "mixing shared-cache mode and write-ahead logging is discouraged… remove `Cache=Shared` when the database is configured to use write-ahead logging."
- Apply PRAGMAs on every opened connection through a `DbConnectionInterceptor` registered in `UseSqlite(o => o.AddInterceptors(...))`, not once at startup:
  - `journal_mode=WAL` (persistent in the file; setting it once is enough, but re-asserting is harmless and returns `wal`)
  - `synchronous=NORMAL` (per connection)
  - `busy_timeout=5000` (per connection; complements `DefaultTimeout`)
  - `foreign_keys=ON` (also covered by the connection-string keyword)
  - `auto_vacuum=INCREMENTAL` requires a one-time `VACUUM` on an existing database — schedule it in the migration step, not at startup.
- **Backup** uses `SqliteConnection.BackupDatabase(destination)` (the online backup API, WAL-safe) or `VACUUM INTO '<path>'`. A plain file copy of `workspace.db` is **not** a valid backup in WAL mode — committed pages may still live in `-wal`. Restore = close all connections, replace the file, delete stale `-wal`/`-shm`.
- **Startup integrity**: `PRAGMA quick_check` (not `integrity_check` — orders of magnitude cheaper on a large workspace) and surface a failure in the diagnostics surface rather than swallowing it.
- **MCP read-only + WAL**: `AdsWorkspace` opens `Mode=ReadOnly`. SQLite ≥3.22 permits read-only access to a WAL database if `-shm`/`-wal` exist or can be created, or if the database is opened `immutable`. The desktop app normally creates them; for cold/archived reads document the `immutable=1` URI option. Do not assume `File.Copy`-style snapshot semantics.
- **EF Core 10 breaking change to respect during the baseline**: `Microsoft.Data.Sqlite` 10 now treats textual timestamps **without** an offset as UTC, and converts offset-bearing values to UTC on read. `src/AzureDesignStudio.Workspace/WorkspaceEntities.cs` maps 14 `DateTimeOffset` properties. Rows written by EF carry offsets, so the risk is confined to legacy/hand-written values — but the migration validation must read back a `Scans.StartedAt`, `ChangeFeed.ChangeTime` and `DecomWindows.UpdatedAtUtc` row and assert the instant is unchanged.

### 1.3 gRPC / HTTP hardening, health, CSP

All of the following are **in-framework** in .NET 10 — no new packages except the gRPC health-check package.

| Technology | Version | Purpose | Why Recommended | Conf. |
|---|---|---|---|---|
| `Microsoft.AspNetCore.RateLimiting` | in-framework | Per-user/global request limits | `AddRateLimiter` + `UseRateLimiter`; `PartitionedRateLimiter` keyed on the normalized `oid` claim with an IP fallback | H |
| `Microsoft.AspNetCore.Http.Timeouts` | in-framework | Server-side deadlines | `AddRequestTimeouts` + `UseRequestTimeouts`; `.WithRequestTimeout("grpc")` is available on `GrpcServiceEndpointConventionBuilder` | H |
| `Grpc.AspNetCore` / `Grpc.AspNetCore.Web` / `Grpc.AspNetCore.Server.Reflection` | **2.84.0** | Server, gRPC-Web, reflection | Move 2.66.0 → 2.84.0 as one aligned set; `MaxReceiveMessageSize`/`MaxSendMessageSize` live here | H |
| `Grpc.AspNetCore.HealthChecks` | 2.84.0 | Standard `grpc.health.v1` service | Desktop talks native HTTP/2 on `localhost:5204` and cannot use an HTTP health endpoint cheaply; this exposes the same `IHealthCheck` results over gRPC | H |
| `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` | 10.0.13 | `AddDbContextCheck<DesignDbContext>` | Microsoft-maintained; verifies the SQL Server connection per `/health/ready` probe | H |
| `Grpc.Net.Client` / `.Web` / `.ClientFactory` / `Grpc.Tools` | 2.84.0 | Client + codegen | Must move with the server packages; `Grpc.Tools` regenerates the same contracts in `src/AzureDesignStudio.SharedModels/*.proto` | H |

**Repo wiring — limits and deadlines**

- `AddGrpc(o => { o.MaxReceiveMessageSize = 16 * 1024 * 1024; o.MaxSendMessageSize = 16 * 1024 * 1024; o.EnableDetailedErrors = false; })`, then `AddServiceOptions<DesignService>(o => o.MaxReceiveMessageSize = 4 * 1024 * 1024)`. The design payload cap (N26) must be **lower** than the transport cap so the contract validator produces the error, not the transport.
- Kestrel: set `Limits.MaxRequestBodySize = 20 MB`, `MaxRequestHeadersTotalSize`, `RequestHeadersTimeout`, `KeepAliveTimeout` once on `KestrelServerOptions.Limits` inside the existing `ConfigureKestrel` block at `Program.cs:15-17`. Server-wide limits apply to both listeners, including the h2c `ListenLocalhost(5204)` one — the ordering that matters is that Kestrel's cap (20 MB) must sit **above** the gRPC cap (16 MB) so an oversized design message is rejected as `ResourceExhausted` by the gRPC layer rather than as a bare HTTP 413.
- Rate limiter: global `PartitionedRateLimiter.Create<HttpContext,string>` keyed on `oid` (falling back to remote IP) plus a `ConcurrencyLimiter` per partition for the expensive `Deploy` RPCs. `UseRateLimiter()` must sit **after** `UseRouting()` (required for endpoint-attached policies) **and after** `UseAuthentication()` — `HttpContext.User` is anonymous before authentication runs, so a partition keyed on `oid` silently collapses into a single shared partition if the limiter is placed earlier. The cost is that unauthenticated floods do the JWT handshake first; offset that with the Kestrel caps above, not by moving the limiter.
- **Rejection contract (M — needs an integration test):** with the default `OnRejected`, a gRPC call surfaces as `RpcException(StatusCode = Unavailable, "Bad gRPC response. HTTP status code: 503")`, which is a lie about the condition. Write a trailers-only gRPC response in `OnRejected` when `Request.ContentType` starts with `application/grpc`: `StatusCode = 200`, `Content-Type: application/grpc`, `grpc-status: 8` (RESOURCE_EXHAUSTED), `grpc-message: rate_limited`, plus `Retry-After`. Non-gRPC requests keep the documented 429 + `Retry-After`. This is spec-consistent, not officially documented as a recipe — assert it in `Server.Tests`.
- Same pattern for timeouts: `RequestTimeoutPolicy.WriteTimeoutResponse` should emit `grpc-status: 4` (DEADLINE_EXCEEDED) for gRPC content types instead of the default 504.

**Repo wiring — health**

- `builder.Services.AddHealthChecks().AddDbContextCheck<DesignDbContext>("server-db")` and a custom `workspace`-only check is unnecessary on the server (no workspace reference by design — do not add one for health).
- `app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();` and `/health/ready` with the DbContext check. `live` must not touch the database or a rollout stalls during a DB restart.
- `AddGrpcHealthChecks()` / `MapGrpcHealthChecksService().AllowAnonymous()` from `Grpc.AspNetCore.HealthChecks`, configured with `o.Services.MapService("", r => r.Tags.Contains("public"))` so the unauthenticated gRPC probe does not reveal dependency details.
- Health checks must stay reachable when `UseHttpsRedirection()` is active behind ingress: the existing `UseForwardedHeaders()` config already clears `KnownProxies`, which is what makes the scheme correct — keep it ahead of the health mapping.

**Repo wiring — CSP (M, this is the sharpest edge in the whole milestone)**

- The `.NET 10` Blazor WASM host page `src/AzureDesignStudio/wwwroot/index.html` contains `<script type="importmap"></script>` (line 42) plus an inline Application Insights snippet (lines 28-40), and `src/AzureDesignStudio.Desktop/wwwroot/app.html` contains an inline theme bootstrap script (lines 9-17). Under a strict `script-src 'self'`, **all three are blocked**, and the import map is not optional: with the map blocked the fingerprinted `dotnet.js` URL is never rewritten and the app fails to boot — this is an open runtime issue (dotnet/runtime#121885), not a warning.
- Import maps **cannot** be external files (HTML spec), so the only options are a hash or a nonce. The host page is static HTML, not Razor — there is no nonce to template. Therefore: **hash**.
- Recommended: `Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension` **10.0.13** (MPL-2.0, version-aligned with the 10.0.13 framework packages), which computes the import-map SHA-256 at build/publish and substitutes it into a placeholder in the CSP. Without it, a build step must hash the rendered import map and the inline snippets and emit both the meta tag and the header — more moving parts, same result.
- Policy shape for the WASM host: `default-src 'self'`; `script-src 'self' 'wasm-unsafe-eval' 'sha256-…'` (`wasm-unsafe-eval` is required by the Mono runtime and by AOT — Microsoft's Blazor CSP doc states this explicitly); `style-src 'self' 'unsafe-inline'` is **required here** because `Z.Blazor.Diagrams` writes inline `style` attributes on nodes/links and Ant Design Blazor toggles inline styles; `object-src 'none'`; `base-uri 'self'`; `frame-ancestors 'none'` (header only — not valid in `meta`); `connect-src 'self' https://login.microsoftonline.com https://*.applicationinsights.azure.com https://js.monitor.azure.com` plus `ws:`/`wss:` in Development.
- Serve it as a **response header** from `src/AzureDesignStudio.Server/Program.cs` (middleware before `UseRouting`), and as a **`<meta http-equiv>` tag** in `src/AzureDesignStudio.Desktop/wwwroot/app.html` — WebView2 serves the host page from the app package and never sees the server's headers, so the desktop needs its own copy. Generate both from one source (a checked-in `csp.json` or an MSBuild item) or they will drift.
- `E2E.Tests` must fail on any `securitypolicyviolation` event — that is the only durable guard, and it is the acceptance criterion from N13.

### 1.4 Test stack (three new projects, one existing)

| Technology | Version | Purpose | Why Recommended | Conf. |
|---|---|---|---|---|
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.13 | `WebApplicationFactory<Program>` | Only supported way to exercise the real `Program.cs` pipeline (auth, rate limiter, gRPC) in-process | H |
| `Microsoft.AspNetCore.Authentication` test handler | hand-written in `Server.Tests` | Fake JWT | Register a test `AuthenticationHandler<AuthenticationSchemeOptions>` in `ConfigureTestServices`; issue `oid`, `tid`, `scp` claims so `ServiceTools.GetUserId` and `RequiredScope` are exercised, not bypassed | H |
| `Grpc.Net.Client` + `Grpc.Net.Client.Web` | 2.84.0 | gRPC calls against TestServer | Wire `ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler())` — `this.Server.CreateHandler()` inside the factory double-boots the host | H |
| `bunit` | 2.12.5 | Razor component tests | Requires `Microsoft.NET.Sdk.Razor` and Components ≥ 10.0.13 (its own dependency floor) — this forces the 10.0.13 bump | H |
| `Microsoft.Playwright` | 1.63.0 | Browser E2E (WASM) | Same runner for WASM and desktop; CDP over WebView2 means one harness for both hosts | H |
| `Microsoft.Playwright.Xunit` | 1.63.0 | xUnit 2 integration | Matches `Core.Tests`' xUnit 2.9.x; a `.Xunit.v3` package exists if you migrate later | H |
| `Testcontainers.MsSql` | 4.16.0 | Disposable SQL Server for migration tests | Needed because the server model has a SQL Server–only check constraint (`ISJSON`) that SQLite cannot create — see §4 | M |
| xUnit | 2.9.3 (bump from 2.9.2) | Runner | Keep **one** test stack. xunit.v3 4.0.2 exists and is where the ecosystem is going, but it changes the executable/MTP model and coverage tooling; migrating 755 cases mid-trust-gate is unnecessary risk | M |
| `coverlet.collector` | 6.0.4 | Coverage collection | Bump from 6.0.2; keep `--collect:"XPlat Code Coverage"` | H |
| `dotnet-reportgenerator-globaltool` | 5.5.11 | Merge Cobertura + diff | Produces the per-PR delta N5 requires; no licence constraints | H |

**Repo wiring — tests**

- `src/AzureDesignStudio.Server/Program.cs` uses top-level statements, so the generated `Program` class is `internal`. Add `public partial class Program { }` at the end of the file or `InternalsVisibleTo` — `WebApplicationFactory<Program>` will not compile otherwise.
- `Server.Tests` referencing `Server` transitively references `src/AzureDesignStudio` (WASM). That is correct for E2E realism but makes the Debug build heavier; keep `-c Debug` for this project in the fast job and accept ~1-2 min.
- Keep `Microsoft.EntityFrameworkCore.InMemory` for behavior tests, but **do not** use it for migration tests: it ignores check constraints and has no `ALTER TABLE`. Migration tests run against a `Testcontainers.MsSql` instance (`mcr.microsoft.com/mssql/server:2022-latest`) or a GitHub Actions `services:` container, applying `Migrate()` then comparing `__EFMigrationsHistory` and row counts.
- `Components.Tests` must reference `src/AzureDesignStudio.Core` (Razor SDK class library) and set up `JSInterop.Mode = JSRuntimeMode.Loose`; every component here touches Ant Design or Z.Blazor.Diagrams JS interop.
- `E2E.Tests` needs an auth story before it needs a browser: add an `E2E` environment that registers a test auth scheme and a seeded `appsettings.E2E.json`, gated on `env.IsEnvironment("E2E")` and rejected in `Production`. Do not add a general "auth disabled" switch.
- Desktop E2E: run the **unpackaged** desktop executable (`WindowsPackageType=None`) with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<10000+worker>` and `WEBVIEW2_USER_DATA_FOLDER=<per-worker temp>`, then `Playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}")`. This is Playwright's documented WebView2 recipe. Windows-only; nightly rather than per-PR.
- Playwright browser install: `pwsh bin/Debug/net10.0/playwright.ps1 install chromium` (Windows) / `install --with-deps chromium` (Linux); cache `%USERPROFILE%\AppData\Local\ms-playwright` keyed on the `Microsoft.Playwright` version.

### 1.5 AutoMapper exit

**Decision: remove AutoMapper in this milestone. Emit mappings from the in-repo Roslyn generator. Conf: H on removing AutoMapper; M on generator-vs-Mapperly.**

Why removal is forced, not merely nice:

1. `AutoMapper` 12.0.1 is in the affected range of **CVE-2026-32933 / GHSA-rvv3-g6hj-g44x** (high, CVSS 7.5) — uncontrolled recursion → `StackOverflowException` → process termination. Patched versions are **15.1.1** and **16.1.1**.
2. Those patched versions are dual-licensed **RPL-1.5 + Lucky Penny Software commercial** (the change landed at 15.0.0, July 2025); ≤ 14.0.0 stays MIT but is vulnerable. There is **no** "free and patched" version. The repo's `NoWarn=NU1903` suppression in two csproj files is therefore permanent debt, and N13's `dotnet list package --vulnerable` gate cannot pass while it exists.
3. `src/AzureDesignStudio/AzureDesignStudio.csproj` publishes with `PublishTrimmed=true` and `RunAOTCompilation=true` in Release. Reflection-driven mapping plus trimming is a standing source of "it works in Debug" failures.

**Recommended shape (uses what already exists):**

- `src/AzureDesignStudio.SourceGeneration/AzureNodeDtoGenerator.cs` already renders `AzureNodeProfile.g.cs` and `DataModelFactory.g.cs` from `Templates/AzureNodeProfile.scriban` / `Templates/DataModelFactory.scriban`, driven by `[MapToDto]` on 49+ models. Replace:
  - `Templates/AzureNodeProfile.scriban` → per-type `static AzureNodeDto ToDto(<T> source)` / `static <T> FromDto(AzureNodeDto dto)` bodies.
  - `Templates/DataModelFactory.scriban` → `switch (dto.TypeKey)` dispatch calling those methods directly instead of `mapper.Map<…>(dto)`.
- Introduce `IAzureNodeMapper` in `src/AzureDesignStudio.Core/DTO/`, and change the public seam `GetNodeDto(IMapper mapper)` → `GetNodeDto(IAzureNodeMapper mapper)` across the model overrides plus `DataModelFactory.GetNodeModelFromDto`. This is the migration cost: ~49 override signatures, mechanical, compiler-verified.
- Keep the genuinely hand-written mappings explicit: `LinkModel → LinkModelDto` (anchor parent id/alignment), `FirewallPolicyModel ↔ FirewallPolicyModelDto`, `GroupId`, `Size ?? new Size(81,81)`, and `TypeKey` assignment. These are exactly the five behaviours the AutoMapper profile encodes today (`AzureNodeProfile.cs:12-30`).
- Delete `AutoMapper` and `AutoMapper.Extensions.Microsoft.DependencyInjection` references, the `AddAutoMapper(typeof(AzureNodeProfile))` call at `src/AzureDesignStudio/Program.cs:78`, and both `NoWarn=NU1903` blocks.

**Alternative accepted if hand-authored maps are preferred:** Mapperly **4.3.1** (`Riok.Mapperly` + `Riok.Mapperly.Abstractions`, Apache-2.0, no runtime reflection, trimming-safe). It handles `[MapProperty]`, `[MapperIgnoreTarget]`, `[ObjectFactory]` and `[AfterMap]` cleanly. It is *not* recommended as the primary path only because it duplicates the generator the repo already owns and would still require changing the same `IMapper` seam.

### 1.6 Build inputs (supporting, but load-bearing)

| Tool | Version | Purpose | Notes | Conf. |
|---|---|---|---|---|
| `Directory.Packages.props` (CPM) | n/a | One version per package | 11 projects repeat `10.0.12`/`2.66.0`; a single bump to 10.0.13 becomes a one-line change instead of a 40-line edit | H |
| `RestorePackagesWithLockFile` | n/a | `packages.lock.json` | Prerequisite for `setup-dotnet` `cache: true`, for `dotnet restore --locked-mode` in CI, and for a reproducible SBOM | H |
| `Directory.Build.props` | n/a | `EnableNETAnalyzers`, `AnalysisLevel=latest-recommended`, `Nullable`, `TreatWarningsAsErrors` | The repo has no shared props; `Nullable` is currently per-project (and disabled in `AzureResources`) | H |
| `Microsoft.Sbom.DotnetTool` | 4.1.13 | SPDX SBOM (`dotnet sbom`) | Attach to the release next to the `.msix`; satisfies the N13 supply-chain item | M |
| `CycloneDX` | 6.3.1 | CycloneDX SBOM alternative | Use if consumers want CycloneDX rather than SPDX; do not run both | M |
| `actions/dependency-review-action@v4` | v4 | PR gate on new vulnerable deps | Complements `dotnet list package --vulnerable --include-transitive` | H |

---

## 2. Installation

```bash
# --- Repo-wide build inputs ---
# global.json: { "sdk": { "version": "10.0.400", "rollForward": "latestFeature" } }
# Directory.Packages.props + RestorePackagesWithLockFile=true in Directory.Build.props

# --- EF Core design-time + tool ---
dotnet add src/AzureDesignStudio.Workspace package Microsoft.EntityFrameworkCore.Design --version 10.0.13
dotnet add src/AzureDesignStudio.Server    package Microsoft.EntityFrameworkCore.Design --version 10.0.13
# src/.config/dotnet-tools.json  ->  dotnet-ef 10.0.13
dotnet tool restore
dotnet ef migrations add InitialCreate --project src/AzureDesignStudio.Workspace --context WorkspaceDbContext
dotnet ef migrations add InitialCreate --project src/AzureDesignStudio.Server    --context DesignDbContext

# --- Server hardening ---
dotnet add src/AzureDesignStudio.Server package Grpc.AspNetCore.HealthChecks --version 2.84.0
dotnet add src/AzureDesignStudio.Server package Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore --version 10.0.13
dotnet add src/AzureDesignStudio.Server package Microsoft.Data.SqlClient --version 7.1.1
# gRPC set bump (Server, SharedModels, WASM):
#   Grpc.AspNetCore / .Web / .Server.Reflection / Grpc.Net.Client / .Web / .ClientFactory / Grpc.Tools -> 2.84.0

# --- Test projects ---
dotnet new xunit -o src/AzureDesignStudio.Server.Tests
dotnet add src/AzureDesignStudio.Server.Tests package Microsoft.AspNetCore.Mvc.Testing --version 10.0.13
dotnet add src/AzureDesignStudio.Server.Tests package Testcontainers.MsSql --version 4.16.0

dotnet new bunit --framework xunit -o src/AzureDesignStudio.Components.Tests
dotnet add src/AzureDesignStudio.Components.Tests package bunit --version 2.12.5

dotnet new xunit -o src/AzureDesignStudio.E2E.Tests
dotnet add src/AzureDesignStudio.E2E.Tests package Microsoft.Playwright --version 1.63.0
dotnet add src/AzureDesignStudio.E2E.Tests package Microsoft.Playwright.Xunit --version 1.63.0
pwsh src/AzureDesignStudio.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium

# --- Coverage ---
dotnet tool install -g dotnet-reportgenerator-globaltool --version 5.5.11

# --- MSIX (CI) ---
dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj
dotnet publish src/AzureDesignStudio.Desktop -c Release -f net10.0-windows10.0.19041.0 \
  -p:WindowsPackageType=Package -p:RuntimeIdentifierOverride=win-x64 \
  -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:AppxPackageSigningEnabled=false
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f <cert.pfx> /p <password> <pkg.msix>
signtool verify /pa /v <pkg.msix>
```

Note: run the `dotnet add` commands against the project that owns the reference. Once `Directory.Packages.props` lands, versions move there and `dotnet add package` should be invoked without `--version`. Check what `dotnet new xunit` emits before committing — newer SDK templates can scaffold `xunit.v3`; pin `xunit` 2.9.3 + `xunit.runner.visualstudio` 2.8.2 explicitly so all four test projects stay on one stack.

---

## 3. Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|---|---|---|
| In-repo generator emitting explicit maps | **Mapperly 4.3.1** | If the team wants mapping code authored per-type in C# rather than templated; Mapperly's diagnostics are better than Scriban's, and it needs no seam change beyond `IAzureNodeMapper` |
| In-repo generator emitting explicit maps | **Mapster 7.x** | Only if runtime-configurable mappings are genuinely needed (e.g. user-defined DTO shaping). MIT, but runtime reflection — wrong for trimming/AOT |
| Microsoft `AddDbContextCheck` + `MapHealthChecks` | **Xabaril `AspNetCore.HealthChecks.Sqlite` 9.0.0** | Not recommended: 9.0.0 is the latest published and predates .NET 10. A 20-line `IHealthCheck` running `PRAGMA quick_check` is smaller than the dependency and avoids a net9-targeted package in a net10 build |
| `Testcontainers.MsSql` for migration tests | GitHub Actions `services: mssql` | If image-pull time dominates: a service container starts once per job, Testcontainers starts per collection. Either way the provider is SQL Server, not InMemory |
| Playwright `ConnectOverCDPAsync` for WebView2 | WinAppDriver 1.2.2009 (preinstalled on runner) | If CDP is blocked by a WebView2 policy. WinAppDriver drives native chrome, not DOM, so it cannot assert canvas/Blazor state — strictly worse here |
| Azure Artifact Signing | Self-signed PFX + `TrustedPeople` | Pilot/internal distribution where Artifact Signing eligibility (USA/Canada/EU/UK organizations; individual developers limited to US/Canada) is unmet |
| `VACUUM INTO` / `BackupDatabase` | `sqlite3 .backup` CLI | If a maintenance tool already shells out to `sqlite3`; equivalent guarantees, extra process dependency |
| xunit v2 for the new projects | **xunit.v3 4.0.2** | When the whole solution migrates (`Core.Tests`' 755 cases included) and coverage moves to `Microsoft.Testing.Extensions.CodeCoverage` |
| `migrations bundle` for SQL Server | `Migrate()` at startup | Only for single-instance deployments; the Kubernetes manifest runs 2 replicas, so startup migration is a race |

---

## 4. What NOT to Use

| Avoid | Why | Use Instead | Conf. |
|---|---|---|---|
| **AutoMapper 12.x–14.x** | Affected by CVE-2026-32933 (uncontrolled recursion → process kill); the repo suppresses `NU1903` to live with it | Remove; generator-emitted maps | H |
| **AutoMapper 15.x / 16.x** | Patched, but RPL-1.5 + paid commercial licence — a distribution/build-licensing decision for a shipping desktop app | Generator-emitted maps or Mapperly 4.3.1 | H |
| `AutoMapper.Extensions.Microsoft.DependencyInjection` 12.0.0 | Deprecated; its `AddAutoMapper` is replaced by `services.AddAutoMapper(cfg => …, assemblies)` in v13+ | Removed entirely with AutoMapper | H |
| `EnsureCreated()` alongside migrations | Never writes `__EFMigrationsHistory`; a DB created this way can never be migrated and a migration'd DB makes `EnsureCreated` a no-op that hides drift | `Migrate()` + baseline adoption | H |
| Raw `ALTER TABLE … ADD COLUMN` guards (`WorkspaceDbContext.EnsureAddedColumns`) | Not transactional across statements, no rollback, no record of what ran | A real migration per change | R/H |
| `EF Core InMemory` for migration/schema tests | No relational behaviour, no constraints, no `ALTER`; `DesignDbContext` has `HasCheckConstraint("ADS_CHECK_JSON", "ISJSON(DesignData)=1")`, a SQL Server–only function | `Testcontainers.MsSql` (or a CI service container) | H |
| `Cache=Shared` with WAL | Microsoft's own connection-string docs warn against the combination; shared cache changes locking behaviour and defeats WAL's reader/writer concurrency | Plain connection string + `Pooling=true` | H |
| Copying `workspace.db` as a backup | In WAL mode, committed data can still be in `-wal`; the copy is torn | `SqliteConnection.BackupDatabase` or `VACUUM INTO` | H |
| `dotnet-ef` 6.0.4 (current pin) | Cannot load an EF 10 model or the 10.x design assembly | `dotnet-ef` 10.0.13 | H |
| Failing the health check on `/health/live` with a DB probe | A DB restart during rollout would mark every pod unready and stall the deployment | `Predicate = _ => false` for live; DB check only on ready | H |
| `unsafe-eval` in CSP for Blazor | Over-broad; the runtime needs `wasm-unsafe-eval` specifically | `'wasm-unsafe-eval'` (plus hashes for inline scripts) | H |
| `script-src 'self'` alone on the .NET 10 WASM host page | The inline import map is blocked, fingerprints never resolve, `dotnet.js` 404s — app will not start | Hash the import map (`Toolbelt…ImportMapExtension` 10.0.13) | H |
| FluentAssertions 8+ | Free for non-commercial use only — paid Xceed licence required for commercial use (v7 stays Apache-2.0) | xUnit built-in asserts, `Shouldly` 4.3.0 or `AwesomeAssertions` 9.6.0 | H |
| `Microsoft.AspNetCore.TestHost` alone for E2E | Cannot be reached from a separate process — Playwright/WebView2 need a real socket | `WebApplicationFactory` for in-process tests; Kestrel (`dotnet run`) for E2E | H |
| `this.Server.CreateHandler()` inside `ConfigurePrimaryHttpMessageHandler` | Re-enters factory creation and can boot the host twice (documented failure mode with `WithWebHostBuilder`) | `services => ((TestServer)services.GetRequiredService<IServer>()).CreateHandler()` | H |
| Package-version spoofing via `NoWarn` | `NoWarn=NU1903` hides *all* high-severity advisories in that project, not one | `dotnet list package --vulnerable --include-transitive` gate in CI | H |

---

## 5. Stack Patterns by Variant

**If the pilot needs installability today and Artifact Signing eligibility is unresolved:**
- Self-signed PFX, `signtool sign /fd SHA256 /f … /p … /tr http://timestamp.digicert.com /td SHA256`, ship the `.cer` alongside the `.msix`, document `Import-Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople`.
- Because it is self-signed, SmartScreen shows "unknown publisher" — that is a pilot-only outcome, not a defect.

**If Artifact Signing is available:**
- `azure/login@v2` with `id-token: write` (no client secret), then `azure/trusted-signing-action@v0` with `files-folder` + `files-folder-filter: msix`.
- Update `Package.appxmanifest` `Publisher` to the verified identity **before** the first signed release; changing it later breaks MSIX upgrades.

**If the desktop host needs to run E2E on PRs (not just nightly):**
- Build unpackaged (`WindowsPackageType=None`), start the exe with the CDP env vars, connect over CDP. Do not try to install the MSIX on the runner — `Add-AppxPackage` on a hosted runner is slow and stateful.

**If SQL Server migration tests must run on the Ubuntu job:**
- `services: mssql: image: mcr.microsoft.com/mssql/server:2022-latest, env: ACCEPT_EULA=Y, MSSQL_SA_PASSWORD=…`, and read the password from a secret even in tests — SQL Server refuses to start with a weak one, which reads as a flaky test.

**If `PublishTrimmed` + AOT start failing after the mapping change:**
- The failure will be `JsonSerializer` reflection on DTOs, not the mappings. Add a `JsonSerializerContext` for `AzureNodeDto`/`LinkModelDto` rather than loosening trimming.

---

## 6. Version Compatibility

| Package A | Compatible With | Notes |
|---|---|---|
| `bunit` 2.12.5 | `Microsoft.AspNetCore.Components*` **≥ 10.0.13** | bUnit's floor forces the 10.0.12 → 10.0.13 bump across WASM, Core, Server, tests |
| `Microsoft.EntityFrameworkCore.*` 10.0.13 | `Microsoft.Data.SqlClient` 6.1.7+ / pinned 7.1.1 | 7.1.1 removes the Entra/broker dependency from the server container |
| `Grpc.AspNetCore*` 2.84.0 | `Grpc.Tools` 2.84.0, `Grpc.Net.Client*` 2.84.0 | Treat the gRPC packages as one versioned set — server, client and codegen ship together and the wire contract is generated from the same release |
| `Grpc.AspNetCore.HealthChecks` 2.84.0 | `Grpc.AspNetCore` 2.84.0 | Same release train |
| `Microsoft.Playwright` 1.63.0 | Edge WebView2 Evergreen runtime | CDP is stable across versions; pin the package, not the browser |
| `Riok.Mapperly` 4.3.1 | net10.0, trimming/AOT | 5.0.0 is `-next` only — do not take a prerelease for a trust gate |
| MAUI workload (net10) | .NET SDK 10.0.4xx | The runner's preinstalled MAUI packs are net9 only |
| `xunit` 2.9.3 | `xunit.runner.visualstudio` 2.8.2, `coverlet.collector` 6.0.4 | Do not mix xunit v2 and v3 across projects in this milestone |

---

## 7. Confidence Assessment

| Area | Confidence | Reason |
|---|---|---|
| AutoMapper removal | **HIGH** | CVE + licence facts verified via GitHub Advisory and multiple independent sources; versions confirmed on NuGet |
| EF Core migrations tooling | **HIGH** | Microsoft docs for design-time factories, `dotnet ef` 10 behavior, and package floors verified |
| SQLite WAL/busy-timeout/backup | **HIGH** | Microsoft.Data.Sqlite docs and sqlite.org WAL doc; the `Cache=Shared` conflict is stated by Microsoft |
| gRPC limits/deadlines/health | **HIGH** | Microsoft docs for `AddGrpc` options, request timeouts, `Grpc.AspNetCore.HealthChecks` |
| gRPC rejection contract (`grpc-status: 8`) | **MEDIUM** | Spec-consistent and derived from the documented default (`503 → Unavailable`), but not an official recipe — must be pinned by an integration test |
| CSP hash pipeline | **MEDIUM** | The blocking behavior is confirmed (docs + dotnet/runtime#121885); the chosen workaround package exists at 10.0.13 but is third-party |
| MSIX signing/CI | **HIGH** | Microsoft MSIX signing guide (2026-04) and the runner image inventory |
| Test stack versions | **HIGH** for versions; **MEDIUM** for the xunit-v2 choice | Package metadata verified; staying on v2 is a risk-management call |

---

## 8. Sources

- GitHub Advisory **GHSA-rvv3-g6hj-g44x / CVE-2026-32933** (AutoMapper DoS; patched 15.1.1, 16.1.1) — https://github.com/advisories/GHSA-rvv3-g6hj-g44x — **HIGH**
- NuGet package metadata (`api.nuget.org/v3-flatcontainer`, `nuget.org/packages/…`) for every pinned version above, incl. `bunit` 2.12.5 dependencies — **HIGH**
- EF Core 10 breaking changes, incl. `Microsoft.Data.Sqlite` timestamp handling and the SqlClient broker dependency — https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes — **HIGH**
- EF Core "Using a Separate Migrations Project" (design-time factory, platform-specific startup projects) — https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects — **HIGH**
- Microsoft.Data.Sqlite connection strings (`Cache`, `Foreign Keys`, `Default Timeout`, `Pooling`; explicit shared-cache/WAL caution) — https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings — **HIGH**
- SQLite WAL (read-only databases, checkpointing, persistence of `journal_mode`) — https://sqlite.org/wal.html — **HIGH**
- `SqliteConnection.BackupDatabase` — https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.backupdatabase — **HIGH**
- gRPC for .NET configuration (`MaxReceiveMessageSize` default 4 MB, `AddServiceOptions`) — https://learn.microsoft.com/en-us/aspnet/core/grpc/configuration?view=aspnetcore-10.0 — **HIGH**
- gRPC health checks (`Grpc.AspNetCore.HealthChecks`, `MapGrpcHealthChecksService`) — https://learn.microsoft.com/en-us/aspnet/core/grpc/health-checks?view=aspnetcore-10.0 — **HIGH**
- Rate limiting middleware (`OnRejected`, `Retry-After`, ordering after `UseRouting`) — https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0 — **HIGH**
- Request timeouts middleware (`WithRequestTimeout`, `WriteTimeoutResponse`) — https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0 — **HIGH**
- gRPC rate limiting behavior on .NET clients (`503` → `Unavailable`) — https://blog.ladeak.net/posts/aspnetcore-grpc-ratelimiting — **MEDIUM** (third-party, but consistent with the framework docs)
- Blazor CSP guidance (`script-src 'self' 'wasm-unsafe-eval'`, hashes, `frame-ancestors` header-only) — https://learn.microsoft.com/en-us/aspnet/core/blazor/security/content-security-policy?view=aspnetcore-10.0 — **HIGH**
- .NET 10 import map vs strict CSP (open issue, inline import map is unavoidable) — https://github.com/dotnet/runtime/issues/121885 — **HIGH**
- `Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension` 10.0.13 — https://www.nuget.org/packages/Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension — **MEDIUM** (solution choice)
- MSIX signing end-to-end guide (Artifact Signing, self-signed for pilot, OV fallback, `azure/trusted-signing-action`) — https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide — **HIGH**
- Publish a packaged MAUI Windows app (`WindowsPackageType=Package`, `RuntimeIdentifierOverride=win-x64`, WindowsAppSDK #3337, Trusted People install) — https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli?view=net-maui-10.0 — **HIGH**
- GitHub Actions `windows-2025` runner image inventory (SDK 10.0.401, Windows SDK 10.0.26100.0, VS2022 MAUI 9.0.111) — https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md — **HIGH**
- Playwright WebView2 (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, `ConnectOverCDPAsync`, `WEBVIEW2_USER_DATA_FOLDER`) — https://playwright.dev/dotnet/docs/webview2 — **HIGH**
- bUnit project setup (`Microsoft.NET.Sdk.Razor`, net8+) — https://bunit.dev/docs/getting-started/create-test-project.html — **HIGH**
- gRPC + `WebApplicationFactory` handler wiring and the double-boot caveat — https://github.com/grpc/grpc-dotnet/issues/1308 — **HIGH**
- FluentAssertions 8 licensing (commercial use requires Xceed licence; v7 remains Apache-2.0) — https://xceed.com/fluent-assertions-faq/ — **HIGH**
- Repo evidence: `.planning/codebase/STACK.md`, `.planning/codebase/CONCERNS.md`, `.planning/codebase/TESTING.md`, `docs/v3-plan.md` (N1–N3, N5, N13–N14, N17–N18, N26, N46), `.github/workflows/{build,publish}.yml`, `src/AzureDesignStudio.Server/Program.cs`, `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs`, `src/AzureDesignStudio.Mcp/AdsWorkspace.cs`, `src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs`, `src/AzureDesignStudio.SourceGeneration/Templates/*.scriban`, `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest`, `src/AzureDesignStudio/wwwroot/index.html`, `src/AzureDesignStudio.Desktop/wwwroot/app.html` — **HIGH (repo-derived)**

---
*Stack research for: Azure Design Studio v3 Trust Gate + signed MSIX capability*
*Researched: 2026-10-21*
