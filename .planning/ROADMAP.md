# Roadmap: Azure Design Studio

## Overview

This is a brownfield trust-and-delivery milestone, not a feature milestone. Eleven projects already ship a visual Azure editor, tenant scans, drift, cost, what-if-gated apply, and a read-only MCP surface; what is missing is the evidence that any of it can be trusted, upgraded, and installed by someone else. The order is forced by the work: land the 120 local commits and 54 working-tree paths before anything else, make the build reproducible, make the server bootable by a test, migrate both databases without losing a row, then close the identity, guardrail, and bounds gaps at the seats the architecture already has. The Round 2 shell runs as a parallel track from the build phase because it has no server dependency, and the signed MSIX is last because its publisher identity is the only genuinely external decision — so that decision is made in Phase 1.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [ ] **Phase 1: Land and Stabilize** - Commit the working slice in reviewable chunks, remove tracked blobs and scratch, fix the stale pointer zone, correct docs, decide the publisher
- [ ] **Phase 2: Reproducible Build and CI Baseline** - Pin the SDK, centralize and lock packages, gate Desktop on PRs, block vulnerable packages
- [ ] **Phase 3: Server Foundation and Integration Harness** - Config-selected providers, a reachable `Program`, and a real hosted-server test channel
- [ ] **Phase 4: Database Durability** - Baseline migrations, WAL, integrity check, backup/restore, and read-only MCP over one workspace
- [ ] **Phase 5: Identity, Guardrails, and Offline Development** - Typed `oid`/`tid`, deny-by-default RPCs, tenant-scoped roles, and a DEV crypto provider
- [ ] **Phase 6: Bounds, Payload Contract, and Health** - Per-user limits, calibrated deadlines, shared payload caps, and anonymous health endpoints
- [ ] **Phase 7: Round 2 Shell Completion** - Zone contract, palette lifetime and keyboard path, honest status, one theme axis, link and a11y gates (parallel track)
- [ ] **Phase 8: Verification Depth and Diagnostics** - Server, component, and E2E suites in CI, coverage and flake budgets, stable error codes, bounded logs, support bundle
- [ ] **Phase 9: Credential Model Migration** - Remove replayable subscription secrets, migrate behind a dual-run flag, move ARM calls to the desktop broker
- [ ] **Phase 10: Post-Gate Capabilities** - Paged 100k inventory, local cost rail, offline linter, hash-chained audit, telemetry privacy contract
- [ ] **Phase 11: Signed MSIX and Packaged Install** - Fail-closed signing, identity assertions, verified clean-machine install, workspace continuity

## Phase Details

### Phase 1: Land and Stabilize

**Goal**: The milestone's starting point stops living on one machine: the working slice lands in reviewable commits, tracked artifacts stop lying, the stale pointer zone is fixed, and the publisher identity is decided before any packaging work begins.
**Depends on**: Nothing (first phase)
**Requirements**: None — enabling gate for BUILD-01 and the PROJECT.md Active item "Current uncommitted shell/domain/docs slice is committed in reviewable chunks and pushed". No v1 requirement is delivered here; this phase protects every later diff.
**Success Criteria** (what must be TRUE):
  1. A fresh clone contains exactly the tracked source: no root `*.dll` blobs, no `.omo/`/`.swarm/` entries, `.planning/` still tracked, and every cited docs/research file committed.
  2. `git status` is clean, `origin/main` points at the local head, and an out-of-band `git bundle` restores that same head.
  3. The land arrives as three reviewable commits (core / shell / docs) and each one restores, builds, and keeps the 755-case offline suite green.
  4. Dropping a stencil by pointer still reaches the canvas after the drawer remounts, with no console error.
  5. README and AGENTS report 11 projects and Entra ID work+personal auth (not B2C), and the publisher legal name plus signing path (Azure Artifact Signing, OV certificate, or controlled-pilot PFX) is recorded in PROJECT.md Key Decisions.

**Plans**: 1/8 plans executed
**Wave 1**
- [x] 01-01-PLAN.md — Core topic: domain slice plus its tests, `.gitignore` hygiene, five tracked artefacts removed, proved in a worktree at 753

**Wave 2** *(blocked on Wave 1 completion)*
- [ ] 01-02-PLAN.md — Remount-safe pointer zone with a throwaway headless harness BEFORE/AFTER transcript

**Wave 3** *(blocked on Wave 2 completion)*
- [ ] 01-03-PLAN.md — Shell topic: shell components, stencil catalog, restored fallback icon, Desktop host, proved at 755

**Wave 4** *(blocked on Wave 3 completion)*
- [ ] 01-04-PLAN.md — Docs truth: counts, auth, architecture tree, historical banners, publisher decision recorded

**Wave 5** *(blocked on Wave 4 completion)*
- [ ] 01-05-PLAN.md — Docs topic commit with worktree proof and the SC#5 audit

**Wave 6** *(blocked on Wave 5 completion)*
- [ ] 01-06-PLAN.md — Two pushes (pre-land tip by refspec, then head), the recovery bundle, and the recorded `BUNDLE_SHA`

**Wave 7** *(blocked on Wave 6 completion)*
- [ ] 01-07-PLAN.md — Fresh-clone audit against `BUNDLE_SHA`, final suite run, SC#4 human verification and phase evidence

**Wave 8** *(blocked on Wave 7 completion)*
- [ ] 01-08-PLAN.md — Convergence after the last metadata commit: closing push, bundle re-cut at `CONVERGE_SHA`, gate command, bundle deletion

**Implementation note**: `src/AzureDesignStudio/wwwroot/js/ads.js` — move the init guard onto the zone element (`zone.__adsPointerInit`) and re-resolve the zone inside `inZone`/overlay updates instead of closing over the first node. Of the two known lifetime defects, only the pointer zone is fixed here; the command registry's owner lifetime is SHELL-02 in Phase 7. Untrack `.omo/`/`.swarm/` but retain `.planning/`; ignore only generated scratch and root `*.dll`.

### Phase 2: Reproducible Build and CI Baseline

**Goal**: A clean clone builds from pinned, locked, centralized inputs, and the Windows desktop host fails a pull request before merge instead of failing at tag time.
**Depends on**: Phase 1
**Requirements**: BUILD-01, BUILD-02, BUILD-03, BUILD-04, BUILD-05
**Success Criteria** (what must be TRUE):
  1. A clean clone with no warm caches restores and builds every project with the SDK pinned at 10.0.401 (`latestPatch`) and locked package versions; restore fails if a lock file would change.
  2. A pull request that introduces a deliberate compile error in `MauiProgram.cs` fails the Windows Desktop job, and the job's wall-clock duration is recorded.
  3. CI fails on any High/Critical vulnerable package, and the only allowlist entry is AutoMapper with a dated expiry.
  4. Package versions appear only in `Directory.Packages.props`, app projects build with warnings as errors, and a changed file that violates formatting fails the check.
  5. After two consecutive green non-blocking Windows runs, the Desktop job becomes blocking and every subsequent merge requires it.

**Plans**: TBD
**Parallel track**: Phase 7 shell completion may start as soon as Phase 1 lands; it has no server dependency, but no new UI surface may start before Phase 9.
**Implementation note**: `global.json` (10.0.401/`latestPatch`), `Directory.Build.props`, `Directory.Packages.props`, `packages.lock.json`, `.editorconfig`. In `.github/workflows/build.yml` add a `windows-latest` job with NuGet cache and `dotnet workload restore` (runner images ship MAUI 9 workloads), keep the Linux job as the fast gate, and use `dotnet format --verify-changed`. Keep `WindowsPackageType=None` as the csproj default — packaging in Phase 11 passes `-p:WindowsPackageType=Package`. Do not add MSIX packing until the duration is known.

### Phase 3: Server Foundation and Integration Harness

**Goal**: The server can be booted by a test, configured without Azure, and every RPC exercised in CI without credentials or a developer's database.
**Depends on**: Phase 2
**Requirements**: TEST-01
**Success Criteria** (what must be TRUE):
  1. A server integration test boots the real host in CI with no Key Vault, no Azure credentials, and no `#if DEBUG` deciding its providers.
  2. A gRPC channel over `TestServer` reaches every service through a test authentication handler, and a failure surfaces the RPC status instead of a host crash.
  3. A test that creates a design writes to a temporary SQLite file; no test touches a developer's `workspace.db` or a live tenant.
  4. `Program` is reachable from the test project, and the RPC hot path no longer calls `EnsureCreated`.

**Plans**: TBD
**Implementation note**: `src/AzureDesignStudio.Server/Program.cs` (`public partial class Program`), `Database:Provider`/`Crypto:Provider` read from configuration, `IsDevelopment()` gating diagnostics only, direct Server→Core reference, design-time context factories beside `Persistence/DatabaseBootstrapper`. New `AzureDesignStudio.Server.Tests` on `Microsoft.AspNetCore.Mvc.Testing` 10.0.13 with a test auth handler; SQLite temp files with `ClearAllPools()` by default, InMemory only for pure service logic.

### Phase 4: Database Durability

**Goal**: Both databases get one schema authority: existing installations upgrade without losing a row, SQLite survives concurrent read/write, corruption is visible, and a user can restore a real backup.
**Depends on**: Phase 3
**Requirements**: DATA-01, DATA-02, DATA-03, DATA-04, DATA-05, DATA-06, DATA-08
**Success Criteria** (what must be TRUE):
  1. An existing `workspace.db` upgrades through EF migrations with designs, scans, and plans intact and row counts unchanged, and a fresh database and an upgraded database produce the same schema.
  2. The server database upgrades through EF migrations and a rehearsed, documented rollback restores the prior state.
  3. A concurrent scan write and MCP read complete without `SQLITE_BUSY` or `SQLITE_LOCKED`, with WAL, `busy_timeout`, `foreign_keys`, and per-connection pragmas in force.
  4. Startup `quick_check` surfaces a deliberately corrupted workspace instead of opening it, and a user can take a `VACUUM INTO` backup and restore it with data verified.
  5. MCP opens a WAL workspace read-only (or reports the explicit `ReadWrite + query_only` fallback it used) and passes a cold-workspace test; the baseline adopts existing DDL, reports or dedupes rows violating new unique indexes, and documents `__EFMigrationsLock` recovery.

**Plans**: TBD
**Implementation note**: `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs` and the `WorkspaceDb` options/pragma factory; `src/AzureDesignStudio.Mcp/AdsWorkspace.cs`. Baseline-as-guarded-SQL: migration 1 is the existing guarded DDL via `migrationBuilder.Sql` for both providers so legacy databases receive a history row without re-creation; migrations 2+ are generated diffs. `dotnet-ef` 10.0.13, no `Cache=Shared`, `VACUUM INTO` backup, SQL Server parity in its own container job, checked-in legacy fixtures built from today's bootstrap DDL. Back up before migrating; never re-create a user database.

### Phase 5: Identity, Guardrails, and Offline Development

**Goal**: Every request carries a trustworthy user and tenant, guardrail policy and approvals can only be changed by the right role inside the right tenant, and a developer can work offline in DEBUG without weakening production.
**Depends on**: Phase 4
**Requirements**: IDA-01, IDA-02, IDA-03, IDA-04, IDA-05, IDA-06, CRED-01, CRED-02
**Success Criteria** (what must be TRUE):
  1. A token with a missing `oid`/`tid` or a non-GUID claim receives a typed 401 in-band; no request path throws, and no deferred-save retry loop starts.
  2. A user whose token carries the legacy `.../objectidentifier` URI claim still reads their existing designs, subscriptions, and quota.
  3. A reflection-based test enumerates every gRPC method and fails when any method is callable without authentication unless it is on the explicit anonymous list.
  4. A caller without `Guardrail.Admin` receives 403 for guardrail read/mutation, two tenants cannot read or overwrite each other's policies or approvals, and approval requires `Deploy.Approver` with self-approval refused as a separate rule.
  5. A DEBUG developer registers and exercises a subscription with no Key Vault and no Azure credentials, while Production refuses development keys and still requires Key Vault.

**Plans**: TBD
**Implementation note**: new server `Auth/AdsIdentity.TryFrom` (accept mapped `oid` and the legacy URI claim through `Guid.TryParse`; never `sub`; never flip `MapInboundClaims`; in-band 401 rather than `RpcException`). `AdsPolicies` fallback plus `Guardrail.Admin`/`Deploy.Approver` app roles; tenant column on the guardrail and approval rows after Phase 4's migrations, with the legacy `Id=1`/NULL-tenant row retained as the inherited default. `ICryptoService` with a DPAPI/Data-Protection DEBUG implementation selected by `Crypto:Provider`; exclude `appsettings*.json` from service-worker precache so role/scope changes are not sticky.

### Phase 6: Bounds, Payload Contract, and Health

**Goal**: One caller cannot exhaust the server, every client obeys the same payload rules, long deploys survive their own duration, and liveness is observable without authentication.
**Depends on**: Phase 5
**Requirements**: BOUND-01, BOUND-02, BOUND-03, BOUND-04, BOUND-05, BOUND-06, BOUND-07
**Success Criteria** (what must be TRUE):
  1. A caller over the per-user limit receives a typed 429/`ResourceExhausted` that the client classifies as retryable, and a parallel burst is bounded rather than exhausting memory.
  2. A 16 MB request is rejected before persistence, and a design over the shared design cap is rejected with a stable code on both client and server while legacy designs still load.
  3. A server-streaming deploy runs past a single global deadline without being killed, while a short unary method still times out at its calibrated deadline.
  4. `/health/live` and `/health/ready` answer without authentication, without an HTTPS redirect, and without fallback-policy interference.
  5. All clients share the cap constants, the Deploy client uses `GrpcWebText` for server streaming and raises its receive limit, and per-user concurrency is capped.

**Plans**: TBD
**Implementation note**: rate limiter after `UseAuthentication` and before `UseAuthorization` (the partition key needs `oid`), `OnRejected` writing `grpc-status: 8` and timeouts writing `grpc-status: 4`; Kestrel `MaxRequestBodySize` and gRPC `MaxReceiveMessageSize` driven by one shared constant; per-method `RequestTimeouts`, not global. `AdsDesignPayload` in Core (version, byte cap, canonical parse, reason codes) called by `TopMenu.SaveDiagram` and `DesignService.Save/Load`; health endpoints anonymous with the redirect exclusion; `AdsConcurrencyInterceptor`; clients built through `GrpcClientFactory` so auth handlers apply.

### Phase 7: Round 2 Shell Completion

**Goal**: The shell already on disk becomes a contract: zones that persist and can be resized without a mouse, a palette with one owner and a real keyboard path, honest status, one theme axis, docs links that cannot rot, and an accessibility gate.
**Depends on**: Phase 1 for the landed slice; starts in parallel with Phase 2. SHELL-06 and SHELL-07 close once Phase 8's E2E/axe infrastructure exists.
**Requirements**: SHELL-01, SHELL-02, SHELL-03, SHELL-04, SHELL-05, SHELL-06, SHELL-07
**Success Criteria** (what must be TRUE):
  1. The three-zone layout persists widths across restart, collapses on snap, and resizes by keyboard through a named zone contract consumed by the existing stencil rail and inspector.
  2. Ctrl/Cmd+K opens the palette; arrow/Enter/Esc work; focus returns to the invoker; commands registered by `TopMenu` disappear when it disposes and appear exactly once after a remount.
  3. The status bar distinguishes saved-locally, synced, and offline-pending states and shows the scan's as-of time without inferring state from the wrong store.
  4. Both HTML entry points first-paint in the selected theme with no flash using one documented AntDesign axis, and the choice survives reload in WASM and Desktop.
  5. InfoTips render definitions and Learn links from one doc registry with a CI check that fails on any non-200 link; axe reports zero WCAG 2.2 AA violations, drag surfaces have keyboard/single-pointer equivalents, focus is never obscured, targets meet 24×24 CSS px, and UI-touching PRs attach scripted before/after screenshots with a hero set regenerated per release.

**Plans**: TBD
**UI hint**: yes
**Parallel track**: Starts with Phase 2 and must be verified before Phase 11. No new UI surface may start before the gate closes in Phase 9; this phase only completes what already exists.
**Implementation note**: `AdsShellState`/`AdsSelectionStore` scoped per circuit (never on `AdsHostFeatures`); `AdsCommandRegistry` moved to Core with `RegisterRange(owner)`/`RemoveOwner`, and `TopMenu.Dispose` unregisters. Palette/inspector/status-bar/theme components plus `ads-tokens.css` and both HTML entries; theme is one complete-theme swap or one verified variable layer, never a hybrid. Doc registry feeding `InfoTip`; screenshot capture into `docs/ui/`.

### Phase 8: Verification Depth and Diagnostics

**Goal**: The gate is evidenced rather than asserted: server, component, and E2E suites run in CI, coverage and flake budgets are enforced, CSP and the dependency exit land on a tested base, and users get stable error codes, bounded logs, and a secret-free support bundle.
**Depends on**: Phase 5, Phase 6, Phase 7
**Requirements**: TEST-02, TEST-03, TEST-04, TEST-05, TEST-06, DIAG-01, DIAG-02, DIAG-03
**Success Criteria** (what must be TRUE):
  1. Server tests fail when user or tenant isolation, the 10-design quota, malformed names, what-if token single-use, approval ownership, or decommission kind-gating regress.
  2. Component tests cover palette filtering and keyboard navigation, owner-scoped registry removal, inspector selection, and status-bar counts; every new test project is in both the build and test workflow steps, and CI asserts each project's test count.
  3. A committed E2E smoke suite passes from a documented command covering boot, stencil drop, port link, undo/redo, save→reload, offline save, scan browse, error toast, and zero console exceptions; WASM runs every PR and WebView2 runs nightly/Windows.
  4. New-code coverage is collected behind an 80% gate and flaky tests are quarantined by name with an expiry date.
  5. Every user-facing error carries a stable `ADS-####` code mapped to a remediation, logs roll and stay bounded outside `%TEMP%` growth, and one click produces a support bundle with versions, OS, workspace stats, redacted logs, and active config but no secrets.

**Plans**: TBD
**UI hint**: yes
**Implementation note**: expand `AzureDesignStudio.Server.Tests`; add `Components.Tests` (bUnit 2.12.5, referencing `src/AzureDesignStudio`, not Core alone) and `E2E.Tests` (Playwright 1.63.0 for WASM, WebView2 CDP nightly). Coverage merge plus gate; MCP tool-manifest snapshot. Gate-tail engineering verified here: hash-based CSP from one checked-in source (header plus the Desktop `<meta>` tag, `frame-ancestors 'none'`, `object-src 'none'`), AutoMapper replaced through the existing Roslyn/Scriban generator with goldens unchanged and a trimmed-publish boot job, which retires Phase 2's dated allowlist entry.

### Phase 9: Credential Model Migration

**Goal**: The server can no longer recover and replay a subscription secret, existing subscriptions move to the new model behind a dual-run flag with a rollback, and desktop ARM calls use the desktop broker.
**Depends on**: Phase 5, Phase 8
**Requirements**: CRED-03, CRED-04, CRED-05
**Success Criteria** (what must be TRUE):
  1. Neither database holds a subscription client secret the server can recover and replay, and every deploy and scan audit record names the credential type used.
  2. Existing subscriptions migrate behind a dual-run flag with a documented rollback and revocation path, and both credential paths pass the integration suite.
  3. Desktop user-scoped ARM reads and writes go through the desktop token broker instead of a server-stored secret, proven by a test that fails if the broker is bypassed.

**Plans**: TBD
**Implementation note**: replace the `ClientSecretCredential` sites in `src/AzureDesignStudio.Server/Services/DeployService.cs`; route user-scoped work through `AdsTokenBroker`; server-side execution uses managed identity or user delegation, and any unavoidable service principal stores a Key Vault reference rather than a decryptable secret. Migration + revocation + dual-run flag; keep legacy read-only for one release. Long pole of the milestone — start at phase entry.

### Phase 10: Post-Gate Capabilities

**Goal**: On a gate that actually holds, the first depth capabilities land: a 100k-row inventory that pages from SQL, truthful local cost, a deterministic offline linter, a tamper-evident audit chain, and a telemetry privacy contract.
**Depends on**: Phase 4 (paged read path), Phase 8 (evidence suite); sequenced after Phase 9 so the gate closes first.
**Requirements**: POST-01, POST-02, POST-03, POST-04, POST-05
**Success Criteria** (what must be TRUE):
  1. A 100k-row tenant inventory pages from SQL with a stable cursor, and the virtualized tree follows APG semantics while memory stays bounded.
  2. The cost rail recomputes locally within 200 ms on the reference design, every figure carries a provenance badge, and actuals appear only on Desktop.
  3. The architecture linter reports the same deterministic Well-Architected violations in the validation drawer and through one read-only MCP tool.
  4. A verify command detects an altered or deleted audit row, and export reproduces the hash chain.
  5. Telemetry fields are classified and redacted, identifiers are hashed, and desktop diagnostics are opt-in with a persisted setting that survives restart.

**Plans**: TBD
**UI hint**: yes
**Implementation note**: one SQL-level paged query service in `AdsWorkspace` (cursor pagination, stable sort, count-on-demand, hard caps for MCP) consumed by `TenantScanDrawerTemplate`; local cost engine with provenance; linter over `LinkTypeClassifier` plus scan inventory feeding the validation drawer and `AdsMcpTools`; `prevHash` chains on `AuditRecord`, `AdsPlanApplyEvents`, `AdsDecomWindowEvents` with a verify command; telemetry redaction classifier and diagnostics page setting. Forest and cost-rail performance are unmeasured — gate each on a recorded measurement, not on hope.

### Phase 11: Signed MSIX and Packaged Install

**Goal**: A pilot user can install a verifiable, signed build on a clean Windows machine, and the packaged app finds or imports the work they already had.
**Depends on**: Phase 1 (publisher decision), Phase 2 (reproducible build), Phase 7 (shell verified), Phase 8 (E2E and a11y evidence), Phase 9 (supply-chain closure)
**Requirements**: SIGN-01, SIGN-02, SIGN-03, SIGN-04, SIGN-05, DATA-07
**Success Criteria** (what must be TRUE):
  1. A tagged release fails when signing material is absent, and only a manual dispatch can explicitly request an unsigned build.
  2. The MSIX `Publisher` equals the signing certificate subject verbatim, `Identity Name` is stable, CI asserts both, and the package version comes from nbgv as `^\d+\.\d+\.\d+\.0$` without rewriting `MinVersion`.
  3. The produced `win-x64` package verifies with a timestamp, then installs and launches on a clean Windows machine.
  4. The release publishes a CycloneDX SBOM, per-artifact SHA-256 checksums, and a recorded clean-machine install verification.
  5. A packaged install finds or imports an existing unpackaged workspace, MCP discovery reports which install root it used, and uninstall/upgrade leave designs intact.

**Plans**: TBD
**Implementation note**: one publisher authority (`Platforms/Windows/Package.appxmanifest` subject matching the certificate verbatim) and one stable identity name; csproj keeps `WindowsPackageType=None` while the packaging job passes `-p:WindowsPackageType=Package` and `-p:RuntimeIdentifierOverride=win-x64`; `AppxPackageSigningEnabled=false` then post-publish `signtool` or `azure/trusted-signing-action@v0`; version stamped through XML-node editing in `.github/workflows/publish.yml`. Packaged data root changes, so the workspace import prompt and MCP dual-path probe (`src/AzureDesignStudio.Mcp/AdsWorkspace.cs`) ship with the first packaged build. `0x8007000B` means publisher mismatch; `0x800B0109` means untrusted root.

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11.
Phase 7 runs as a parallel track from Phase 2 and must be verified before Phase 11.

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Land and Stabilize | 1/8 | In Progress | - |
| 2. Reproducible Build and CI Baseline | 0/TBD | Not started | - |
| 3. Server Foundation and Integration Harness | 0/TBD | Not started | - |
| 4. Database Durability | 0/TBD | Not started | - |
| 5. Identity, Guardrails, and Offline Development | 0/TBD | Not started | - |
| 6. Bounds, Payload Contract, and Health | 0/TBD | Not started | - |
| 7. Round 2 Shell Completion | 0/TBD | Not started | - |
| 8. Verification Depth and Diagnostics | 0/TBD | Not started | - |
| 9. Credential Model Migration | 0/TBD | Not started | - |
| 10. Post-Gate Capabilities | 0/TBD | Not started | - |
| 11. Signed MSIX and Packaged Install | 0/TBD | Not started | - |

## Coverage

- v1 requirements: 57
- Mapped to phases: 57
- Unmapped: 0
- Duplicated: 0

| Phase | Requirements | Count |
|-------|--------------|-------|
| 1. Land and Stabilize | none (enabler) | 0 |
| 2. Reproducible Build and CI Baseline | BUILD-01…BUILD-05 | 5 |
| 3. Server Foundation and Integration Harness | TEST-01 | 1 |
| 4. Database Durability | DATA-01…DATA-06, DATA-08 | 7 |
| 5. Identity, Guardrails, and Offline Development | IDA-01…IDA-06, CRED-01, CRED-02 | 8 |
| 6. Bounds, Payload Contract, and Health | BOUND-01…BOUND-07 | 7 |
| 7. Round 2 Shell Completion | SHELL-01…SHELL-07 | 7 |
| 8. Verification Depth and Diagnostics | TEST-02…TEST-06, DIAG-01…DIAG-03 | 8 |
| 9. Credential Model Migration | CRED-03, CRED-04, CRED-05 | 3 |
| 10. Post-Gate Capabilities | POST-01…POST-05 | 5 |
| 11. Signed MSIX and Packaged Install | SIGN-01…SIGN-05, DATA-07 | 6 |
