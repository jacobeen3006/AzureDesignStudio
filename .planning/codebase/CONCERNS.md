---
last_mapped_commit: a05ef8c00d2ff2dc2f0d9bea0a220051852405a8
last_mapped_at: 2026-10-21
---
# Codebase Concerns

**Analysis Date:** 2026-10-21

Scope: full repository (`src/` — 11 projects, ~64k lines of C#/Razor across 760 source files). Severity is rated **High** (will cause data loss / security exposure / broken production behavior), **Medium** (degrades reliability or developer velocity; will bite during the next feature), **Low** (hygiene).

---

## Tech Debt

### Monolithic gRPC service files

- **Issue:** `DeployService` is 2,210 lines with 44 `catch` blocks and 13 RPC entry points; `DesignService` and `RetailPriceService` are small by contrast. Client-side, `TenantScanDrawerTemplate.razor` is 2,481 lines with 23 `@inject`s and ~30 async load methods in one `@code` block.
- **Files:** `src/AzureDesignStudio.Server/Services/DeployService.cs`, `src/AzureDesignStudio.Desktop/Components/TenantScanDrawerTemplate.razor`, `src/AzureDesignStudio/Components/MenuDrawer/CodeDrawerTemplate.razor.cs` (580 lines), `src/AzureDesignStudio/Components/TopMenu.razor.cs` (513 lines)
- **Impact:** Every deploy-adjacent change (what-if gate, approval, staged batches, decommission windows) touches one 2,210-line file; merge conflicts and partial-reading regressions are near-certain. The drawer cannot be unit-tested because its logic lives in a component.
- **Fix approach:** Split `DeployService` by RPC family into partials or collaborator classes (`PlanApprovalService`, `StagedApplyService`, `DecommissionService`, `GuardrailService`) injected into a thin `Deploy` gRPC façade. Move drawer logic into a `TenantScanViewModel` in `src/AzureDesignStudio.Desktop/` so the behavior is testable without Razor.
- **Severity:** Medium

### No `Directory.Build.props` / `Directory.Packages.props` — copy-pasted build plumbing and drifted versions

- **Issue:** There is no central property or package file (`Directory.Build.props`, `Directory.Packages.props`, `global.json`, `nuget.config` all absent). The same `DropForeignThreadingAnalyzers` MSBuild target is copy-pasted into four project files, and ASP.NET patch versions disagree across projects.
- **Files:** `src/AzureDesignStudio/AzureDesignStudio.csproj`, `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj`, `src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj`, `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj`, `src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj`
- **Impact:** `Microsoft.AspNetCore.Components.WebAssembly` is `10.0.12` while `Microsoft.AspNetCore.Components.WebAssembly.Server` is `10.0.13` — a client/server patch skew that produces framework-asset mismatches only at runtime. `Microsoft.Extensions.Logging.Debug` is `10.0.0` while the rest of `Microsoft.Extensions.*` is `10.0.12`. The SDK is unpinned (`10.0.x` in CI), so builds are not reproducible across machines.
- **Fix approach:** Add `src/Directory.Packages.props` with `ManagePackageVersionsCentrally` and one `MicrosoftAspNetCoreVersion` property; move the analyzer-removal target into `src/Directory.Build.targets`; add `global.json` pinning the SDK feature band.
- **Severity:** Medium

### Duplicated ARM resource models instead of shared base types

- **Issue:** `AzureDesignStudio.AzureResources` is 447 hand-maintained ARM schema files (20,528 lines). Shared sub-object types are redeclared per service folder rather than defined once.
- **Files:** `src/AzureDesignStudio.AzureResources/` (`Network/` 216 files, `Compute/` 81, `Web/` 45, `Storage/` 36). Duplicate type names include `SubResource`, `Sku`, `PrivateEndpoint`, `PrivateEndpointConnection`, `ManagedServiceIdentity`, `ExtendedLocation`, `PublicIPAddressSku` — verified as repeated `public class`/`public partial class` declarations across folders.
- **Impact:** A schema correction (e.g. `Sku` casing, `PrivateEndpoint` shape) must be applied N times and silently diverges when one copy is missed; the generated ARM JSON then fails ARM validation at deploy time, not compile time.
- **Fix approach:** Hoist each shared shape into `src/AzureDesignStudio.AzureResources/Base/` (which already exists with 14 files) and reference it from service folders. `AzureDesignStudio.Core/AzureDesignStudio.Core.csproj` still carries commented-out `PackageReference`s to published `AzureDesignStudio.AzureResources.*` packages — either finish that extraction or delete the dead comment block.
- **Severity:** Medium

### Two divergent `ads-stencils.json` copies

- **Issue:** The runtime catalog and the container/K8s copy are different files with different content.
- **Files:** `src/AzureDesignStudio/wwwroot/ads-stencils.json` (8,459 bytes, 2026-10-20) vs `src/Configurations/ads-stencils.json` (2,467 bytes, 2026-10-11)
- **Impact:** The stencil list is the app's palette source (`src/AzureDesignStudio/Services/AdsContext.cs` loads it; `src/AzureDesignStudio.Desktop/App.xaml.cs` deserializes it from `AppContext.BaseDirectory`). The second copy has already drifted by six-plus nodes; whichever consumer reads the stale copy shows a different palette than the tests (`src/AzureDesignStudio.Core.Tests/StencilTests.cs`) assert.
- **Fix approach:** Keep exactly one file under `src/AzureDesignStudio/wwwroot/ads-stencils.json` and reference it from the Kubernetes ConfigMap/Desktop build with a link, not a copy — mirror how `ads-stencils.json` is already linked into `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` and `src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj`.
- **Severity:** Medium

### Dead binaries committed at repository root

- **Issue:** `bd2.dll` and `bd_served.dll` (both 64,277 bytes) are tracked in git and referenced by nothing in the repository.
- **Files:** `bd2.dll`, `bd_served.dll` (repo root)
- **Impact:** Unreviewed compiled artifacts in a repo whose CI runs CodeQL on source only; they are invisible to the build, cannot be provenance-verified, and invite "just drop a DLL here" habits.
- **Fix approach:** `git rm` both. If they are Bicep-decompiler experiments, that capability already lives in the `Azure.Bicep.Decompiler` package referenced by `src/AzureDesignStudio/AzureDesignStudio.csproj`.
- **Severity:** Low

### `#if DEBUG` branches that change infrastructure, not just diagnostics

- **Issue:** The server's provider, configuration source, schema bootstrap and reflection service are all chosen by compilation symbol rather than configuration.
- **Files:** `src/AzureDesignStudio.Server/Program.cs:30` (InMemory vs SQL Server), `:58` (`UseSqlServer`), `:72` (`EnsureCreated` + hand-written DDL, `!DEBUG` only), `:183` (gRPC reflection, `DEBUG` only)
- **Impact:** A Debug build and a Release build are different applications: schema created by EF model vs by raw T-SQL, relational constraints absent vs present, Key Vault config source absent vs required. Bugs that depend on unique indexes, `ExecuteDeleteAsync` semantics or SQL translation cannot reproduce in the developer's default build (see "InMemory-in-DEBUG divergence" below).
- **Fix approach:** Select the provider from `builder.Configuration`/`builder.Environment` (e.g. `Database:Provider` = `Sqlite` | `SqlServer`), and let `IsDevelopment()` gate reflection only. A SQLite or LocalDB provider gives developers a relational database with real constraints.
- **Severity:** Medium

---

## Known Bugs

No bug tracker entries are recorded in-tree (no `.github/ISSUE_TEMPLATE`, no `docs/bugs.md`). The following are **latent defects confirmed by code reading**, each with a reproducible trigger.

### `CryptoService` throws during DI construction when Key Vault is not configured

- **Symptoms:** Every `Deploy.*` gRPC call fails in a fresh Debug run (or any run without a real vault), with no useful message. `ICryptoService` is registered unconditionally but `DeployService` depends on it.
- **Files:** `src/AzureDesignStudio.Server/Services/CryptoService.cs:19-24`, `src/AzureDesignStudio.Server/Program.cs:52`, `src/AzureDesignStudio.Server/appsettings.json` (`KeyVaultName: "<kv name>"`)
- **Trigger:** Run the server with the checked-in placeholder config. `new Uri($"https://{configuration["KeyVaultName"]}.vault.azure.net/")` receives `https://<kv name>.vault.azure.net/` — `<`, `>` and the space make it an invalid URI, so the constructor throws before any RPC body runs.
- **Workaround:** Supply a real `KeyVaultName`/`KeyVaultKeyName` through user secrets (`UserSecretsId` is set in `src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj`).
- **Fix approach:** Make the vault client lazy and register a `NullCryptoService` when `KeyVaultName` is blank, so Debug runs can save subscription info to the InMemory store.
- **Severity:** Medium

### Stencil-load failure silently yields an empty palette

- **Symptoms:** The desktop app starts with no stencils and no visible error; the canvas is unusable and nothing is logged.
- **Files:** `src/AzureDesignStudio.Desktop/App.xaml.cs:24-31`
- **Trigger:** `ads-stencils.json` missing from `AppContext.BaseDirectory`, malformed, or `InitializeAsync()` throwing (e.g. after a schema change to `StencilModel`).
- **Workaround:** None available to the user; the failure is indistinguishable from "no design loaded".
- **Fix approach:** Log the exception through `ILogger` and surface a diagnostic banner; do not swallow `catch (Exception)` at startup.
- **Severity:** Medium

### Device-code sign-in state does not survive an app restart

- **Symptoms:** After signing in through the device-code fallback, the next launch is anonymous and `[Authorize]` gRPC calls return 401 until the user signs in again.
- **Files:** `src/AzureDesignStudio.Desktop/MsalAccountService.cs:39-66` (PCA built without token-cache serialization), `:210-215` (`RestorePrincipalAsync` doc-comment claims a "persisted cache")
- **Trigger:** Broker/WAM unavailable → `AcquireTokenWithDeviceCode` path → app restart. MSAL's in-memory user cache is discarded; the WAM broker's OS-level persistence only covers tokens it issued.
- **Workaround:** Re-sign-in (interactive) each launch on broker-less setups.
- **Fix approach:** Serialize `_pca.UserTokenCache` into `WindowsSecretStore` (`AzureDesignStudio/` namespace is already reserved in `src/AzureDesignStudio.Desktop/WindowsSecretStore.cs`) and rehydrate in the constructor.
- **Severity:** Low

### Static `ServiceProvider` installed only after build

- **Symptoms:** `MauiProgram.Services` is `null!` until `CreateMauiApp` finishes; any component constructed before that (or any resumed static state) dereferences null.
- **Files:** `src/AzureDesignStudio.Desktop/MauiProgram.cs:26` (`public static IServiceProvider Services { get; private set; } = null!;`), consumed at `src/AzureDesignStudio.Desktop/App.xaml.cs:18`
- **Trigger:** Startup ordering change, or a second window / activation path that runs before `CreateMauiApp` returns.
- **Fix approach:** Resolve stencils through the injected `AdsContext` in the window/page rather than a static locator.
- **Severity:** Low

---

## Security Considerations

### Subscription client secrets travel on the gRPC wire and are encrypted with a single RSA key

- **Risk:** The Azure service-principal `ClientSecret` is a protobuf field (`SubscriptionInfo.client_secret` in `src/AzureDesignStudio.SharedModels/deploy_model.proto`), collected in the UI at `src/AzureDesignStudio/Components/MenuDrawer/AzSubscriptionDrawerTemplate.razor:20`, sent to the server, then encrypted at rest with RSA-OAEP against one Key Vault key.
- **Files:** `src/AzureDesignStudio.Server/Services/DeployService.cs:76` (encrypt on save), `:138` (decrypt on read), `src/AzureDesignStudio.Server/Services/CryptoService.cs:37-42`; ciphertext column `src/AzureDesignStudio.Server/Models/DesignDbContext.cs:49`
- **Current mitigation:** Transport TLS in the hosted path (`app.UseHttpsRedirection()`), ciphertext at rest, Key Vault key never leaves Azure, `WindowsSecretStore`-backed SP credentials for the desktop's own ARM calls (`src/AzureDesignStudio.Desktop/AdsTokenBroker.cs:34`).
- **Recommendations:** (1) Never decrypt for display — no read path should return the secret; today `LoadSubscriptionInfo` decrypts and can return it to the client. (2) Replace RSA-OAEP with an envelope scheme or Key Vault-native reference, because `CryptographyClient.EncryptAsync` fails outright once plaintext exceeds the key's OAEP payload limit (~190 bytes for RSA-2048) — long secrets fail at save time with a generic 400 (`DeployService.cs:80-85`). (3) `Encoding.Default` in `CryptoService.Decrypt` is culture/OS dependent; use `Encoding.UTF8` explicitly.
- **Severity:** High

### Authorization is single-scope; approval, guardrail and decommission gates have no role separation

- **Risk:** Both `DesignService` and `DeployService` are guarded only by `[Authorize]` + `[RequiredScope("AzureAd:Scopes")]` (`Server.Access`). Nothing distinguishes the human who *requests* a deploy from the human who *approves* it, or from the human who *edits the do-not-touch list*. Self-approval is therefore the default workflow.
- **Files:** `src/AzureDesignStudio.Server/Services/DeployService.cs:26-28` (class attributes), `:604` (`ApprovePlan` — replaces any existing approval for the hash with the caller's own), `:1322` (`SetGuardrailPolicy` — writes the protect-nothing-relevant policy with the caller as author), `:1745` (`ApproveDecomWindow`), `src/AzureDesignStudio.Server/Services/DesignService.cs:10-11`
- **Impact:** The plan-approval, guardrail and decommission-window controls are recorded-but-not-enforced assertions of intent; a single compromised or careless account can approve and apply a destructive plan, and can widen the guardrail policy that is supposed to stop it.
- **Current mitigation:** Immutable audit rows (`AdsPlanApplyEvents`, `AdsDecomWindowEvents`) record `ApprovedByUserId`, and apply-time re-judgement re-reads the server-side policy (`GuardrailPolicyModel` doc-comment at `src/AzureDesignStudio.Server/Models/DesignDbContext.cs`). Detection exists; prevention does not.
- **Recommendations:** Add Entra app roles (`Deploy.Approver`) via `[Authorize(Roles=...)]` or an authorization policy on `ApprovePlan`/`ExecuteDecomWindow`/`SetGuardrailPolicy`, and reject when `ApprovedByUserId == requesting user` for high-blast-radius plans.
- **Severity:** High

### Fail-open authorization default on the gRPC surface

- **Risk:** `MapGrpcService` is called with `.EnableGrpcWeb()` but never `RequireAuthorization()`, and no `FallbackPolicy` is configured. Authorization depends entirely on remembering `[Authorize]` on each service class.
- **Files:** `src/AzureDesignStudio.Server/Program.cs:169-183`
- **Impact:** Any new endpoint (or a refactor that drops the attribute) is publicly reachable, including from the internet through `src/Configurations/main/ingress.yaml`. `RetailPriceService` is anonymous *by design* (documented at `src/AzureDesignStudio.Server/Services/RetailPriceService.cs:10-13`), which makes the absence of a default policy easy to miss.
- **Current mitigation:** `UseAuthentication()`/`UseAuthorization()` are wired before `MapGrpcService`; gRPC reflection is Debug-only.
- **Recommendations:** Set `FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()` and mark `RetailPriceService` with `[AllowAnonymous]` explicitly.
- **Severity:** High

### Container runs as root with a mutable, unpinned image reference

- **Risk:** `src/AzureDesignStudio.Server/Dockerfile` defines no `USER`, so the process runs as root in the `aspnet:10.0` image. The cluster manifest pins a tag, not a digest.
- **Files:** `src/AzureDesignStudio.Server/Dockerfile:42-45`, `src/Configurations/main/deployment.yaml:17` (`image: chunliu.azurecr.io/ads-server:0.4.3-7`, `imagePullPolicy: IfNotPresent`)
- **Impact:** A container escape lands on root; a re-pushed tag silently changes what two replicas run, and `IfNotPresent` means a node never re-pulls.
- **Recommendations:** Add `USER $APP_UID` before `ENTRYPOINT`, add a `HEALTHCHECK`, reference the image by digest, and bump `src/version.json` (currently `1.0`) in the same commit as the manifest tag (currently `0.4.3-7`).
- **Severity:** Medium

### Hardcoded legacy claim URI for user identity

- **Risk:** User identity is read from the `http://schemas.microsoft.com/identity/claims/objectidentifier` claim only.
- **Files:** `src/AzureDesignStudio.Server/Services/ServiceTools.cs:10-14`, consumed at `DeployService.cs:50`, `DesignService.cs:30`
- **Impact:** `JwtBearerOptions.MapInboundClaims` controls whether `oid` is rewritten to this URI. If mapping is disabled (or the token type changes, e.g. a future `JsonWebTokenHandler` default), `GetUserId` returns null and *every* authenticated call degrades to `401 "Access denied"` with only a warning log. Separately, `new Guid(userIdClaim)` throws on any non-GUID claim shape and is converted to a generic 500 by the catch-all.
- **Recommendations:** Accept `oid` or the URI form; pin `MapInboundClaims` explicitly next to `NameClaimType` in `src/AzureDesignStudio.Server/Program.cs:24-29`; use `Guid.TryParse` and return 401 rather than relying on the catch-all.
- **Severity:** Medium

### Service worker caches `appsettings.json` with the app shell

- **Risk:** The published service worker caches every `.json` asset under a version-keyed cache.
- **Files:** `src/AzureDesignStudio/wwwroot/service-worker.published.js:15-17` (`/\.json$/` in `offlineAssetsInclude`), config at `src/AzureDesignStudio/wwwroot/appsettings.json`
- **Impact:** The Azure AD `ClientId` and `ServerScope` live in `appsettings.json`. After a client-id rotation or scope change, existing users keep serving the old config until the service-worker version changes — a silent, hard-to-diagnose auth outage.
- **Recommendations:** Exclude `appsettings*.json` from `offlineAssetsInclude` and fetch it network-first.
- **Severity:** Medium

### Dev-only surface accidentally shipped is currently controlled by compilation symbol only

- **Files:** `src/AzureDesignStudio.Server/Program.cs:183-188` (gRPC reflection), `src/AzureDesignStudio.Desktop/MauiProgram.cs:165` (BlazorWebView dev tools)
- **Impact:** A Debug-built container (e.g. a developer image pushed from a laptop) exposes full service descriptors to anyone who can reach the ingress. The `#if` guard is invisible in the deployed artifact.
- **Recommendations:** Gate on `app.Environment.IsDevelopment()` in addition to the symbol.
- **Severity:** Low

---

## Performance Bottlenecks

### Per-node JSON serialize/deserialize round-trip when retyping DTOs

- **Problem:** Loading a diagram re-types base `AzureNodeDto` instances by serializing each node to JSON and deserializing into the derived type — once per node.
- **Files:** `src/AzureDesignStudio.Core/DataModelFactory.cs:261-273`
- **Cause:** `JsonSerializer.Deserialize(JsonSerializer.Serialize(d), derived)` allocates a full JSON string and a second object per node; `Serialize` here also runs over `object` (runtime type), defeating source-gen fast paths and inviting trimming problems (see "Trim/AOT" below).
- **Improvement path:** Retype with AutoMapper (`mapper.Map(d, typeof(AzureNodeDto), derived)`) or a generated `switch` factory that copies known properties — the `DataModelFactory` is already partially source-generated (`src/AzureDesignStudio.SourceGeneration/`).
- **Severity:** Medium

### Synchronous file append on every Warning-or-worse log, from multiple writers, unbounded

- **Problem:** `FileLogger` calls `File.AppendAllText` synchronously; the crash handlers append to the same path; nothing rotates or truncates.
- **Files:** `src/AzureDesignStudio.Desktop/MauiProgram.cs:29-33` (crash handlers → `%TEMP%\ads_desktop_crash.log`), `:186-196` (`FileLoggerProvider`)
- **Cause:** UI-thread blocking I/O on a file also written by the unhandled-exception handler and by background scan tasks (`src/AzureDesignStudio.Desktop/TenantScanSession.cs:78`). Concurrent appends can raise `IOException` inside the logger, and the file grows without bound in `%TEMP%`.
- **Improvement path:** Write through a `Channel<string>` with a single consumer, cap the file (e.g. 5 MB with one rollover), and use `%LOCALAPPDATA%` instead of `%TEMP%`.
- **Severity:** Medium

### `EnsureCreated()` executed per service instantiation

- **Problem:** `DesignService` and `DeployService` call `_designContext.Database.EnsureCreated()` in their constructors, so the check runs on every RPC (gRPC services are effectively per-call).
- **Files:** `src/AzureDesignStudio.Server/Services/DeployService.cs:39-40`, `src/AzureDesignStudio.Server/Services/DesignService.cs:20`
- **Cause:** Duplicates the startup bootstrap already performed at `src/AzureDesignStudio.Server/Program.cs:74-81`; against SQL Server this is an extra metadata round-trip on the hot path, and it hides provider differences during development.
- **Improvement path:** Remove both constructor calls; keep schema work in the `Program.cs` bootstrap (or replace it with real migrations — see "Missing Critical Features").
- **Severity:** Low

### Long-lived streaming deploy with fixed 2-second polling

- **Problem:** `CreateDeployment` keeps the gRPC stream open for the whole ARM LRO lifetime, polling every 2 s and writing a `processing` frame each time.
- **Files:** `src/AzureDesignStudio.Server/Services/DeployService.cs:1497-1540`
- **Cause:** A 20-minute deployment is 600 progress frames over one HTTP/2 stream; through `nginx` (`src/Configurations/main/ingress.yaml`, `limit-rps: 30`) this is one long-lived connection whose survival depends on proxy read timeouts that are not configured in-repo.
- **Improvement path:** Keep the polling (the audit semantics are deliberate and documented), but raise the interval with backoff after the first minute and document the required ingress `proxy-read-timeout`.
- **Severity:** Low

### Pending-design listing materializes every payload

- **Problem:** The sync queue loads `Data` (full design JSON) for every unsynced design just to order by `SavedAt`.
- **Files:** `src/AzureDesignStudio.Workspace/SqliteDesignOfflineStore.cs:44-53`
- **Cause:** SQLite cannot `ORDER BY DateTimeOffset`, so ordering is client-side — which forces the full projection.
- **Improvement path:** Store `SavedAtTicks` as `INTEGER` (or order by the existing `TEXT` ISO column) and select `Name` only for ordering.
- **Severity:** Low

---

## Fragile Areas

### EF InMemory in DEBUG diverges from SQL Server in production

- **Files:** `src/AzureDesignStudio.Server/Program.cs:30-31` (Debug) vs `:58` (Release), `src/AzureDesignStudio.Server/Models/DesignDbContext.cs`, `src/AzureDesignStudio.Server/Program.cs:74-81` + hand-written DDL at `:86-150`
- **Why fragile:** With InMemory there are no unique indexes, no relational constraints, no `ExecuteDelete` translation, no transaction isolation, and all rows vanish at restart. Production adds a `PlanHash` primary key, a unique index on `AdsDesigns`, and six tables created by raw T-SQL rather than by the EF model. The code exercises `_designContext.AdsPlanApprovals.FindAsync([planHash])`, `Remove` + `Add` replacement, and cross-table reads — behavior that is *modeled* differently in each provider. There is no EF migration anywhere in the repo.
- **Safe modification:** Never assume an index or constraint exists because the model declares one; verify against `src/AzureDesignStudio.Server/Program.cs` DDL. Any new entity must be added to **both** `DesignDbContext` and the raw DDL block, or a Release deploy starts with a missing table.
- **Test coverage:** `src/AzureDesignStudio.Core.Tests/` has no test project reference to `src/AzureDesignStudio.Server/` at all, so no test exercises either provider's schema.
- **Severity:** High

### In-process singletons assume a single replica, but the cluster runs two

- **Files:** `src/AzureDesignStudio.Server/Program.cs:56-57` (`AddSingleton(new WhatIfTokenGate(...))`, `AddMemoryCache()`), `src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs` (a `Dictionary` under a lock), `src/Configurations/main/deployment.yaml:8` (`replicas: 2`), `src/AzureDesignStudio.Server/Services/RetailPriceService.cs:18` (per-instance cache)
- **Why fragile:** A what-if token is issued by whichever pod served `WhatIf` and validated by whichever pod serves `CreateDeployment`. With two replicas behind a plain round-robin `ClusterIP` (`src/Configurations/main/service.yaml`), roughly half of all deploys fail with `WHAT_IF_STALE: What-If token is unknown or already used` — a message that tells the user to re-run What-If, which then fails the same way. The tokens also vanish on pod restart, so a rolling update silently invalidates every in-flight review. The retail-price cache is likewise duplicated per pod.
- **Safe modification:** Do not add state to `WhatIfTokenGate` without solving distribution. Any change to the approval flow must keep plan approvals in the database (`AdsPlanApprovals`) — those survive restarts; the token gate does not.
- **Test coverage:** `src/AzureDesignStudio.Core.Tests/WhatIfTests.cs` and `PlanApprovalGateTests.cs` cover single-process semantics only.
- **Severity:** High

### SQLite workspace has no WAL, no busy timeout, and concurrent writers

- **Files:** `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs:84-89` (`Cache=Shared`, no `journal_mode`, no `busy_timeout`), `src/AzureDesignStudio.Workspace/SqliteDesignOfflineStore.cs` (write per save/delete), `src/AzureDesignStudio.Desktop/TenantScanSession.cs:78` (background scan writing snapshots), `src/AzureDesignStudio.Workspace/WorkspaceChanges.cs`, `WorkspaceScans.cs`
- **Why fragile:** A tenant scan writes `ResourceSnapshotRecord` rows continuously from a background task while the UI autosaves designs, records node positions (`WorkspaceDbContext.cs` `NodePositions` upsert) and appends audit rows — all from separate `DbContext` instances on one file in rollback-journal mode. Readers block writers and vice versa; `SQLITE_BUSY` surfaces as a save failure or a scan that dies mid-run. `WorkspaceDb` also mutates schema at startup (`EnsureAddedColumns`, `EnsureAdditiveTables`) with `ALTER TABLE`/`CREATE TABLE IF NOT EXISTS`, which requires an exclusive lock at launch.
- **Safe modification:** Any new table needs an entry in `EnsureAdditiveTables` (EnsureCreated does not add tables to an existing file). Do not open a second `WorkspaceDb` against the same file.
- **Test coverage:** `src/AzureDesignStudio.Core.Tests/WorkspaceTests.cs` and `ChangeFeedTests.cs` use a temp directory and single-threaded access — the concurrency case is untested.
- **Severity:** Medium

### Trimming and AOT are enabled with reflection-dependent code paths

- **Files:** `src/AzureDesignStudio/AzureDesignStudio.csproj:9-12` (`PublishTrimmed=true`, `RunAOTCompilation` in Release), `:57-62` (`TrimmerRootAssembly` for MSAL and `System.Collections.Immutable`), `src/AzureDesignStudio/Program.cs:21` + `:59` (`AddAutoMapper(typeof(AzureNodeProfile))`), `src/AzureDesignStudio.Core/DataModelFactory.cs:261-273` (runtime `JsonSerializer` with a computed `Type`), `src/AzureDesignStudio.Core/Common/ResourceBaseJsonConverter.cs:22` (`Activator.CreateInstance`)
- **Why fragile:** AutoMapper 12 is reflection-based and not annotated trim-safe; the DTO re-typing path calls the reflection `JsonSerializer` overload with a runtime `Type`; `Probe` objects are created by `Activator.CreateInstance`. All three are trimmed or AOT-compiled without any `JsonSerializerContext` or `DynamicDependency`. Failure mode is **runtime only** — a published WASM build that maps or deserializes nothing, after a green CI (CI runs `dotnet build`, never a trimmed `dotnet publish` of the WASM app; only the release workflow publishes). The project already records one such AOT-only failure in a csproj comment (the `System.Private.Windows.Core` / `System.Formats.Nrbf` workaround).
- **Safe modification:** Any new DTO, mapper profile or ARM resource type must be reachable from a static reference. Do not rely on assembly scanning for new types.
- **Severity:** High

### Client `ServiceProvider` built inside an HTTP handler factory

- **Files:** `src/AzureDesignStudio/Program.cs:59-64` and `:69-74` (`builder.Services.BuildServiceProvider().GetRequiredService<BaseAddressAuthorizationMessageHandler>()`)
- **Why fragile:** Each `ConfigurePrimaryHttpMessageHandler` invocation constructs a **second** service provider. The `BaseAddressAuthorizationMessageHandler` it resolves belongs to that duplicate container, so it is not connected to the circuit's authentication state or its `IAccessTokenProvider`; the original provider is never disposed. Auth behaves correctly today by luck of the handler's design, but any state-dependent handler will read a different singleton graph.
- **Safe modification:** Use the `IServiceProvider` argument (`(sp, o) => ...` / `ConfigurePrimaryHttpMessageHandler(sp => ...)`) — the pattern already used at `src/AzureDesignStudio.Desktop/MauiProgram.cs:47-51`.
- **Severity:** Medium

### Unbounded workspace growth — no retention for scans, snapshots, change feed, audit

- **Files:** `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs` (`ScanRecord`, `ResourceSnapshotRecord`, `ChangeFeedRecord`, `AuditRecord`, `CostCacheEntry`), `src/AzureDesignStudio.Workspace/TopologyPositions.cs:40` (`PruneAsync` — the only prune in the codebase, and it covers node positions only)
- **Why fragile:** Every tenant scan appends one `ScanRecord` plus one `ResourceSnapshotRecord` per resource. A 5,000-resource tenant scanned weekly produces ~250k rows in a year, in the same file the UI writes to. There is no vacuum, no expiry, and no size guard.
- **Safe modification:** If you add a new scanned collection, add it to a retention routine in the same change.
- **Severity:** Medium

### Hand-written schema bootstrap instead of migrations

- **Files:** `src/AzureDesignStudio.Server/Program.cs:74-150` (≈60 lines of idempotent `IF OBJECT_ID ... CREATE TABLE` / `COL_LENGTH ... ALTER TABLE` T-SQL)
- **Why fragile:** Every model change must be mirrored by hand in this block; a forgotten `COL_LENGTH` guard means the column never appears on an existing database, and `EnsureCreated()` on a pre-existing schema never adds columns. There is no rollback path and no schema version anywhere.
- **Safe modification:** When changing `src/AzureDesignStudio.Server/Models/DesignDbContext.cs`, add the matching DDL statement *and* a guard for existing rows in the same commit.
- **Severity:** High

---

## Scaling Limits

### Deploy throughput is bounded by one replica's request budget and a 750 m CPU limit

- **Current capacity:** `replicas: 2`, `cpu: 750m`, `memory: 2Gi` (`src/Configurations/main/deployment.yaml:12-16`); ingress `limit-rps: 30` (`src/Configurations/main/ingress.yaml:10`).
- **Limit:** Each deploy holds a server thread/task for its full LRO lifetime (`DeployService.cs:1497-1540`) while also issuing ARM polling and Resource Graph calls; a handful of concurrent long deploys saturate the CPU limit, and Kestrel's queue then throttles everyone. The 30 rps ingress limit counts long-lived streams as requests only at open.
- **Scaling path:** Make polling resumable by an out-of-band reconciliation job (the audit tables already have the keys: `ApplyId`, `DeploymentName`, `PlanHash`) so the gRPC stream is a subscriber rather than the driver; then the pod count can rise without multiplying open streams.

### What-if tokens and the retail-price cache do not scale past one replica

- **Current capacity:** Correct only at `replicas: 1`.
- **Limit:** See "In-process singletons assume a single replica". Horizontal scaling actively *breaks* deploys rather than degrading them.
- **Scaling path:** Move `WhatIfTokenGate` state to the database (the `AdsPlanApprovals` table already stores plan JSON and hashes) or to a distributed cache; move the retail-price cache to `IDistributedCache`.

### Per-user design quota is enforced by a count query, not a constraint

- **Current capacity:** 10 designs per user, checked by `Count()` before insert in `src/AzureDesignStudio.Server/Services/DesignService.cs:47-53`.
- **Limit:** Two concurrent saves from the same user can both pass the check. There is no unique index supporting the quota and no transaction wrapping check+insert.
- **Scaling path:** Enforce with a unique index on `(UserId, Name)` plus a transactional counter, or accept the race explicitly in the code comment.

---

## Dependencies at Risk

### AutoMapper 12.0.1 with an accepted advisory

- **Risk:** GHSA-rvv3-g6hj-g44x (DoS via uncontrolled recursion). The repo suppresses it project-wide.
- **Files:** `src/AzureDesignStudio/AzureDesignStudio.csproj:19-21` and `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj:10-13` (`<NoWarn>$(NoWarn);NU1903</NoWarn>` with a justification comment); also `AutoMapper.Extensions.Microsoft.DependencyInjection` 12.0.0.
- **Impact:** The suppression is per-project, so a future direct `AutoMapper` use in a project without the `NoWarn` will fail restore (or be reintroduced unexamined). The stated mitigation ("mappings are shallow node DTOs") is only true while nobody adds a recursive mapping — and `DataModelFactory.cs:262` already maps DTOs by runtime type.
- **Migration plan:** AutoMapper 13+ requires a commercial license; the practical alternatives are a generated mapper (the repo already ships `src/AzureDesignStudio.SourceGeneration/` with Scriban templates that emit `AzureNodeDto`/`AzureNodeProfile`) or hand-written `ToDto` methods. Removing AutoMapper also removes the largest trimming hazard (see above).

### Azure Bicep Decompiler drags Windows-only compatibility assemblies into a browser app

- **Risk:** `Azure.Bicep.Decompiler` 0.48.1 pulls `Microsoft.Windows.Compatibility` → `System.Private.Windows.Core`, which the project separately pins (`System.Formats.Nrbf` 10.0.12) purely so Mono AOT can load it, and which forces a `TrimmerRootAssembly` for `System.Collections.Immutable`.
- **Files:** `src/AzureDesignStudio/AzureDesignStudio.csproj:14`, `:44-46`, `:52-54`, `:57-62`
- **Impact:** Three workarounds in one project file, each with a comment explaining a past breakage. The decompiler is a client-side nicety; its transitive graph is the main source of trim/AOT fragility in the WASM app.
- **Migration plan:** Move decompilation server-side behind a new RPC (the server is already the ARM authority) and drop the package from the WASM project entirely; or pin the decompiler major and re-verify AOT on every bump.

### Test stack version skew

- **Risk:** `xunit` 2.9.2 with `xunit.runner.visualstudio` 2.8.2 and `Microsoft.NET.Test.Sdk` 17.12.0.
- **Files:** `src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:19-24`
- **Impact:** Mismatched runner/adapter majors are a common source of silently-not-discovered tests after a future SDK update; nothing pins the trio together.
- **Migration plan:** Move to `xunit.v3` or align all three, then verify discovery count in CI (`dotnet test` output) rather than trusting an exit code of 0.

---

## Missing Critical Features

### No EF migrations

- **Problem:** Schema is created by `EnsureCreated()` plus hand-written idempotent T-SQL; there is no `Migrations/` folder in `src/AzureDesignStudio.Server/`.
- **Blocks:** Any non-additive change (rename, type widening, index on existing data) has no supported path; the `ALTER TABLE` guards only ever add nullable columns.

### No test project for the server, the desktop host, or the WASM client

- **Problem:** The only test project is `src/AzureDesignStudio.Core.Tests`, which references `Core`, `Workspace`, `Discovery` — not `Server`, `Desktop`, `Mcp`, or the WASM app.
- **Blocks:** 3,000 lines of server code (`DeployService` 2,210) and 4,063 lines of desktop host code have zero automated coverage; see "Test Coverage Gaps".

### No observability contract (health/readiness/metrics)

- **Problem:** `src/AzureDesignStudio.Server/Program.cs` maps no `/healthz`, `/readyz` or metrics endpoint; the Kubernetes deployment defines no `livenessProbe`/`readinessProbe` (`src/Configurations/main/deployment.yaml`). Diagnostics are Application Insights only (`AdsTelemetryInitializer` sets `RoleName` and nothing else).
- **Blocks:** Rolling updates route traffic to a pod that has not finished the `EnsureCreated()` + DDL bootstrap; an unhealthy replica is never restarted.

### No retention or export policy for the local workspace

- **Problem:** No prune/vacuum/export path for scans, snapshots, change feed or audit rows (see "Unbounded workspace growth").
- **Blocks:** Long-lived installs degrade to a multi-gigabyte `workspace.db` with no supported remediation beyond deleting the file.

---

## Test Coverage Gaps

### Server-side deploy pipeline is entirely untested

- **What's not tested:** All of `src/AzureDesignStudio.Server/Services/DeployService.cs` — what-if gate consumption, plan approval, staged batch orchestration, decommission windows, guardrail enforcement, audit event ordering, streaming outcome mapping.
- **Files:** `src/AzureDesignStudio.Server/Services/DeployService.cs`, `src/AzureDesignStudio.Server/Services/DesignService.cs`, `src/AzureDesignStudio.Server/Services/CryptoService.cs`
- **Risk:** The audit-trail invariants ("one terminal event per `ApplyId`", "`OutcomeUnknown` is never reported as `failed`") are the codebase's most safety-critical promises and are verified only by reading. A refactor of the 44 `catch` blocks can silently downgrade an `OutcomeUnknown` to `Failed` — which the code comments explicitly warn is "a second lie".
- **Priority:** High

### Live-Azure ARM validation is excluded from CI

- **What's not tested in CI:** The four classes that call real ARM (`ApiMTests`, `NetworkTests`, `SqlDatabaseTests`, `WebTests`) inherit `TestBase`, which is itself `[Trait("Category", "LiveAzure")]` and requires `az login` plus a pre-created `ads-arm-test-rg`. CI runs `dotnet test ... --filter "Category!=LiveAzure"`.
- **Files:** `src/AzureDesignStudio.Core.Tests/TestBase.cs:10-32`, `.github/workflows/build.yml:38`, `src/AzureDesignStudio.Core.Tests/{ApiMTests,NetworkTests,SqlDatabaseTests,WebTests}.cs`
- **Risk:** ARM acceptance for APIM, networking, SQL and Web template generation — the highest-churn ARM models, 216 + 45 + 14 files — is never checked in CI. Offline golden tests (`OfflineArmTemplateTests.cs`, `StencilExportGoldenTests.cs`, `NewResourceTypesArmTests.cs`) assert structure, not that ARM accepts the payload. A schema typo ships green and fails at deploy time.
- **Priority:** High

### Trimming and AOT are never exercised by CI

- **What's not tested:** CI runs `dotnet build -c Release` for the WASM project; it never runs `dotnet publish` with `PublishTrimmed`/`RunAOTCompilation`. The release workflow publishes but publishes no test signal.
- **Files:** `.github/workflows/build.yml:29-36`, `.github/workflows/publish.yml:20-32`, `src/AzureDesignStudio/AzureDesignStudio.csproj:9-12`
- **Risk:** Trim/AOT breakage (AutoMapper maps, JSON DTO re-typing, Bicep decompiler) is discovered by users of the published site, not by CI. Prior breakage of exactly this kind is documented in the csproj.
- **Priority:** High

### Client-side and desktop components have no tests

- **What's not tested:** Every Razor component and the desktop host: `src/AzureDesignStudio.Desktop/Components/TenantScanDrawerTemplate.razor` (2,481 lines), `src/AzureDesignStudio/Components/DiagramPanel.razor.cs`, `AzureDesignStudio.Core/Common/DiagramHistory.cs` (518 lines of undo/redo gesture coalescing), `src/AzureDesignStudio.Desktop/MsalAccountService.cs`, `WindowsSecretStore.cs`, `AdsTokenBroker.cs`.
- **Risk:** `DiagramHistory` translates third-party library events into undoable commands; a library bump (untested) can silently break undo for deletes and group membership. MSAL/broker failure paths are exercised only on developer machines with WAM.
- **Priority:** Medium

### SQLite concurrency and schema-upgrade paths untested

- **What's not tested:** Concurrent readers/writers against `workspace.db`; `EnsureAddedColumns`/`EnsureAdditiveTables` against a database file created by a previous version; `WorkspaceDb` behaviour when the file is locked.
- **Files:** `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs:82-140`, `src/AzureDesignStudio.Core.Tests/WorkspaceTests.cs`, `ChangeFeedTests.cs`
- **Risk:** Upgrade bugs surface only for existing users on first launch of a new build — the worst possible place to find them, since the workspace is the only local copy of their designs.
- **Priority:** Medium

---

*Concerns audit: 2026-10-21*
