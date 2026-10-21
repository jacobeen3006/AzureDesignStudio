# Azure Design Studio — v3 Proposal: The Next 50 Improvements

> **Date:** 2026-10-20 · **Status:** proposal, not started · **Scope:** delivery safety, security/tenancy, data scale, IaC depth, FinOps automation, agent surface, maintainability.
> **Predecessors:** `docs/v1-plan.md` (complete), `docs/v2-plan.md` (Phases 6–13 substantially shipped), `docs/top-100-ui-improvements.md` (Round 2 UI, in progress). This document is **Round 3** and deliberately does not re-number or duplicate those catalogs.
> **Audit basis:** repository at `a05ef8c` plus the current working tree (Round 2 first slice). Every item below cites the code or configuration that motivates it.

---

## 1. Executive summary

The product now has real depth: tenant scans, topology, drift, exposure, reachability, cost, what-if, deployment stacks, decommission planning, and 13 read-only MCP tools. The **next constraint is not more surfaces**. It is trust:

- The Windows desktop host — 4,102 source lines plus a 2,481-line drawer — is **not built on pull requests**; it only builds on tags/manual release.
- There are **no committed E2E artifacts** despite changelog references to CDP/WebView2 gates, and **no server, component, or contract test project**; all 625 test attributes live in `Core.Tests`.
- The server still stores a **long-lived client secret per subscription** (`AzureSubscriptionModel.ClientSecret`) and exchanges it for ARM tokens.
- The guardrail policy is a **single global row any authenticated caller can replace**.
- There is **no rate limiting, CSP, health endpoint, gRPC message cap, or migration story**.
- The workspace stores **full resource properties per scan with no retention**, and the inventory UI filters in memory then renders `Take(200)`.

**Recommendation:** run a four-week **Trust Gate** before adding any Phase 2 capability. Then build the data foundation, then resume depth. Concretely:

1. **Weeks 1–4 — Trust Gate (N1–N3, N5–N6, N9–N14, N17–N18, N26, N46–N47, N50):** all hosts in CI, E2E committed, server integration tests, credential/tenancy fixes, request limits, migrations, build hygiene.
2. **Weeks 5–8 — Data foundation (N4, N7–N8, N15–N16, N19–N20, N27–N28, N48–N49):** paging, retention, incremental scans, scheduler, design concurrency/offline correctness, drawer decomposition.
3. **Weeks 9–16 — Depth (N21–N25, N29–N40):** server workspace, generic ARM tiles, IaC import, policy/capacity preflight, identity map, FinOps closure.
4. **Weeks 17–20 — Automation (N41–N45):** MCP v2, CLI, GitHub Action, diagram interchange.

At two engineers this is roughly **5–6 months**; solo it is **10–12 months**. The first 28 items are the executable core and fit **8–10 weeks solo**. Do not let Round 2 UI work pull the data-layer spikes forward: U45/U57/U69 depend on N20/N24/N19 respectively.

---

## 2. Audited baseline (what is already true)

| Area | Evidence | Consequence |
|---|---|---|
| Projects | 11 projects under `src/`; `AGENTS.md`/README still say 8 | Docs drift; onboarding cost (N50) |
| Tests | 625 `[Fact]`/`[Theory]` attributes → **755 executed cases, all green offline on 2026-10-20**; `Core.Tests` references only Core, Workspace, Discovery | No Server/Desktop/component coverage (N3, N4) |
| CI | `.github/workflows/build.yml` builds Server, WASM, MCP, Core.Tests on Ubuntu | Desktop is absent from PR gate (N1) |
| E2E | No `*e2e*`, `*smoke*`, `*cdp*`, or `*playwright*` files; changelog describes manual gates | Regressions caught manually (N2) |
| Coverage | `coverlet.collector` referenced; no workflow collects it | No coverage signal (N5) |
| Server auth | `[Authorize]` + `RequiredScope`; `GetUserId` reads the legacy URI claim and `new Guid(...)` | Fragile identity (N11), global policy (N10) |
| Secrets | Client secrets encrypted with Key Vault, decrypted into `ClientSecretCredential` at 7 sites | Long-lived replayable secrets (N9) |
| Middleware | `UseHsts`/`UseHttpsRedirection` only; no CSP, rate limiter, health checks, response compression | Hardening gaps (N13, N14) |
| Databases | `EnsureCreated()` + ad-hoc `ALTER TABLE`/`CREATE TABLE`; no `Migrations/` folder | No upgrade/rollback story (N17) |
| Workspace | SQLite shared cache, no WAL/busy timeout; full `PropertiesJson` per resource per scan; no retention | Lock contention and growth (N18, N19) |
| Inventory UI | `Filtered().Take(200)`; `Filtered().ToList()` for bulk actions; MCP loads all snapshots | 100k rows is not a UI concern yet (N20) |
| Design sync | `DesignService.Save` accepts any string; no revision; `FlushPendingAsync` stops at first failure; local pending wins | Lost updates and resurrection (N26–N28) |
| Identity model | `MsalAccountService` uses `GetAccountsAsync().FirstOrDefault()`; `ScanRecord` has no tenant id | Single-tenant in practice (N22) |
| Scan engine | `ScanEngine` always runs the full ARG query per subscription group | Full cost every scan (N23) |
| Scheduling | No `BackgroundService`/`PeriodicTimer` outside tests | Manual scans only (N24) |
| IaC | `.ads.json` import; ARM→Bicep decompile for display; no ARM/Terraform/Bicep source import; no `.bicepparam`, AVM, overlays | Export-only depth (N29–N31) |
| Governance | No policy/quota/identity collector; reachability accepts optional RBAC evidence but nothing supplies it | Preflights missing (N33–N35) |
| FinOps | Idle/orphan engines and chargeback exist; no Advisor, savings backlog, budget, or delta | Insight not action (N36–N38) |
| Agent surface | 13 MCP tools, read-only, manual `--db`/`ADS_WORKSPACE_DB` resolution | No topology/report tools, no packaging (N41–N42) |
| Maintainability | AutoMapper 12.0.1 + deprecated DI package, 118 references; no `Directory.Build.props`, CPM, analyzers, or lock files; 2,481-line drawer | Upgrade and AOT risk (N46–N48) |

**Excluded because already delivered:** what-if gate, plan approvals, deployment stacks, stack drift, reconcile verbs, decommission planner/runbook/window execution, change feed, drift, topology SVG, BOM, exposure, reachability, locks, data holdings, expiry watch, orphan detection, MCP read-only tools.
**Excluded because Round 2 owns it:** tokens/theming, shell, command palette, tenant forest UI, canvas LOD, cost rail, InfoTips, motion/3D, virtualization *as UI work*. N20 is the data API those UI items need; it is not a duplicate.

---

## 3. Selection rules

An item enters the 50 only if all are true:

1. **Not shipped** in v1/v2 or the Round 2 implementation table.
2. **Evidence-backed** by a file, missing gate, or observable behavior (cited below).
3. **Independently acceptable** — one owner can declare it done with an observable check.
4. **No new product category** — extends the existing thesis (Azure-native design + live truth + safe execution), not multi-cloud or general diagramming.
5. **Bounded**: no item is "add AI"; every item names the contract or gate it changes.

Effort: **S** ≤ 2 days · **M** 3–5 days · **L** ≥ 8 days. Impact: **P0** blocks trust · **P1** next value · **P2** leverage.

---

## 4. The 50

### A. Prove it — delivery and correctness gates (N1–N8)

**N1. Build the Desktop host on every PR.** `[S][P0]`
Evidence: `build.yml` builds four projects on Ubuntu; `AzureDesignStudio.Desktop` targets `net10.0-windows10.0.19041.0` and appears only in `publish.yml`.
Action: add a `windows-latest` job that builds Desktop + shared projects, caches the MAUI workload, and packs an unsigned MSIX template (no release upload). Keep the Linux job as the fast gate.
Acceptance: a deliberate C# error in `MauiProgram.cs` fails the PR; job ≤ 20 min; MSIX artifact produced.

**N2. Commit the E2E harness and run a smoke suite in CI.** `[M][P0]`
Evidence: changelog repeatedly cites "desktop CDP/WebView2 gate", but no harness is checked in; only `DockerBuild.ps1`/`GenerateVersioningTargets.ps1` exist.
Action: one Playwright suite for WASM (seeded config, auth bypass) and one WebView2 CDP smoke for Desktop. Scenarios: boot; drop stencil; link ports; undo/redo; save→reload; offline save; seeded scan drawer browse; error toast; zero console exceptions.
Acceptance: `dotnet run`/`npm test` command documented; CI runs WASM suite; Desktop suite runs nightly or on Windows PRs; screenshots attached on failure.

**N3. Add a server integration test project.** `[M][P0]`
Evidence: no `Microsoft.AspNetCore.Mvc.Testing`/`WebApplicationFactory`; `Core.Tests` does not reference `Server`.
Action: `AzureDesignStudio.Server.Tests` with `WebApplicationFactory`, test authentication handler, in-memory EF. Cover Save/Load/Delete isolation by user, 10-design quota, malformed names, guardrail policy authorization, what-if token single-use, approval ownership, decom approval kind-gating.
Acceptance: ≥ 30 tests, no Azure credentials, runs in CI in < 3 min.

**N4. Add bUnit component tests for shared UI logic.** `[M][P1]`
Evidence: no bUnit; the tenant drawer concentrates state, loading, and mutation in one 2,481-line component.
Action: test `AdsCommandService`, drawer tab transitions, validation/error rendering, offline/online status, and the stencil drop state machine; extract components only where a test needs a seam.
Acceptance: ≥ 25 tests; no UI-framework internals asserted; runs headless.

**N5. Enforce coverage and flake budgets.** `[S][P0]`
Evidence: `coverlet.collector` is referenced but no workflow collects coverage; no thresholds.
Action: collect and merge coverage; fail on new-code coverage < 80% and overall regression > 1%; quarantine flaky tests by name with an expiry; publish trend.
Acceptance: PR shows coverage delta; a test that drops below threshold fails.

**N6. Contract-test the MCP surface.** `[S][P1]`
Evidence: 13 tools in `AdsMcpTools`, 21 engine tests, no protocol/schema snapshot tests.
Action: snapshot the tool manifest and JSON schemas; assert pagination bounds, read-only annotations, honest empty/unknown returns, and that no tool opens a writable connection; verify `--db`/env resolution.
Acceptance: renaming/removing a tool or loosening a schema fails the test.

**N7. Inject ARM/ARG failures through the server, not only the engines.** `[M][P1]`
Evidence: `FailureModeSuiteTests` exercises engines; `DeployService` calls real `ArmTransport`; no fake-handler suite verifies token non-consumption on 429/5xx/malformed responses.
Action: fake `HttpMessageHandler` for what-if, deploy, stack, lock, cost, and change-feed calls; assert outcomes, audit rows, and that a failed preview never burns a token.
Acceptance: each ARM failure class has a test; no network.

**N8. Add an engine benchmark and memory budget.** `[M][P1]`
Evidence: no BenchmarkDotNet; `ScanDiffer`, `BillOfMaterials`, `ReportBundle`, `TopologySvgWriter`, and history folding materialize whole snapshots.
Action: benchmarks over 10k/100k synthetic resources; publish allocations and elapsed; fail CI on > 20% regression in the tracked set.
Acceptance: trend artifact per run; one baseline recorded in the doc.

### B. Secure it — identity, tenancy, and server hardening (N9–N16)

**N9. Remove stored subscription client secrets from the server path.** `[L][P0]`
Evidence: `AzureSubscriptionModel.ClientSecret`; `CryptoService` decrypts it; `ClientSecretCredential` is constructed at 7+ sites in `DeployService`.
Action: desktop already has `AdsTokenBroker` and performs ARM calls locally — make user-scoped reads/writes use that token. For server-side execution, use managed identity plus user delegation (OBO) where required, or workload identity federation; if a service principal is unavoidable, store a Key Vault reference, never a decryptable secret. Provide migration, revocation, and dual-run flag.
Acceptance: no client secret in either database; deploy/what-if/scan paths still pass the integration suite; audit names the credential type used.

**N10. Scope guardrails and approvals by tenant/role.** `[M][P0]`
Evidence: `SetGuardrailPolicy` writes row `Id = 1`; any `[Authorize]` caller can replace a policy that governs every deployment on the server. `GuardrailPolicyModel` has no tenant id and no role check.
Action: add tenant/organization id, separate read vs write, require an admin app role (or group claim) to mutate, and bind approvals to the same scope. Migrate the existing row to the server's default tenant.
Acceptance: a non-admin authenticated user gets 403; two tenants cannot read or overwrite each other's policy; tests.

**N11. Normalize identity extraction.** `[S][P0]`
Evidence: `ServiceTools.GetUserId` looks up the legacy `.../objectidentifier` URI; `new Guid(userIdClaim)` in `DesignService`/`DeployService` throws on a missing or non-GUID claim.
Action: one helper returning `(oid, tenantId)` with `TryParse`; accept both mapped `oid` and legacy URI; return typed 401/403 responses instead of exceptions.
Acceptance: malformed/missing claims return 401, not 500; unit tests cover both claim shapes.

**N12. Provide a local crypto provider.** `[S][P0]`
Evidence: `CryptoService` always constructs `DefaultAzureCredential` + Key Vault, even when `DEBUG` uses the in-memory database. Local subscription registration fails without a vault.
Action: `ICryptoService` dev implementation using Windows DPAPI/ASP.NET Data Protection with a persisted local key; production selector by environment; refuse dev keys in Production.
Acceptance: subscription registration works fully offline in DEBUG; production still requires Key Vault.

**N13. Add CSP and security headers, then verify the supply chain.** `[M][P0]`
Evidence: `UseHsts` exists; no CSP anywhere; inline App Insights snippet and several scripts need hashes/nonces; no dependency-review or SBOM step; MSIX signing is optional.
Action: nonce/hash-based CSP with `frame-ancestors 'none'`, `object-src 'none'`, explicit connect-src; `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`; `dotnet list package --vulnerable --include-transitive` gate; CycloneDX SBOM artifact; require signing for release builds.
Acceptance: browser E2E reports zero CSP violations; vulnerable-package gate fails on High/Critical; SBOM attached to release.

**N14. Bound server work.** `[S][P0]`
Evidence: no `AddRateLimiter`, no `MaxReceiveMessageSize`, no request size cap, no health checks, no deadlines. A single caller can send an unbounded design payload.
Action: per-user token bucket on gRPC/HTTP, 16 MB gRPC cap (design cap lower), Kestrel request cap, server deadlines, per-user concurrency semaphore, `/health/live` + `/health/ready`.
Acceptance: oversized payload returns `ResourceExhausted`; burst returns 429; health endpoints work without auth; load test recorded.

**N15. Define and enforce a telemetry/privacy contract.** `[M][P1]`
Evidence: Application Insights is registered for WASM and Desktop; no documented redaction or opt-in; crash logs contain raw exception text.
Action: classify telemetry fields; never send resource names/ids, subscription ids, tags, or design content; hash identifiers; desktop opt-in with an in-app diagnostics page; retention config.
Acceptance: captured telemetry contract test; inspection shows no tenant identifiers; setting persists.

**N16. Make audit trails tamper-evident.** `[M][P1]`
Evidence: `AuditRecord` and `AdsPlanApplyEvents`/`AdsDecomWindowEvents` are append-only by convention only; no hash chain, DB permission, or verification command.
Action: hash-chain each event stream (`prevHash` + canonical body), add a verify command and export; revoke UPDATE/DELETE for the application login where the engine allows; surface a verification badge in the audit view.
Acceptance: deleting/altering a row is detected; verification is covered by a test; export reproduces the chain.

### C. Scale it — workspace, paging, scheduling (N17–N24)

**N17. Adopt EF Core migrations for both databases.** `[M][P0]`
Evidence: `WorkspaceDb` uses `EnsureCreated` + manual `ALTER TABLE`/`CREATE TABLE`; `Program.cs` embeds raw SQL DDL for server audit tables; no `Migrations/` folder.
Action: baseline migrations for SQLite and SQL Server; version table; upgrade fixtures from the current schema; explicit rollback notes; CI applies migrations to a copy.
Acceptance: an existing `workspace.db` and server DB upgrade with row counts unchanged; fresh create and upgrade produce the same schema.

**N18. Make SQLite durable and maintainable.** `[S][P0]`
Evidence: connection string sets only `DataSource` and `Cache=Shared`; no WAL, `busy_timeout`, `foreign_keys`, or integrity check; MCP reads the same file.
Action: `journal_mode=WAL`, `busy_timeout`, `synchronous=NORMAL`, `foreign_keys=ON`, `auto_vacuum=INCREMENTAL`; `VACUUM INTO` backup + restore command; startup `quick_check`; show journal/size in diagnostics.
Acceptance: concurrent read while a scan writes does not throw `SQLITE_BUSY`; backup/restore test passes; integrity failure is surfaced, not ignored.

**N19. Add snapshot retention and compaction.** `[M][P1]`
Evidence: every scan writes a full `PropertiesJson` row per resource; scans are never pruned; no size meter.
Action: retention policy (keep last N scans or D days, configurable), deduplicate resumed rows, compress/delta-encode properties, incremental vacuum, and a size estimate before the next scan. Preserve referenced scans (exercised by reports/plans).
Acceptance: 10 scans × 50k resources stay under the agreed budget; old-scan reads still work until pruned; pruning is reversible via backup.

**N20. Build a paged inventory read path.** `[M][P0]`
Evidence: `TenantScanDrawerTemplate` calls `Filtered().Take(200)`, `Filtered().ToList()`, and `Filtered().Count()`; `AdsWorkspace.GetSnapshotsAsync` loads every row.
Action: SQL-level filtering (normalize tags or use JSON functions), cursor pagination, stable sort, count-on-demand; UI consumes pages; MCP uses the same query service with hard caps.
Acceptance: 100k rows, first page < 100 ms warm, memory bounded; bulk actions stream instead of materializing.

**N21. Add a server-side workspace API.** `[L][P1]`
Evidence: `AzureDesignStudio.Server` has zero workspace references; the shared UI reports `AdsHostFeatures` empty for WASM; scans/designs are desktop-local.
Action: gRPC `Workspace` service (list/get scans, paged inventory, reports, design sync) with user/tenant scoping, quotas, and pagination; opt-in sync from Desktop. Keep MCP local-first.
Acceptance: WASM can read a synced inventory read-only; cross-device save/load; authorization tests prove no cross-user access.

**N22. Support multiple accounts and tenants.** `[L][P1]`
Evidence: `MsalAccountService` uses `GetAccountsAsync().FirstOrDefault()` in five places; `ScanRecord` has no tenant id; there is no account switcher.
Action: account store keyed by home account id, tenant id on scans/designs/plans, per-account token cache, switcher in the user drawer, and filters that default to the active tenant.
Acceptance: two tenants scanned in one session; no token or inventory bleed; tenant shown on every scan row and report.

**N23. Make scans incremental.** `[M][P1]`
Evidence: `ScanEngine.RunAsync` always issues the full `Resources | where subscriptionId in (...)` query per group; no cursor or change-feed input.
Action: full baseline + incremental run using `resourcechanges` since the last successful scan, applying add/modify/delete tombstones; periodic full reconcile; manifest records mode.
Acceptance: incremental result equals a full scan on the same fixtures; steady-state rows/queries reduced by ≥ 70%.

**N24. Add a background scan/drift scheduler.** `[M][P1]`
Evidence: no `BackgroundService`/`PeriodicTimer` in production code; scans are click-driven; change-feed collection is manual.
Action: opt-in local scheduler (interval, quiet hours, network/battery guards), resumable checkpoints, notification on completion/drift/expiry, and a server scheduler behind N21.
Acceptance: a scheduled scan survives app restart; pause/cancel works; missed windows are reported, not silently skipped.

### D. Close the loop — design correctness and IaC depth (N25–N35)

**N25. Ship a generic ARM resource tile.** `[L][P0]`
Evidence: unknown catalog keys resolve to `UnknownNodeModel`; adopted live types without a typed model become inert; there is no raw type/apiVersion/properties editor.
Action: `GenericArmNodeModel` with type, apiVersion, name, location, tags, and raw `properties` JSON; schema-lite validation; export as-is; categorized icon fallback; adoption writes real values instead of a placeholder.
Acceptance: any ARG row adopts, edits, exports, and round-trips through `.ads.json`; no `NotImplementedException`; golden tests for 10 untyped fixtures.

**N26. Enforce a design payload contract.** `[M][P0]`
Evidence: `SaveDesignRequest.data` is an unbounded string stored as `nvarchar(max)`; no version check or size cap on the server; `.ads.json` has a version only for text exports.
Action: shared validator (version, max bytes, canonical parse) at client and server; 400 with a stable error code; gRPC cap aligned with N14; migration for legacy payloads.
Acceptance: oversized/malformed/incompatible payloads are rejected before persistence; legacy designs still load.

**N27. Add optimistic concurrency and design history.** `[M][P0]`
Evidence: `DesignModel` has `LastModified` only; `Save` overwrites by name; no revision or history table.
Action: revision/etag on server and local records; `Save` takes the expected revision and returns a conflict payload; history table with named snapshots; UI offers compare/overwrite/restore.
Acceptance: two clients saving the same base revision produce one success and one explicit conflict; restore reproduces bytes.

**N28. Fix offline sync semantics.** `[M][P0]`
Evidence: `FlushPendingAsync` returns on the first deferred failure; `LoadDesign` lets any local pending copy win; offline `DeleteDesign` removes only the local copy, so the server copy reappears on the next online list.
Action: FIFO operation queue with sequence and tombstones; continue past per-item failures with backoff; detect revision conflicts; surface per-design sync state; reconcile deletes explicitly.
Acceptance: an offline create→rename→delete sequence reconnects to the exact intended server state; no resurrection; tests cover partial failure.

**N29. Broaden IaC import.** `[L][P1]`
Evidence: import UI accepts only `.ads.json`; Bicep is ARM→Bicep decompile for display, not Bicep source import; no Terraform import.
Action: ARM JSON import (typed where possible, generic otherwise), Bicep source import via build/compile with parameters, Terraform `azapi` config/state import with explicit fidelity caveats; preview diff before replacing the canvas.
Acceptance: fixtures for one typed and one untyped resource per format; unsupported constructs are counted, never dropped silently.

**N30. Parameterization and environment overlays.** `[M][P1]`
Evidence: deployment parameters are ad hoc (`DeploymentParameters`); no `.bicepparam`, typed parameter schema, or per-environment overlay.
Action: define parameters from the design, export/import `.bicepparam`, overlay SKU/region/count/redundancy per environment, resolve secrets from Key Vault references, and validate at deploy prompt.
Acceptance: one design deploys to dev/prod with distinct overlays; no plaintext secret in any artifact.

**N31. Add Azure Verified Modules as an output target.** `[M][P2]`
Evidence: exports emit raw resources only; no AVM references or version pins.
Action: opt-in module mode with pinned AVM versions, required-parameter validation, and a provenance note; compare raw vs module output in the review drawer.
Acceptance: generated Bicep builds; module mode fails closed when a required input cannot be derived.

**N32. Reusable modules and cross-design composition.** `[L][P2]`
Evidence: no module abstraction; `DesignDiffEngine` compares two saved designs, but designs share no dependency inventory.
Action: save a group as a versioned parameterized module; stamp instances; link designs that share resources; global dependency inventory feeds decommission refusal.
Acceptance: stamping two instances edits independently; decommissioning a shared resource is refused by cross-design evidence.

**N33. Policy preflight against the target scope.** `[L][P0]`
Evidence: `Discovery` has no policy assignment/definition code; the what-if gate checks only ARM prediction and guardrails.
Action: fetch effective assignments and exemptions at the deploy target, evaluate deny/audit/modify against the plan's resource ids/tags, and name the exact assignment/definition. Never reimplement the policy engine; rely on ARM/Policy APIs and state the limits.
Acceptance: a plan blocked by a deny assignment earns no token and names the assignment; an unevaluable scope is reported as unknown, not green.

**N34. Capacity and quota preflight.** `[M][P1]`
Evidence: no quota/capacity code in `Discovery`; `ArmTemplateValidator` explicitly leaves quotas to what-if/deploy.
Action: query regional vCPU/quota usage and SKU/zone availability for the plan; surface hard failures before what-if; cache with short TTL and provenance.
Acceptance: a plan exceeding a known quota is blocked with the quota name/current/limit; unknown quotas are warnings.

**N35. Build the identity and access map.** `[L][P1]`
Evidence: `ReachabilityEngine` accepts optional `ScopeRoleAssignment` but no collector exists; decommission takes RBAC evidence as caller-supplied; there is no UI.
Action: collect role assignments and managed identities per scan scope, resolve effective access with explicit inheritance caveats, render the graph, and feed reachability/decommission evidence.
Acceptance: "who can read this storage account" answers with assignment scope and caveats; no assignment is guessed.

### E. FinOps and lifecycle closure (N36–N40)

**N36. Unify a savings backlog.** `[M][P1]`
Evidence: `IdleCostRanker`, `OrphanDetector`, and chargeback exist separately; no Advisor ingestion and no combined ranked list.
Action: merge orphan/idle findings, Advisor right-size recommendations, and reservation/savings-plan gaps into one ranked backlog with $ and effort; every row links to evidence.
Acceptance: one export lists top 50 opportunities with provenance; duplicate resources are deduped across sources.

**N37. Add pre-deploy cost delta and budget gates.** `[M][P1]`
Evidence: `CostEstimator` prices a design; the what-if review has no monthly delta or budget threshold.
Action: compute monthly delta from the what-if payload, show it beside the review, and allow a budget rule to warn/block; attach the delta to the approval record.
Acceptance: approval shows before/after/delta and provenance; a blocking budget prevents token issuance.

**N38. Attribute cost change to change-feed events.** `[M][P2]`
Evidence: chargeback digest and change feed are separate engines; no month-over-month per-resource delta joined to actor.
Action: join Cost Management rows to resources and change-feed entries, rank movers, and attribute only where the feed proves it; otherwise say "unattributed".
Acceptance: top 10 movers with dollar delta and actor/operation or an explicit unattributed label.

**N39. Deliver drift/expiry/cost digests.** `[M][P1]`
Evidence: no email/Teams/webhook adapter, no notification center beyond toasts; drift and expiry engines only render in the drawer.
Action: local notification center plus webhook/Teams/SMTP adapters with a signed payload, quiet hours, and deduplication; driven by N24.
Acceptance: a scheduled digest is delivered once, retries safely, and contains no secrets/tenant identifiers beyond the configured scope.

**N40. Baseline conformance.** `[M][P1]`
Evidence: design↔live diff exists; there is no declared baseline, conformance score, or per-scope rollup.
Action: declare a design or exported state as the baseline, evaluate every scope, produce a conformance score and drift list, and reuse the existing report bundle.
Acceptance: a tenant with one drifted resource reports 1 violation, not a generic diff; baseline identity is hashed.

### F. Reach further — agent and automation surface (N41–N45)

**N41. MCP v2: complete the read model.** `[M][P1]`
Evidence: 13 tools; no topology, report bundle, BOM, naming, design diff, or baseline conformance tool; no resources/prompts.
Action: add those tools over the existing pure engines, version the tool schema, keep every tool read-only, and expose prompts/resources for common workflows.
Acceptance: schema snapshot; read-only test; a full "scan → topology → report → diff" workflow runs from an MCP client.

**N42. Package and auto-discover the MCP server.** `[S][P2]`
Evidence: `Program.cs` requires `--db` or `ADS_WORKSPACE_DB`; no packaging/tool manifest.
Action: auto-locate the workspace across supported installs, ship as a `dotnet tool`, add `ads-mcp init`/version handshake, and document client config.
Acceptance: `dotnet tool install -g` then zero-config start against a default install works; ambiguous paths fail with a list.

**N43. Add a headless CLI over the same engines.** `[M][P1]`
Evidence: all analysis is UI- or MCP-driven; no CLI project.
Action: `ads scan|diff|bom|report|validate|decom-plan` with JSON output, stable exit codes, cancellation, and the same workspace locking rules as MCP.
Acceptance: CI can produce a BOM and fail on drift without the desktop app.

**N44. Ship a GitHub Action for drift and guardrails.** `[M][P1]`
Evidence: no CI integration; reports are manually downloaded.
Action: composite action using N43, uploads report/artifact, posts a job summary, fails on guardrail violations or configured drift thresholds.
Acceptance: sample workflow on a seeded workspace produces the expected pass/fail.

**N45. Add diagram-as-code interchange.** `[M][P2]`
Evidence: no `drawio`, Mermaid, or SVG/PNG diagram export beyond print/image; no diagram import other than `.ads.json`.
Action: export deterministic draw.io XML/Mermaid/SVG; import draw.io for layout and labels with explicit mapping caveats; embed the static SVG in the report bundle (already supported for topology).
Acceptance: round-trip preserves node positions/labels where representable; unknown shapes are counted; bytes are deterministic.

### G. Keep it maintainable (N46–N50)

**N46. Replace AutoMapper with source-generated mapping.** `[M][P0]`
Evidence: 118 `AutoMapper`/`IMapper` references; AutoMapper 12.0.1 plus the deprecated `AutoMapper.Extensions.Microsoft.DependencyInjection`; changelog records a known DoS advisory with no patched OSS line.
Action: move to Mapperly or explicit mappers for `AzureNodeProfile` and DTO generation; add parity tests; verify trimming/AOT.
Acceptance: zero reflection maps at runtime; DTO golden tests unchanged; `PublishTrimmed` output passes the WASM smoke.

**N47. Centralize build policy.** `[S][P0]`
Evidence: no `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, lock files, or analyzers; package versions are duplicated per project; warnings are not errors.
Action: central package management, shared nullable/lang/analyzer settings, `TreatWarningsAsErrors` for app projects, lock mode, `dotnet format` verification, grouped dependency updates.
Acceptance: clean clone builds with one policy; a new package version is edited in one file; CI fails on warnings/format.

**N48. Decompose the scan drawer.** `[M][P1]`
Evidence: `TenantScanDrawerTemplate.razor` is 2,481 lines with a single `activeTab` field and per-tab load methods.
Action: one component per tab, view models for scan/browse/topology/exposure/reach/changes/cost/stacks/decom/report; shared selection and busy-state store; keep the existing host seam.
Acceptance: behavior parity via N2; no component over 500 lines; tab state survives switching.

**N49. Add a support bundle and error catalog.** `[S][P1]`
Evidence: crash log appends unbounded to `%TEMP%`; no stable error codes outside deploy; no diagnostics export.
Action: rolling logs, stable `ADS-####` codes mapped to remediation, one-click bundle (versions, OS, workspace stats, redacted logs, active config), and a copyable request id.
Acceptance: a user can create a bundle with no secrets; every surfaced error has a code; log size is bounded.

**N50. Make the docs tell the truth.** `[S][P1]`
Evidence: README/AGENTS say 8 projects (11 exist); four plan documents overlap; no ADRs or runbooks; no status dashboard.
Action: docs index with authority order; ADRs for query spine, two-plane canvas, execution safety, credential model, workspace schema; runbooks for migration/rollback, key rotation, incident response; generated project/test counts.
Acceptance: a new contributor reaches a running app and a test from the index; stale counts are eliminated by generation or a check.

---

## 5. Sequencing and effort

### Phase 0 — Trust Gate (weeks 1–4)

Items: **N1–N3, N5–N6, N9–N14, N17–N18, N26, N46–N47, N50** (17 items, ~27–31 dev-days).
Exit gate: all 11 projects build on PR (Windows job for Desktop); committed E2E green; server integration suite green; no plaintext/DB-recoverable subscription secret; guardrail mutation requires admin; CSP + limits + health live; both DBs migrate; design payload bounded; AutoMapper removed; warnings/format enforced.
**Do not start Phase 2 before this gate.**

### Phase 1 — Data foundation (weeks 5–8)

Items: **N4, N7–N8, N15–N16, N19–N20, N27–N28, N48–N49** (11 items, ~28–32 dev-days).
Exit gate: 100k-row page < 100 ms; retention budget proven; offline sync test matrix green; concurrent-save conflict surfaced; audit verification; drawer split; support bundle.

### Phase 2 — Depth and closure (weeks 9–16)

Items: **N21–N25, N29–N40** (17 items, ~70 dev-days).
Sequence inside the phase:
1. N25 generic tile (unblocks all untyped adoption).
2. N33 policy + N34 quota (deploy trust).
3. N35 identity map (feeds N36–N38).
4. N29–N32 IaC depth.
5. N36–N40 FinOps/lifecycle.
N21/N22 (server workspace, multi-tenant) are the long poles; start them first and run them in parallel with the typed work.
Exit gate: unsupported types round-trip; policy/capacity preflights fail closed; baseline conformance; scanner schedule delivers a digest.

### Phase 3 — Automation and leverage (weeks 17–20)

Items: **N41–N45** (5 items, ~25 dev-days).
Exit gate: MCP schema + CLI + Action green on a seeded repo; diagram interchange deterministic.

### Totals

| Phase | Items | Effort (dev-days) | Elapsed, 2 devs |
|---|---|---|---|
| 0 Trust Gate | 17 | 27–31 | 3–4 weeks |
| 1 Data foundation | 11 | 28–32 | 3–4 weeks |
| 2 Depth | 17 | 65–75 | 7–8 weeks |
| 3 Automation | 5 | 22–28 | 2–3 weeks |
| **Total** | **50** | **145–170** | **~15–19 weeks** |

The estimate excludes Round 2 UI and live-Azure certification. Solo, add ~60–80% elapsed time. The "core 28" (Phases 0–1) is 8–10 weeks solo and is the recommended first commitment.

### Dependency notes

- N20 (paging) is a hard prerequisite for Round 2 U45 (virtualized forest) and the browse tab. N19 (retention) is a prerequisite for claiming any 100k-row target.
- N21/N22 depend on N10/N11/N17 (scoped identity, sane claims, migrations).
- N33/N34 depend on N14 (limits) and N9 (credential model) because they add new ARM calls.
- N41–N44 depend on N20/N21 only for remote use; local CLI/MCP can ship earlier.
- N46 (AutoMapper removal) must precede any serious trimming/AOT work; otherwise the AOT budget is measured on a reflection-heavy baseline.

---

## 6. Definition of done and metrics

Every item must ship with: the code change, a test at the right layer, a doc/CHANGELOG note, an observable diagnostic (log/health/metric), and a rollback note. No item is "done" because it compiles.

| Dimension | Gate |
|---|---|
| Build | All 11 projects compile on PR; Desktop job green; zero warnings (app projects) |
| Tests | New-code coverage ≥ 80%; server integration + bUnit + committed E2E; no unquarantined flake |
| Security | No stored subscription secret; CSP violation-free; no High/Critical vulnerable package; audit verifies |
| Tenancy | Cross-user/cross-tenant access tests return 403/404, never data |
| Scale | 100k inventory page < 100 ms; workspace stays under budget for 10×50k scans; 50k scan resumes |
| Reliability | Offline create/update/delete reconciles exactly; conflict is explicit; DB upgrade/rollback proven |
| Operations | Health endpoints; support bundle; migrations; backup/restore drill |
| Product | Untyped ARM round-trip; policy/capacity preflight; baseline score; digest delivered once |

Suggested tracking dashboard: build duration, test count by layer, coverage delta, E2E pass rate, workspace size, scan rows/sec, p95 page latency, credential type in use, audit verification status.

---

## 7. Risks and non-goals

| Risk | Mitigation |
|---|---|
| Credential migration (N9) breaks deploy | Dual-run feature flag; keep the legacy path read-only for one release; integration gate; documented rollback |
| Server workspace (N21) becomes a data-exfiltration surface | Tenant-scoped queries, authorization tests, no cross-user ids, quotas, threat model before coding |
| AutoMapper removal is broad | Codemod + parity golden tests; land N47 first; one PR per mapping family |
| Desktop CI is slow/flaky | Cache MAUI workload; WASM suite on every PR, Desktop on Windows PR + nightly; quarantine by name |
| SQLite migration loses data | Backup before migrate; upgrade fixtures from real v1 files; rollback script; `quick_check` after |
| Round 2 UI pulls the team off the gate | Frozen rule: no new UI surface until Phase 0 exit; UI may consume only data APIs that already exist |
| Policy preflight turns into a policy engine | Read Azure Policy APIs; fail closed/unknown, never reimplement evaluation |

**Non-goals (unchanged):** multi-cloud, CRDT collaboration, general-purpose diagramming, a home-grown policy engine, moving scans into WASM.

**Kill criteria:**
- If N21 cannot prove tenant isolation with tests, keep the workspace desktop-only and ship only the paging/export API.
- If N9's OBO path cannot cover management-group/tenant deployments, keep the service principal but make it a Key Vault reference with rotation and per-tenant scoping.
- If N25 cannot round-trip a representative untyped resource, do not add more typed stencils until it can.

---

## 8. Immediate next 10 (two weeks)

1. **N1** Windows Desktop PR build.
2. **N3** server integration test skeleton + auth/isolation tests.
3. **N2** commit the WASM Playwright smoke (boot/drop/save/undo).
4. **N11** identity helper + 401-not-500 tests.
5. **N12** local crypto provider so DEBUG works without a vault.
6. **N10** guardrail admin/tenant scoping.
7. **N13** CSP + headers + vulnerable-package gate.
8. **N14** gRPC cap, rate limit, health endpoints.
9. **N17/N18** migrations + WAL/backup (land together with a real workspace upgrade test).
10. **N26** design payload validator and size cap.

These ten are intentionally small, independently shippable, and each removes a class of failure that no amount of feature work fixes. After they land, re-estimate Phase 1 against the measured CI times, not this document.

---

## 9. Traceability to prior plans

| This plan | Prior reference | Status difference |
|---|---|---|
| N9, N10, N11, N12 | v1 6.2 credential chain; round-1 89 | Prior work covered the desktop broker; the server secret path and authorization are new |
| N21, N22 | round-1 95; round-2 U54 | Explicitly blocked there on a connection store; N22 is that store |
| N23 | round-1 11, 26 | Resumable scans shipped; incremental scans did not |
| N24, N39 | round-1 59, 70 | Never started |
| N25 | round-1 4, 39; round-2 U7 | Generic tile is the concrete implementation |
| N29–N32 | round-1 38–42, 46, 49 | Not shipped |
| N33–N35 | round-1 54, 55, 85 | Not shipped |
| N36–N38 | round-1 75–79 | Partially shipped (chargeback, idle ranking); backlog/budget/attribution still open |
| N40 | round-1 31, 87 | Not shipped |
| N41–N44 | round-1 93 | Read-only MCP shipped; depth/packaging/CI automation did not |
| N45 | round-1 19 | Not shipped |
| N46–N50 | new | Engineering debt made visible by the audit |

---

*End of proposal. This document is the authority for v3 sequencing until superseded; Round 2 remains the authority for visual/UX work.*
