# Project Research Summary

**Project:** Azure Design Studio
**Domain:** Brownfield .NET 10 Azure architecture/governance workspace — Blazor WASM + MAUI Windows desktop sharing one Razor UI, gRPC server, SQLite workspace, read-only MCP
**Researched:** 2026-10-21
**Confidence:** HIGH on mechanics and versions; MEDIUM on delivery duration, signing eligibility, and Round 2 effort

## Executive Summary

Azure Design Studio is not a greenfield product: v1.0 and v2 shipped 49 stencils, ARM/Bicep export, tenant scans, topology/drift, what-if-gated apply, deployment stacks, decommission planning, and a read-only MCP surface across 11 projects. This milestone is a **trust-and-delivery milestone**, not a feature milestone. Its four outcomes are: land the 120 local commits and 54 working-tree paths, close the v3 Trust Gate (identity, credentials, limits, guardrails, migrations, CSP, tests), finish the already-started Round 2 shell, and produce a signed MSIX that installs on a clean Windows machine. Experts build this kind of release by making the seams single-sourced — schema, identity, packaging identity, payload limits, and command lifetime — then proving each with a hosted test before packaging.

The research converges on one execution order despite disagreeing on details: **land first, make the build reproducible, make the server testable, migrate the databases, harden the server, complete the shell in parallel, then sign**. The repo already has the right seams for this (`WorkspaceDb`, `AdsHostFeatures`, `AdsDesignText`, `GrpcClientFactory`); the milestone's discipline is to attach each new capability to exactly one of them instead of adding a parallel path. Three changes are forced rather than optional: AutoMapper 12.0.1 must go (CVE-2026-32933, and the patched versions are commercially licensed), `EnsureCreated` plus hand-rolled DDL must become a baselined migration, and the MSIX `Publisher` must become a real certificate subject before the first pilot build.

The dominant risks are data loss and unverifiable delivery, not missing features. A naive `EnsureCreated` → `Migrate()` swap can strand every existing `workspace.db`; the field schema has weaker constraints than the model declares; WAL changes what a valid backup and a read-only MCP open mean; gRPC limits have client-side halves with 4 MB defaults; and the MSIX identity tuple already disagrees across csproj, manifest, and workflow. Each risk has a concrete, testable mitigation below, and the roadmap should treat those tests as exit criteria rather than follow-up work.

## Key Findings

**Research inputs synthesized:** `.planning/research/STACK.md`, `.planning/research/FEATURES.md`, `.planning/research/ARCHITECTURE.md`, `.planning/research/PITFALLS.md`.

### Recommended Stack

The stack delta is small and version-pinned. Framework, EF Core, and ASP.NET test packages move together to **10.0.13** (bUnit 2.12.5's dependency floor forces it), the gRPC set moves as one train to **2.84.0**, and the SDK is pinned at **10.0.401 / `rollForward: latestPatch`** (resolving the 10.0.400/`latestFeature` disagreement between STACK and ARCHITECTURE). Server hardening uses in-framework primitives — rate limiter, request timeouts, health checks, gRPC message caps — so no new runtime dependency is introduced beyond `Grpc.AspNetCore.HealthChecks`.

**Core technologies:**
- **EF Core 10.0.13 + `dotnet-ef` 10.0.13**: migrations for both contexts — the repo's 6.0.4 tool cannot read an EF 10 model; `Microsoft.Data.SqlClient` pinned to 7.1.1 to keep the Entra broker out of the Linux container.
- **gRPC 2.84.0 set** (`Grpc.AspNetCore`, `.Web`, `.Server.Reflection`, `Grpc.Net.Client`, `.Web`, `.ClientFactory`, `Grpc.Tools`): server, client, and codegen must share one release; message-size caps live here.
- **xUnit 2.9.3 + `xunit.runner.visualstudio` 2.8.2 + coverlet 6.0.4**: one test stack; migrating 755 existing cases to xunit.v3 mid-gate is unnecessary risk.
- **bUnit 2.12.5, `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, `Microsoft.Playwright` 1.63.0**: component, hosted-server, and browser/WebView2 layers respectively.
- **MSIX toolchain**: `dotnet workload restore <Desktop.csproj>` (runner images ship MAUI 9 workloads only), `WindowsPackageType=Package`, `RuntimeIdentifierOverride=win-x64`, then `signtool` or `azure/trusted-signing-action@v0`.
- **Build inputs**: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `packages.lock.json` — prerequisites for caching, locked restore, and a reproducible signed artifact.
- **AutoMapper exit**: emit explicit maps from the existing Roslyn/Scriban generator; Mapperly 4.3.1 is the accepted alternative, not the default.

### Expected Features

The feature research is a **pilot-readiness filter**, not a catalog. Everything in the first group is P1: if one is missing, the build is not pilot-ready.

**Must have (table stakes):**
- Typed identity (`oid`/`tid`, 401/403, never 500) and fail-closed authorization on every RPC — the current surface has a fail-open default and 26 `new Guid(...)` sites.
- Tenant-scoped, role-gated guardrail policy — today a single global row (`GuardrailRowId = 1`) is writable by any authenticated caller.
- No server-recoverable subscription secret (8 `ClientSecretCredential` sites); DEBUG must work with no Key Vault or Azure credentials.
- Bounded server work (rate limit, request/message caps, deadlines calibrated per method, concurrency cap) plus anonymous `/health/live` and `/health/ready`.
- One design payload contract enforced on client and server, with the design cap below the transport cap and legacy rows still loading.
- CSP + security headers, vulnerable-package gate, SBOM, and **fail-closed** signing.
- Signed MSIX whose `Publisher` equals the certificate subject, timestamped, verified, and installed on a clean machine.
- Packaged-app workspace continuity: import/migration prompt and MCP dual-path discovery, or pilots appear to lose designs.
- EF migrations for both databases, WAL + `busy_timeout` + `quick_check`, and a real (WAL-safe) backup/restore path.
- Windows Desktop PR build, server integration suite, component suite, committed E2E smoke, and support bundle with `ADS-####` codes.
- Round 2 shell: three-zone **contract** with persisted widths, owner-scoped palette lifecycle and keyboard contract, theme tokens in both HTML entries, status bar that distinguishes local/synced/offline, InfoTips with a link-rot CI gate, and a WCAG 2.2 AA gate.

**Should have (competitive, after the gate):** virtualized tenant forest (requires a paged read path), local cost rail with provenance badges, deterministic canvas architecture linter, audit hash-chain verification, telemetry privacy contract, screenshot-in-PR, coverage/flake budget, MCP contract snapshot.

**Defer (v3.1+):** server workspace/cross-device read, multi-tenant switcher and cross-tenant store, IaC import depth/AVM, FinOps closure, CLI + GitHub Action, 3D/parallax and native title bar.

**Refuse (anti-features):** multi-cloud, CRDT collaboration, home-grown Azure Policy evaluation, tenant scans in WASM, a cross-tenant connection store in this milestone, 3D before the 2D performance gate, claiming native Mica, a second desktop E2E stack, agent writes without approval, and telemetry-on-by-default.

### Architecture Approach

The architecture is unchanged in shape; the milestone adds narrow components at existing seams. The build order is `S0 land → S1 build policy → S2 server bootstrap → S3 migrations/WAL → S4 identity/crypto → S5 limits/payload → S6 guardrails`, with `S8 shell` and `S9 Windows CI/MSIX` as parallel tracks and `S7 test suites` growing alongside rather than after. Two prerequisites make the rest testable: **provider and environment come from configuration** (`Database:Provider`, `Crypto:Provider`) instead of `#if DEBUG`, and `Program` becomes reachable (`public partial class Program`) for `WebApplicationFactory`.

**Major components:**
1. **Hosts** (WASM, Desktop, MCP) — DI composition, transport, platform auth; shape unchanged.
2. **`AdsContext` + `AdsShellState`/`AdsSelectionStore`** — singleton canvas state, scoped per-circuit shell state and one selection cursor; shell state must not be added to `AdsHostFeatures`.
3. **`AdsCommandRegistry` in Core** — owner-keyed registration/removal; `TopMenu` disposes and unregisters.
4. **`AdsDesignPayload` in Core** — version, byte cap, canonical parse, shared reason codes; called by `TopMenu.SaveDiagram` and `DesignService.Save/Load`.
5. **Server `Auth/`** — `AdsIdentity.TryFrom`, `AdsPolicies` (fallback + app roles), `AdsConcurrencyInterceptor`; `Persistence/DatabaseBootstrapper` and design-time factories live beside it.
6. **`WorkspaceDb` / `AdsWorkspace`** — one options/pragma factory, migrations, WAL, `quick_check`, and a schema-version handshake for the read-only MCP window.

**Key patterns:** baseline-as-guarded-SQL (migration 1 preserves the existing DDL verbatim); two-layer limits (transport ceiling vs domain contract); scoped stores beside the singleton canvas; config-selected providers; explicit MCP open-mode fallback instead of retry loops.

### Critical Pitfalls

1. **MSIX identity is decided in three disagreeing places** — csproj (`ApplicationId=com.azuredesignstudio.desktop`, `WindowsPackageType=None`), manifest (`Identity Name="AzureDesignStudio.Desktop" Publisher="CN=Azure Design Studio"`), and workflow (unanchored version regex that also rewrites `MinVersion`). Prevention: one publisher authority (certificate subject, verbatim), one name authority, version from nbgv through XML-node edit, assert in CI; remember `0x8007000B` is publisher mismatch and `0x800B0109` is untrusted root, and trust is a `LocalMachine\TrustedPeople` property.
2. **`EnsureCreated` + migrations without history** — five call sites, two on the RPC hot path; a database created by `EnsureCreated` can never be migrated, and a later `Migrate()` fails on existing tables. Prevention: delete per-RPC `EnsureCreated` first, baseline both providers, never re-create a user database, back up (`VACUUM INTO`) before migrating.
3. **The baseline fails on data the old schema allowed** — the model declares unique indexes the shipped DDL never created (`ChangeFeed(TargetResourceId, ChangeKey)`, `Designs.Name`), and SQL Server's `ISJSON` check may be blocked by pre-constraint rows. Prevention: diff a real file against the model, dedupe/report before creating constraints, refuse to migrate on violations with a visible message.
4. **WAL × read-only MCP × `Cache=Shared`** — `SQLITE_LOCKED` is not retried by `busy_timeout`, a WAL database cannot be opened read-only without a writable `-shm`, and copying `workspace.db` without `-wal` loses committed rows. Prevention: remove `Cache=Shared` everywhere, apply pragmas per connection, explicit fallback (`ReadOnly` → `ReadWrite` + `PRAGMA query_only=ON`) with the mode reported, and a cold-workspace MCP test.
5. **gRPC limits/deadlines have client halves** — client `MaxReceiveMessageSize` also defaults to 4 MB, so a raised server cap still fails on download; a global deadline kills the server-streaming deploy; browser server-streaming requires `GrpcWebMode.GrpcWebText`; a rate-limit 429 surfaces as `Unavailable`, not `ResourceExhausted`. Prevention: one shared limit constant on both ends, per-method (not global) deadlines, `GrpcWebText` for the Deploy client, and `OnRejected` writing `grpc-status: 8` (timeouts `grpc-status: 4`).
6. **Identity normalization orphans every saved design** — designs, subscriptions, and quota are keyed on the legacy URI-claim GUID; switching to `sub` silently yields zero designs, and flipping `MapInboundClaims` breaks the lookup. App roles are not retroactive and group assignment may never emit `roles`; the service worker caches `appsettings.json`, so scope changes are sticky. Prevention: `oid` only (both shapes), never `sub`, in-band 401 (not `RpcException`, which triggers deferred-save retry), SW exclusion for `appsettings*.json`.
7. **Test projects added but never run, or booting in Release** — `build.yml` builds an explicit project list and runs `--no-build`; Release compiles out `DEBUG`, so the non-debug path demands `KeyVaultName` and constructs `SecretClient` before any test body. Prevention: add projects to both lists, refactor provider selection before the first integration test, assert per-project test counts in CI.
8. **AutoMapper removal is a generator change plus a trimming risk** — the profile is Scriban-generated, one caller (`DataModelFactory.GetNodesDto`) dispatches by runtime `Type` via a JSON round-trip, and CI never publishes trimmed. Prevention: template first, goldens unchanged, generated type dispatcher including `UnknownNodeModel`, and a real trimmed-publish boot job before claiming AOT parity.

Two secondary traps belong in planning, not discovery: a non-blocking Windows job becomes permanent unless duration is recorded and a two-green-run rule is enforced; AntDesign 1.6.2 ships four ~690 KB **standalone** stylesheets, so the theme axis must be one documented choice (complete-theme swap or verified variable layer), never a hybrid.

## Decisions the Roadmap Must Make

These are not open research questions; they are choices the roadmap must record before planning the affected phase. Where the four files disagreed, the resolution is stated rather than averaged.

### 1. Signing path and publisher identity
**Recommendation:** fix the publisher legal name first, then choose **Azure Artifact Signing** if eligibility holds (orgs USA/CA/EU/UK; individual developers USA/Canada only), else an **OV code-signing certificate**; self-signed PFX + `TrustedPeople` only for a controlled pilot if both are unavailable. The manifest `Publisher` is the certificate's subject string verbatim, and `Identity Name` never changes after the first install. Tagged releases must **fail** when signing material is absent; only `workflow_dispatch` may request unsigned. Decision order is publisher → signing path → manifest → signing step → clean-VM install, because inverting it only moves the failure.

### 2. Migration baseline shape
**Recommendation:** adopt ARCHITECTURE's **baseline-as-guarded-SQL** for both providers: migration 1 is the existing guarded DDL expressed as `migrationBuilder.Sql`, so legacy databases no-op and receive the history row while fresh databases get the real schema; migrations 2+ are generated diffs. Reject STACK's "generate `InitialCreate`, then insert the baseline row" as the primary path because it requires per-database history surgery and silently skips columns that older files lack; keep it only as a documented recovery procedure. Add PITFALLS' preflight: dedupe/report violating rows before creating unique indexes, repair the two SQLite `Scans` columns only when `__EFMigrationsHistory` is absent, and back up before migrating.

### 3. CSP approach
**Recommendation:** **hash-based CSP generated from one checked-in source**, served as a response header by the server and as a `<meta http-equiv>` tag in `Desktop/wwwroot/app.html`. Import maps cannot be external files and the host pages are static, so a nonce has nowhere to live; `script-src 'self' 'wasm-unsafe-eval'` plus SHA-256 hashes for the import map, the App Insights snippet, and the desktop theme bootstrap. `style-src 'unsafe-inline'` is required by Z.Blazor.Diagrams and Ant Design inline styles; `frame-ancestors` is header-only. Toolbelt's ImportMap extension 10.0.13 may supply the hashing step, but it is third-party (MEDIUM confidence) — the roadmap should accept a small build script as an equivalent. Report-only is allowed only with an expiry date.

### 4. Test project topology
**Recommendation:** four projects, three new, all on xUnit v2: `Core.Tests` (unchanged, framework-free logic + SQLite migration tests), `Server.Tests` (`WebApplicationFactory` + test auth handler + `Grpc.Net.Client` over `TestServer`), **`Components.Tests`** (bUnit; this resolves STACK's `Components.Tests` vs ARCHITECTURE's `UI.Tests` in favor of the name that matches PROJECT.md's "component suites"), and `E2E.Tests` (WASM Playwright every PR; WebView2 CDP nightly/Windows). `Components.Tests` must reference `src/AzureDesignStudio` (the shell components live there), not Core alone. Default server tests to **SQLite temp files** with `ClearAllPools()`, use InMemory only for pure service behavior, and run SQL Server migration parity in a separate container job (`Category=SqlServer`).

### 5. Identity and compatibility window
**Recommendation:** `oid` is the only canonical user key; accept the mapped `oid` and the legacy `.../objectidentifier` URI claim through one `AdsIdentity.TryFrom`, `Guid.TryParse`, and return **in-band 401** for missing/non-GUID claims. Never accept `sub` as a substitute; never flip `MapInboundClaims`; pin it with a comment. Keep the legacy claim shape accepted for the whole of v3 (it is a pure function, cheap to retain) and end the window only after a telemetry check shows no legacy-only tokens. For guardrails, the legacy `Id=1` row with `TenantId = NULL` stays as the inherited default for tenants that never wrote a policy — that is the compatibility contract, not a migration to delete. For roles, require assignment on the API registration, prefer direct assignment over group assignment, and exclude `appsettings*.json` from service-worker precache before any scope or client-id change.

### Contradictions resolved (recorded so they are not re-litigated)

| Topic | Files disagreed | Resolution |
|---|---|---|
| SDK pin | STACK: 10.0.400 / `latestFeature`; ARCH + FEATURES: 10.0.401 / `latestPatch` | **10.0.401 / `latestPatch`** — matches the runner inventory and prevents silent feature-band drift |
| Packaging default | STACK: keep `WindowsPackageType=None`; PITFALLS: set `Package` in csproj on Release | **Keep `None` as the default**; the packaging job passes `-p:WindowsPackageType=Package`. Release-default packaging would break the unpackaged Desktop E2E path and the dev loop |
| `MSIX` vs `Package` property value | STACK: `Package` (`MSIX` legacy alias); PITFALLS/workflow: `MSIX` | **`Package`** in new automation; keep the alias only where an existing workflow needs it |
| Rate limiter placement | STACK/ARCH: after authN, before authZ; PITFALLS pipeline sketch: after authZ | **After `UseAuthentication`, before `UseAuthorization`** — the partition key needs `oid`, and endpoint policies need `UseRouting` ahead of both |
| Health vs HTTPS redirect | STACK: keep redirect, forwarded headers first; PITFALLS: exclude `/health` from redirect | **Exclude `/health/*` from redirect and mark anonymous**; keep forwarded headers first so ingress scheme stays correct |
| MCP WAL open mode | STACK: read-only contract; ARCH: `ReadWrite + query_only=ON` fallback; PITFALLS: decide explicitly | **ReadOnly primary; explicit `ReadWrite + query_only=ON` fallback only to materialize `-shm`, reported in the tool result**, plus a cold-workspace test |
| Server test database | STACK: InMemory + Testcontainers for migrations; ARCH/PITFALLS: SQLite temp file; next-phases: "in-memory EF" | **SQLite temp file by default**, InMemory only for pure service logic, SQL Server parity in its own container job |
| `.planning/` tracking | `docs/next-phases-plan.md` P1 says untrack `.planning/`; GSD writes research/roadmap there | **Keep `.planning/` tracked** (PROJECT, codebase, research, roadmap); untrack only `.omo/`, `.swarm/`, and generated scratch. Amend P1's acceptance accordingly |
| Role names | STACK/ARCH: `GuardrailsAdmin`/`Guardrail.Admin`; PITFALLS: `Deploy.Approver` | **Two app roles**: `Guardrail.Admin` for policy mutation and `Deploy.Approver` for approvals; neither implies the other; self-approval remains a separate check |
| Credential model timing | FEATURES: start day one; next-phases: land in P6; ARCH build order omits N9 | **Design/spike during the gate, land behind a dual-run flag in the tail phase** — the migration/revocation tail is the long pole, not the code |

## Implications for Roadmap

Suggested phase structure follows the repo's own P1–P7 authority but re-cuts it so each phase ends in an observable gate. Phases 1–2 are strictly ordered; Phase 6 runs as a parallel track from Phase 2; Phases 3–5 are server-sequential; Phases 7–8 close the gate and ship.

### Phase 1: Land and stabilize
**Rationale:** 120 commits and 54 paths exist on one machine; every later diff is unreviewable until this lands. The offline suite is green now.
**Delivers:** three reviewable commits (core / shell / docs), root `*.dll` removal and ignore rule, `.omo`/`.swarm` untracked (`.planning` retained), stale pointer-zone fix, owner-scoped command registry with `TopMenu.Dispose`, docs truth (11 projects, Entra not B2C), `appsettings*.json` excluded from service-worker precache, theme axis recorded, inline-script inventory for CSP.
**Addresses:** Land current work; palette lifetime; docs drift.
**Avoids:** Pitfall 7's service-worker stickiness and Pitfall 11's theme hybrid.

### Phase 2: Reproducible build and CI baseline
**Rationale:** The signed artifact must be reproducible, and every new package added before centralization bakes in version drift.
**Delivers:** `global.json` (10.0.401/`latestPatch`), `Directory.Build.props`, `Directory.Packages.props`, lock files, non-blocking `windows-latest` Desktop build with cached NuGet and project-scoped workload restore, vulnerable-package gate with one dated AutoMapper allowlist entry, format gate, coverage collection, new test projects wired into both build and test steps.
**Addresses:** Desktop host builds on PRs; build provenance.
**Avoids:** Pitfall 2 (permanent non-blocking job) and Pitfall 8 (projects not built / `--no-build`).

### Phase 3: Server foundation and test harness
**Rationale:** Migrations cannot be generated while DEBUG selects InMemory, and no integration test can boot a `Program` that is `internal` or that demands Key Vault in Release.
**Delivers:** `Database:Provider`/`Crypto:Provider` from configuration, `IsDevelopment()` gating only diagnostics, `public partial class Program`, direct Server→Core reference, design-time context factories, `Server.Tests` skeleton with test auth handler and gRPC-over-TestServer channel, deletion of per-RPC `EnsureCreated`.
**Addresses:** Testability prerequisite for the Trust Gate.
**Avoids:** Pitfall 8's Release boot failure and the `#if DEBUG` infrastructure debt.

### Phase 4: Database durability and migrations
**Rationale:** One schema source must precede the guardrail column; the desktop upgrade is the migration a pilot actually exercises. Migrations and WAL land in the same change because `quick_check` and backup are the safety net for a failed migration.
**Delivers:** baseline migrations for SQLite and SQL Server, data-repair preflight, removal of ad-hoc DDL and all `EnsureCreated`, `dotnet-ef` 10.0.13, WAL/`busy_timeout`/`foreign_keys`/`synchronous` interceptor, `quick_check`, `VACUUM INTO` backup + restore drill, MCP schema handshake and open-mode fallback, checked-in legacy workspace and SQL Server schema fixtures, parity test.
**Addresses:** Both databases use migrations; WAL with backup/restore.
**Avoids:** Pitfalls 3, 4, and 5 — the milestone's highest data-loss paths.

### Phase 5: Trust Gate hardening
**Rationale:** Identity is the partition key for limits and the tenant key for guardrails, so it lands first within the phase; payload caps must sit under the transport cap created in the same phase.
**Delivers:** `AdsIdentity`, local crypto provider, fallback authorization policy plus explicit anonymous list, rate limiter with gRPC-shaped rejection, per-method timeouts, 16 MB transport / 4 MB design caps shared with clients, concurrency interceptor, health endpoints, `GrpcWebText` for Deploy, `AdsDesignPayload` on both sides, tenant-scoped `Guardrail.Admin` policy and `Deploy.Approver` approvals, unauthenticated sweep test.
**Addresses:** Typed identity, DEBUG offline, limits/health, payload contract, guardrail authorization.
**Avoids:** Pitfalls 6, 7, and 12 (client halves, identity orphaning, authorization defaults).

### Phase 6: Round 2 shell completion (parallel track)
**Rationale:** The slice is real, working code and the two lifetime bugs are in the surface being demoed. No **new** surfaces may start before the gate, but completing what already exists does not violate the freeze.
**Delivers:** `AdsShellState` + `AdsSelectionStore`, registry moved to Core, three-zone contract with persisted widths and keyboard resize (existing stencil rail/inspector populate it), palette keyboard contract, theme tokens in both HTML entries with first-paint assertion, status-bar state honesty, InfoTips doc registry + link CI, axe gate and keyboard equivalents for drag, bUnit coverage, screenshots in `docs/ui/`.
**Addresses:** Round 2 shell slice (palette, inspector, status bar, theme, three-zone layout).
**Avoids:** Pitfall 11 (theme/a11y). Forest and cost rail are explicitly **not** in this phase — they need a paged read path and a local cost engine.

### Phase 7: Verification depth and Trust Gate tail
**Rationale:** The gate's exit criteria are evidence-based; CSP, AutoMapper removal, and the credential model are the items that need a tested base underneath them.
**Delivers:** committed E2E (WASM every PR, WebView2 nightly/Windows), server/component suites in CI, new-code coverage gate, MCP contract snapshot, AutoMapper removal via generator with runtime-type dispatcher and goldens unchanged, real trimmed-publish boot job, CSP + headers + SBOM, fail-closed release signing, N9 credential model behind a dual-run flag with revocation/audit, blocking Windows job, full build policy, ADRs/runbooks/docs index.
**Addresses:** Server integration, component and E2E suites; CSP; AutoMapper exit; credential model.
**Avoids:** Pitfalls 8, 9, 10, and the "looks done but isn't" checklist.

### Phase 8: Signed MSIX and clean-machine install
**Rationale:** Packaging identity must be settled before the pilot build; the packaged data root changes, so the workspace import and MCP dual-path probe must ship with the first packaged build.
**Delivers:** real `Publisher` and single `Identity Name`, nbgv version stamped through XML nodes and validated as `^\d+\.\d+\.\d+\.0$`, `WindowsPackageType=Package` + `RuntimeIdentifierOverride=win-x64` + self-contained, `AppxPackageSigningEnabled=false` then post-publish signing, `signtool verify`, SHA-256 manifest + SBOM beside the `.msix`, clean-VM install/launch, packaged workspace import prompt, MCP discovery probing both roots, uninstall/upgrade test.
**Addresses:** Signed MSIX installed on a clean Windows machine.
**Avoids:** Pitfall 1 end to end.

### Milestone outcomes → phases

| Outcome (PROJECT.md) | Phases | Exit evidence |
|---|---|---|
| Land current work | 1–2 | Clean tree, pushed, bundle; blobs and scratch untracked; reviewable chunks |
| Trust Gate | 3–5, 7 | Identity 401 not 500; DEBUG offline; limits/health live; guardrails scoped; both DBs migrate; CSP + vuln gate; AutoMapper gone; suites green |
| Round 2 shell | 6 (parallel), verified before 8 | Zone contract, palette keyboard, theme in both hosts, axe gate, component tests |
| Signed MSIX | 2, 7, 8 | Publisher matches cert; verified signature; clean-VM install; workspace continuity |

### Phase Ordering Rationale

- **Landing precedes everything** because the current tree is the only reviewable baseline, and the two lifetime bugs sit in the surface about to be demoed.
- **Build policy precedes migrations and tests** because four packages and one new project would otherwise be added before centralization; and a reproducible SDK is a precondition for a reproducible package.
- **Server foundation precedes migrations**, and migrations precede the guardrail tenant column: otherwise the milestone writes a fourth hand-maintained DDL statement.
- **Identity precedes limits and guardrails** because the limiter partitions by `oid` and policies need `tid`; splitting them produces code that compiles and proves nothing.
- **Tests grow with the work, not after it**: the server skeleton exists before the security code so `Program`'s testability is designed in; E2E lands before the a11y gate because axe needs a booted app.
- **Round 2 runs in parallel** because it has no server dependency; the surface freeze keeps it from pulling effort off the gate.
- **MSIX is last but its identity decision is first**, because certificate procurement and the publisher legal name are the only genuinely external dependencies.

### Research Flags

Phases likely needing deeper research during planning:
- **Phase 4:** SQL Server legacy-schema parity — no checked-in legacy fixture exists, and SQLite's table-rebuild and `__EFMigrationsLock` behavior need a recovery drill.
- **Phase 5:** CSP import-map hash pipeline (dotnet/runtime#121885; third-party Toolbelt option) and the gRPC rejection/timeout trailer contract — spec-consistent but not an official recipe, so pin it with integration tests.
- **Phase 7:** AutoMapper-to-generator runtime dispatch plus trimmed-publish boot; N9 credential model shape and dual-run/revocation plan.
- **Phase 8:** Artifact Signing eligibility and certificate procurement; packaged data-root behavior needs a spike (currently `[INFERENCE]`).

Phases with standard patterns (skip `--research-phase`):
- **Phase 1:** repo hygiene and component lifetime fixes.
- **Phase 2:** `global.json`, CPM, GitHub Actions caching.
- **Phase 3:** `WebApplicationFactory` and gRPC-over-TestServer wiring are documented.
- **Phase 6:** WCAG 2.2, APG tree view, and bUnit patterns are well documented; the open question is effort, not approach.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | Versions verified against package metadata and Microsoft primary docs; MEDIUM on the gRPC rejection contract, the CSP hash workaround, and the xunit-v2 risk call |
| Features | HIGH for the P1 list; MEDIUM for effort | Trust/delivery rows rest on Learn/W3C plus repo citations; S/M/L sizing is the repo's own scale, unmeasured |
| Architecture | HIGH for seams and ordering; MEDIUM for SQL Server baseline | Repo read at `a05ef8c` plus working tree; the SQL Server legacy schema has never been diffed against the model |
| Pitfalls | HIGH on platform mechanics; MEDIUM on CI timing and signing in practice | Each pitfall cites a primary doc or a code location; nothing was validated by running a build, migration, or install |

**Overall confidence:** HIGH on *what* to build and in *what order*; MEDIUM on *how long* and on signing eligibility.

### Gaps to Address

- **Desktop Release build duration is unmeasured** (11+ minutes observed with a warm cache and no result). Record it in Phase 2; do not set a blocking gate or a budget before that.
- **Artifact Signing eligibility and the publisher legal name are unknown.** Both are external dependencies; start them in Phase 1, not ship week.
- **Packaged data root is inferred, not verified.** Spike before Phase 8; ship the import prompt and MCP dual-path probe with the first packaged build regardless.
- **No SQL Server legacy schema fixture exists.** Build it from the current bootstrap DDL, then prove parity before migration 2.
- **What-if token gate is in-process with two replicas.** Decide explicitly: pin `replicas: 1` for the pilot or move the gate to the database. Leaving it implicit means roughly half of deploys fail.
- **`__EFMigrationsLock` recovery is untested.** Document and rehearse it; a killed migration otherwise hangs every later one.
- **bUnit against the WASM project on Linux CI is unproven.** Prove the first project builds without dragging static-web-asset work into test output.
- **gRPC-Web `grpc-timeout` propagation is unverified.** One integration test before claiming deadlines are live.
- **AntDesign theming axis is undecided in code.** Choose one documented approach and assert first paint; do not ship a hybrid.
- **Forest and cost-rail performance are unmeasured.** Keep them post-gate; the paged read path is a prerequisite, not an optimization.

## Sources

### Primary (HIGH confidence)
- **GitHub Advisory GHSA-rvv3-g6hj-g44x / CVE-2026-32933** — AutoMapper denial of service; patched 15.1.1/16.1.1: https://github.com/advisories/GHSA-rvv3-g6hj-g44x
- **EF Core 10 breaking changes** (SQLite timestamps, SqlClient broker dependency): https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes
- **EF Core migrations** (applying, bundles vs scripts, SQLite has no idempotent scripts, migration locking): https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying
- **EF Core SQLite limitations** (table rebuilds, `__EFMigrationsLock`): https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations
- **EF Core `EnsureCreated`** ("EnsureCreated and Migrations don't work well together"): https://learn.microsoft.com/en-us/ef/core/managing-schemas/ensure-created
- **Microsoft.Data.Sqlite connection strings** (shared cache vs WAL, `Default Timeout` is a command timeout): https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings
- **SQLite WAL** (read-only rules, checkpointing): https://sqlite.org/wal.html; **shared-cache mode**: https://sqlite.org/sharedcache.html
- **gRPC for .NET configuration, deadlines, gRPC-Web**: https://learn.microsoft.com/en-us/aspnet/core/grpc/configuration; https://learn.microsoft.com/en-us/aspnet/core/grpc/deadlines-cancellation; https://learn.microsoft.com/en-us/aspnet/core/grpc/grpcweb
- **Rate limiting and request timeouts middleware**: https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit; https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts
- **Health checks** and **gRPC health checks**: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks; https://learn.microsoft.com/en-us/aspnet/core/grpc/health-checks
- **Blazor CSP** (`wasm-unsafe-eval`, hashes, header-only directives): https://learn.microsoft.com/en-us/aspnet/core/blazor/security/content-security-policy
- **.NET 10 import map vs strict CSP** (open issue): https://github.com/dotnet/runtime/issues/121885
- **MSIX signing guide / troubleshooting / app package requirements**: https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide; https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide; https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
- **Publish a packaged MAUI Windows app** (`WindowsPackageType`, `RuntimeIdentifierOverride`): https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli
- **Entra app roles** (`roles` claim, group-assignment gap): https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-app-roles-in-apps
- **`dotnet package list --vulnerable`**: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list
- **Playwright CI and WebView2**: https://playwright.dev/docs/ci; https://playwright.dev/dotnet/docs/webview2
- **bUnit project setup / JSInterop emulation**: https://bunit.dev/docs/getting-started/create-test-project.html; https://bunit.dev/docs/test-doubles/emulating-ijsruntime.html
- **WCAG 2.2** and **APG tree view**: https://www.w3.org/WAI/standards-guidelines/wcag/new-in-22/; https://www.w3.org/WAI/ARIA/apg/patterns/treeview/
- **.NET 10 runner image inventory** (SDK 10.0.401, Windows SDK 10.0.26100, MAUI 9 workloads): https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md

### Secondary (MEDIUM confidence)
- **actions/runner-images #6936** — MAUI workloads are not pre-installed; 2–8+ minute installs: https://github.com/actions/runner-images/issues/6936
- **ant-design-blazor #2641** — theme switching is a stylesheet swap / CSS variables: https://github.com/ant-design-blazor/ant-design-blazor/issues/2641
- **gRPC rate limiting behavior on .NET clients** (503 → `Unavailable`): https://blog.ladeak.net/posts/aspnetcore-grpc-ratelimiting
- **Toolbelt import-map CSP extension 10.0.13**: https://www.nuget.org/packages/Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension
- **MAUI #18571** — `ApplicationDisplayVersion` does not flow into the Windows manifest: https://github.com/dotnet/maui/issues/18571
- **Azure Quick Review (`azqr`) README** — competitor breadth: https://github.com/Azure/azqr

### Repository evidence
- **Research inputs:** `.planning/research/STACK.md`, `.planning/research/FEATURES.md`, `.planning/research/ARCHITECTURE.md`, `.planning/research/PITFALLS.md`
- **Planning context:** `.planning/PROJECT.md`, `.planning/codebase/{STACK,TESTING,CONCERNS}.md`
- **Authority docs:** `docs/v3-plan.md` (N1–N50, Phase 0 exit gate), `docs/next-phases-plan.md` (P1–P7, gates), `docs/top-100-ui-improvements.md` (U1–U100), `docs/assessment-2026-10-21.md` (F1–F7)
- **Code anchors:** `src/AzureDesignStudio.Server/Program.cs`, `Services/{DesignService,DeployService,ServiceTools}.cs`, `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs`, `src/AzureDesignStudio.Mcp/AdsWorkspace.cs`, `src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio.Desktop/MauiProgram.cs`, `Platforms/Windows/Package.appxmanifest`, `src/AzureDesignStudio.Core/DataModelFactory.cs`, `.github/workflows/{build,publish}.yml`

---
*Research completed: 2026-10-21*
*Ready for roadmap: yes*
