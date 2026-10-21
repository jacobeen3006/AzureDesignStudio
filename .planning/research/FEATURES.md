# Feature Research

**Domain:** Azure architecture design + governance workspace (Blazor WASM / MAUI Windows desktop + gRPC server + read-only MCP), brownfield, **subsequent milestone**
**Researched:** 2026-10-21
**Confidence:** HIGH for trust/security and delivery table stakes (Microsoft Learn + W3C primary sources cross-checked against the live repo); MEDIUM for effort sizing and competitor positioning

## Scope and reading rules

This is a **subsequent-milestone** feature landscape, not a greenfield survey. v1/v2 capabilities (49 stencils, ARM/Bicep export, scans, topology, drift, what-if gate, deployment stacks, decommission planner, read-only MCP) are treated as **shipped** and are not re-researched here. `.planning/codebase/` already describes what exists; this file describes what must be **added**, and what must be **refused**, so that a pilot user can install a signed desktop build and trust it.

Milestone shape (from `.planning/PROJECT.md`): land the uncommitted slice → close the v3 Phase 0 Trust Gate → complete the Round 2 shell → ship a signed MSIX.

Labels used below:

- **Table stakes** — a pilot reviewer will conclude the product is not ready if it is missing. Not "nice to have".
- **Differentiator** — already shipped or cheap to ship, and is the reason someone uses this instead of the Azure portal.
- **Anti-feature** — plausible-sounding request that forks the architecture or lies about certainty. Recorded so it stops being re-argued each milestone.
- Effort uses the repo's own scale: **S** ≤ 2 days, **M** 3–5 days, **L** ≥ 8 days (matches `docs/v3-plan.md` §3 so estimates are comparable).
- "Gap" = what is missing **in this repo today**, with a file or doc citation. Doc citations (`v3 N11`, `assessment F6`, `U45`) point at the authoritative plan section.

## Feature Landscape

### Table Stakes — Trust and security

A pilot ships a desktop app that holds a user's token and can change Azure. Every row below is a precondition for that sentence being defensible.

| Feature | Why Expected | Complexity | Notes (repo-specific) | Source |
|---|---|---|---|---|
| Typed identity extraction (401/403, never 500) | A missing claim is an authentication outcome, not a server fault. Microsoft identity guidance is explicit that authorization decisions read `oid`/`tid`/`roles`/`scp` claims; a malformed token must be rejected cleanly. | LOW (S) | **Gap:** 26 `new Guid(...)` sites in `Server`; `ServiceTools.GetUserId` reads only the legacy `.../objectidentifier` URI claim. One helper returning `(oid, tenantId)` via `TryParse`, accepting mapped `oid` **and** the legacy URI. Acceptance: both claim shapes tested; missing/non-GUID claim → 401, never 500. Note `AzureDesignStudio.Server` has no test project at all, so this cannot be proven today. | v3 N11, next-phases 4.1, assessment F7 |
| Role-gated guardrail policy mutation | A policy that governs every deployment on the server must not be writable by any authenticated caller. Entra **app roles** surface in the `roles` claim and are the documented mechanism for "this caller is an administrator"; delegated scopes (`scp`) answer "may this app call the API", not "may this human change global policy". | MEDIUM (M) | **Gap:** `DeployService.cs:1261` `private const int GuardrailRowId = 1` — one global row, no tenant id, no role check. Add tenant/organization id, read/write split, admin app role (`Guardrail.Admin`), migrate row 1 to the default tenant, bind approvals to the same scope. Acceptance: non-admin → 403; two tenants cannot read or overwrite each other; requires N11 first (you cannot scope by tenant id you cannot parse). | v3 N10, next-phases 4.5 |
| Explicit authorization on every gRPC method (no fail-open default) | The default must be "deny"; a new service method added without `[Authorize]` must not be anonymously callable. | LOW (S) | **Gap:** `.planning/codebase/CONCERNS.md` records a fail-open authorization default on the gRPC surface plus single-scope authorization. Add a server test that enumerates every service method over reflection and asserts anonymous invocation is rejected — that is the only durable guard, because code review will miss the next method. | CONCERNS §Security, v3 N3 |
| No server-recoverable subscription secrets | Long-lived, replayable client secrets in a database are the single largest pilot-blocking security finding. Microsoft's guidance for service-to-Azure auth is managed identity or workload identity federation; where a principal is unavoidable, store a **Key Vault reference**, never a decryptable secret. | HIGH (L) | **Gap:** `AzureSubscriptionModel.ClientSecret`; 8 `ClientSecretCredential` construction sites under `Server`. Desktop already owns `AdsTokenBroker` and calls ARM locally — route user-scoped reads/writes through that. Long pole of the gate: start first, dual-run flag, migration + revocation, audit records the credential type used. | v3 N9, assessment §2 |
| DEBUG works with no Key Vault / no Azure credential | A contributor (or a pilot's offline evaluation) must be able to register and exercise a subscription without a vault. Environment-conditional cryptography is standard practice (DPAPI / ASP.NET Data Protection locally). | LOW (S) | **Gap:** `CryptoService` always constructs `DefaultAzureCredential` + Key Vault, even when DEBUG uses the in-memory DB. `ICryptoService` with a DPAPI/DataProtection dev implementation; production selector refuses dev keys. Acceptance: subscription registration completes fully offline in DEBUG. | v3 N12, CONCERNS §Known Bugs |
| Bounded server work: rate limiting + message caps + deadlines + concurrency | Every public API needs abuse bounds before it has users. ASP.NET Core ships `AddRateLimiter` (fixed/sliding/token-bucket/concurrency, partitioned per user), `MapHealthChecks`, and gRPC `MaxReceiveMessageSize`. | MEDIUM (M) | **Gap:** zero matches for `AddRateLimiter`, `MaxReceiveMessageSize`, `MapHealthChecks`, `UseCompression` in any `.cs`. **Calibration detail:** the gRPC server default receive size is **4 MB**; v3's "16 MB cap" is therefore a *raise*, not a tightening — it must be paired with the per-user concurrency semaphore, or one caller can allocate 16 MB per in-flight call. The design payload cap (next row) must sit **below** the transport cap. Oversized → `ResourceExhausted`; burst → 429; deadlines set on every call. | v3 N14, Learn: rate limiting, gRPC config |
| Health endpoints `/health/live` + `/health/ready` | Pilot operators need liveness vs readiness separated; orchestrators restart on liveness and stop routing on readiness. | LOW (S) | **Gap:** no `MapHealthChecks`. Use tags (`live`, `ready`), a readiness check that touches SQL Server only, and unrestricted (unauthenticated) probes bound to the container port — Learn warns about `RequireHost` spoofing, so filter by connection port rather than `Host` header. | v3 N14, Learn: health checks |
| Design payload size/version contract | Unbounded `nvarchar(max)` with no version check means one malformed client payload becomes a server outage or a silent data corruption. | MEDIUM (M) | **Gap:** `SaveDesignRequest.data` is an unbounded string; no size cap or version check server-side; `.ads.json` versioning exists only for text exports. One shared validator (canonical parse, version, max bytes) used by client **and** server, stable `ADS-####` error code, legacy designs still load. | v3 N26 |
| CSP + security headers + dependency vulnerability gate + SBOM | Pilot security review asks for these as a set. `dotnet package list --vulnerable --include-transitive --format json` is first-party (SDK ≥ 9.0.300; noun-first form in .NET 10) and can gate CI. | MEDIUM (M) | **Gap:** no `Content-Security-Policy` anywhere; the App Insights snippet and several scripts are inline (need nonce/hash); AutoMapper 12.0.1 is already an accepted advisory, so the gate needs one **dated allowlist entry that expires** rather than a blanket exclusion. | v3 N13, N46, Learn: dotnet package list |
| **Signed MSIX whose publisher identity matches the certificate** | Windows requires MSIX to be signed with a code-signing certificate that **chains to a trusted root on the device**; unsigned or untrusted packages fail to install (`0x800B0109`). Timestamping is strongly recommended so the package still installs after cert expiry. | MEDIUM (M) | **Gap (the milestone's headline risk):** `.github/workflows/publish.yml` signs only when `secrets.MSIX_PFX_BASE64` is present and **silently uploads an unsigned package otherwise** — the pipeline cannot distinguish "pilot build" from "trustworthy build". `Platforms/Windows/Package.appxmanifest` declares `Publisher="CN=Azure Design Studio"`, a placeholder that will not match any real certificate subject → install fails with publisher mismatch. The csproj sets `WindowsPackageType=None`; packaging exists only as a `-p:WindowsPackageType=MSIX` publish flag, so a local publish and CI can diverge. Required: real publisher CN, signing that **fails closed** for release builds, a signature-verification step (`Get-AuthenticodeSignature` / `signtool verify /pa`), install on a clean machine, and a documented cert-custody path. | v3 Phase 0, publish.yml, Package.appxmanifest, Learn: sign an MSIX package |
| Packaged-app data location continuity | Switching from unpackaged to MSIX changes where MAUI's `FileSystem.Current.AppDataDirectory` resolves, which moves `workspace.db` and invalidates any hard-coded discovery path. A pilot user who installed an earlier unpackaged build must not open an empty workspace after upgrading. | MEDIUM (M) | **Gap:** workspace is created at `FileSystem.Current.AppDataDirectory` (`MauiProgram.cs:58`) while `Mcp/Program.cs:45-46` assumes `%LOCALAPPDATA%\Azure Design Studio\AzureDesignStudio.Desktop\Data\workspace.db`. [INFERENCE] the packaged path becomes the package `LocalState` folder — confirm with a spike, then ship a first-run import/migration prompt and update MCP discovery to probe both locations. Blocks the pilot regardless of signing. | MauiProgram.cs:58, Mcp/Program.cs:45, U45 notes |
| Tamper-evident audit trail | A governance product that writes an apply audit is expected to prove the audit was not edited. Append-only by convention is not evidence. | MEDIUM (M) | **Gap:** `AuditRecord`, `AdsPlanApplyEvents`, `AdsDecomWindowEvents` are append-only by convention only. Pilot scope: hash-chain each stream (`prevHash` + canonical body), a `verify` command, export that reproduces the chain, and a badge in the audit view. Revoking UPDATE/DELETE for the app login is desirable but provider-dependent — do not block the pilot on it. | v3 N16 |
| Telemetry privacy contract + diagnostics opt-in | Enterprises will ask what leaves the machine before they install. Raw resource ids and subscription ids in App Insights is a non-starter. | MEDIUM (M) | **Gap:** Application Insights registered for both WASM and Desktop with no documented redaction; crash log appends raw exception text. Classify fields, never send resource names/ids/subscription ids/tags/design content, hash identifiers, desktop opt-in with an in-app diagnostics page, tested contract. Pairs with the support bundle row in Delivery. | v3 N15, N49 |

**Two rows above are deliberately P2, not pilot-gating:** tamper-evident audit (N16) and the telemetry contract (N15). The pilot ships with the existing append-only audit and existing telemetry; both land immediately after the gate because they are prerequisites for a *production* release, not for a controlled pilot with named users. They are marked P2 in the prioritization matrix and excluded from the pilot launch list. Every other row in this table is P1 — if one of those is missing, do not call the build pilot-ready.

### Table Stakes — Delivery

A signed artifact that nobody can reproduce or diagnose is not shippable. These rows are the "trust the build" half of the gate.

| Feature | Why Expected | Complexity | Notes (repo-specific) | Source |
|---|---|---|---|---|
| Every host builds on PR (Windows Desktop job) | The most-modified host must not be the only one with no gate. The build is currently unverified even locally: the assessment saw a Desktop Release build still running after 11+ minutes with no result. | MEDIUM (M) | **Gap:** `build.yml` builds Server, WASM, MCP, Core.Tests on `ubuntu-latest` and says so in a comment; `Desktop` appears in **no PR workflow** — only `publish.yml` on tags/manual. Stage it: `windows-latest` job, NuGet + MAUI workload cached, `continue-on-error: true` until two consecutive runs are stable, then blocking. Do **not** add MSIX packing until the duration is known. Acceptance: a deliberate error in `MauiProgram.cs` fails the PR. | v3 N1, assessment F6, next-phases P3 |
| Committed E2E smoke suite | Release notes already claim CDP/WebView2 gates that are not in the repository; regressions are caught by hand. | MEDIUM (M) | **Gap:** no `*e2e*`/`*playwright*`/`*cdp*` files. One WASM Playwright suite (seeded config, auth bypass) plus a WebView2 CDP smoke — boot, drop stencil, link ports, undo/redo, save→reload, offline save, drawer browse, error toast, zero console exceptions. Reuse the existing CDP capture path for screenshots; do **not** stand up a second desktop harness (U99 says the same). | v3 N2, U99 |
| Server integration test project | Tenant isolation, quotas, token single-use and approval ownership are server behaviours. Engine tests cannot prove them. | MEDIUM (M) | **Gap:** `Core.Tests` references only Core, Workspace, Discovery; no `WebApplicationFactory`, no test auth handler. New `AzureDesignStudio.Server.Tests`: user isolation, 10-design quota, malformed names, guardrail authorization, what-if token single-use, approval ownership, decom kind-gating. ≥30 tests, <3 min, no Azure credentials. This project is also the **only** way to prove the identity and guardrail rows above. | v3 N3, next-phases P5 |
| Component tests for the shell | The palette, inspector, status bar and registry are new, stateful, and untested; two of them are pure logic that needs no UI framework. | MEDIUM (M) | **Gap:** no bUnit project; zero tests reference `CommandPalette`, `DesignInspector`, `StatusBar`, `ThemeToggle`, `AdsCommandService`. `LinkTypeClassifier` is the pattern to copy — it was extracted and tested (122 lines). Move `AdsCommandService` into `Core` (it has no Blazor dependency) and cover register/overwrite/order/lookup/**remove** there. | v3 N4, assessment F7, next-phases P2 |
| Coverage + flake budget | Coverage data is already collectable; not collecting it wastes an existing dependency. | LOW (S) | **Gap:** `coverlet.collector` referenced, never invoked. Collect and merge, fail on new-code < 80% after N3/N4 exist, quarantine flakes by name with an expiry. Do not gate on overall percentage first — the legacy surface would block every PR. | v3 N5 |
| EF Core migrations for **both** databases, with upgrade fixtures and rollback | A pilot must be able to upgrade a real `workspace.db` without losing designs; the server must be deployable twice without manual SQL. | HIGH (L) | **Gap:** `EnsureCreated()` in 5–7 places plus ad-hoc `ALTER TABLE`/`CREATE TABLE`; no `Migrations/` folder anywhere. Provider-specific detail from EF docs: **SQLite cannot generate idempotent migration scripts**, so the desktop path must use runtime `Database.Migrate()` (EF Core 9+ takes a migration lock) while SQL Server uses an idempotent script/bundle. Baseline migration from the **current** schema, upgrade fixture built from a real v1 workspace file, row counts asserted unchanged, rollback note per migration, backup taken first. | v3 N17, Learn: applying migrations |
| SQLite durability: WAL, busy timeout, integrity check, backup/restore | Desktop writes a scan while MCP reads the same file; the default journal mode makes that a `SQLITE_BUSY` race. | LOW (S) | **Gap:** connection string sets only `DataSource` + `Cache=Shared`. Set `journal_mode=WAL`, `busy_timeout`, `synchronous=NORMAL`, `foreign_keys=ON`, `auto_vacuum=INCREMENTAL`; startup `quick_check`; `VACUUM INTO` backup + restore command surfaced in diagnostics. Land **with** migrations, not after. | v3 N18, CONCERNS §Fragile Areas |
| Support bundle + stable error codes | First-line pilot support without a debugger: version, OS, workspace stats, redacted logs, active config, one request id. | LOW (S) | **Gap:** crash log appends unbounded to `%TEMP%\ads_desktop_crash.log` (`MauiProgram.cs:27,163`); no `ADS-####` catalog outside deploy. Rolling bounded logs, code→remediation map, one-click bundle that provably contains no secrets/tenant identifiers, copyable request id. Packaged apps still reach `%TEMP%`, so the path survives MSIX — but say so in the bundle UI so users can find it. | v3 N49, CONCERNS §Performance |
| Pinned SDK + central build policy | A signed artifact must be reproducible; version stamping (`nbgv` in `build.yml`, regex rewrite of the manifest in `publish.yml`) makes floating SDKs a packaging risk, not a style preference. | LOW (S) | **Gap:** no `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, lock files. Add `global.json` (10.0.401, `rollForward: latestPatch`) immediately; central package management + warnings-as-errors for app projects next. `dotnet format --verify-changed` on changed files only — no whole-repo reformat. | v3 N47, assessment §5.1 |
| Release provenance: SBOM + checksums + written install verification | A pilot reviewer will ask "what is in this binary, and how do I know it is the binary you built". | MEDIUM (M) | Attach a CycloneDX SBOM to the release, publish SHA-256 per artifact, and record a clean-VM install (date, OS build, cert thumbprint, result) in the release notes. **Do not** paper over a missing signing secret: for tagged releases the `desktop-msix` job must fail, not upload. | v3 N13/N2, publish.yml |

### Table Stakes — Round 2 shell

The shell is the pilot's first fifteen minutes. These rows are the *minimum* shell for a pilot demo of a "three-zone workspace", not the full U1–U100 catalog. Each names the Round‑2 item it comes from so nothing is invented.

| Feature | Why Expected | Complexity | Notes (repo-specific) | Source |
|---|---|---|---|---|
| Three-zone layout: forest · canvas · inspector, with a rail that collapses and remembers | A dense Azure tool without a persistent left hierarchy and a right detail rail reads as a diagram toy. Users also expect the layout to survive restart. | MEDIUM (M) | **Partial:** the shipped slice is stencil rail · canvas · inspector (U11 "partial"). The left zone is currently *stencils*, not the forest; the right rail is the inspector, not cost. Build the **zone contract** first (named zones, persisted widths, snap-to-collapse, keyboard resize) so forest and cost rail can land into it without rework. Persisted layout belongs with the workspace, not `localStorage`, or WASM and Desktop disagree. | U11, U13, U14 |
| Command palette that owns its command lifecycle | Ctrl+K is the expected affordance in any dense 2020s app; a palette that survives the component that registered it will execute closures over disposed components. | LOW (S) | **Partial + bug:** first cut wired (save/export/validate/cost/compare/import/names/guardrails/user/scan). **Gap:** `AdsCommandService` is a singleton (`Program.cs:72`) while `TopMenu` registers instance-method closures and implements no `Dispose` (assessment F4) — the feature-gated `scan` command outlives a host-feature change. Owner-scoped `RegisterRange(owner, …)` + `RemoveOwner(owner)`, called from `TopMenu.Dispose`. Also add the keyboard contract: Ctrl/Cmd+K opens, arrows move, Enter runs, Esc closes, focus returns to the previous element. | U12, assessment F4, next-phases P2 |
| Virtualized tenant forest (snapshot-first, 100k rows) | The "perfect tree of all connected tenants' resources" is the flagship surface; rendering 200 rows is not a forest. | HIGH (L) | **Gap:** no forest component exists at all; browse loads a completed scan into memory and renders `Filtered().Take(200)`. Blazor's `Virtualize<TItem>` with an `ItemsProvider` renders only the requested window (accepted pattern for large lists), but the **tree** is still custom work: a flattened node index, sticky ancestors, remembered expansion, `aria-level`/`aria-posinset`/`aria-setsize` when the full set is not in the DOM. Hard dependency on a **paged** inventory read path — the UI cannot paper over `Filtered().ToList()`. | U45, U48, U49, N20, APG tree view |
| Local cost rail with provenance on every number | A cost number with no source and no "as of" date is worse than no number: it gets repeated in a budget meeting. A rail that awaits a server round-trip per node cannot hit a 200 ms debounce. | HIGH (L) | **Gap:** no cost rail component; `CostService.EstimateAsync` awaits a server call per node. Build a local estimator over `workspace.db` `CostCacheEntry` + `cost-map.json`/`price-snapshot.json`; server/Retail API only refreshes the cache. Every figure carries a badge (Retail live · snapshot as-of · actuals) and actuals stay Desktop-only behind `AdsHostFeatures`. | U69, U70, U77 |
| WCAG 2.2 AA as an enforced gate, not a review note | Procurement and Windows accessibility guidance both treat accessibility as a build-time check ("run automated checks for every build where possible"). | MEDIUM (M) | **Partial:** zoomable viewport fixed, sider controls are buttons; axe gate pending. Four 2.2 criteria bite this specific UI: **2.5.7 Dragging Movements** (AA) — canvas drag, stencil drag and splitter drag all need a single-pointer/keyboard equivalent (arrow-key nudge, "move to…" command, palette action); **2.4.11 Focus Not Obscured** (AA) — the sticky status bar and flyouts must not hide the focused control; **2.5.8 Target Size** (AA) — 24×24 CSS px minimum with the spacing exception, which the 14 px info glyphs and 8 px splitters currently fail; **1.4.4 Resize Text** — the old `maximum-scale=1.0` was that defect. Add `aria-live="polite"` for the recomputing cost total and throttled scan-status announcements (4.1.3 Status Messages, AA since 2.1). | U96, WCAG 2.2, APG |
| InfoTips backed by a single doc registry with link-rot CI | A governance tool must explain concepts it enforces; stale links destroy trust faster than missing ones. | MEDIUM (M) | **Gap:** no `InfoTip` component. One JSON registry (`key → title, url, anchor, verified:YYYY-MM-DD`), a CI job that HEAD-checks every URL and fails on non-200, coverage as a checklist (every property, validation error, cost line, policy finding, stack setting), out-of-app open via the system browser with copy-link fallback. The existing 24-URL table is already verified — treat it as the seed, not the deliverable. | U79–U82 |
| Error, loading and empty states that carry the fix | The pilot's first scan will hit a permission error or an unreachable tenant. A raw ARM JSON dump ends the evaluation. | MEDIUM (M) | **Gap:** loading/skeleton states are ad hoc per drawer tab; no error catalog and no shared error surface. Every error names what happened, which scope/API, whether retryable, a retry action, and an `ADS-####` code; ARM errors parsed into code + message + suggested action. Skeleton matched to final layout (no reflow); empty states name the one action that fills them. | U88, U89, U93 |
| Theme and tokens consistent across **both** HTML entry points | Two hosts, one UI: a token that exists in `index.html` but not `Desktop/wwwroot/app.html` is a bug users see as "the desktop looks different". | LOW (S) | **Partial:** `ads-tokens.css` shipped; `design-tokens.json` source + hardcode gate pending. Both `wwwroot/index.html` and `Desktop/wwwroot/app.html` must link the same token/theme stylesheets (U2 notes both currently link only light CSS and duplicate a hardcoded gradient). | U1, U2 |
| Status bar as the truth surface | A workspace app needs a persistent, low-noise statement of *what am I looking at and is it saved*. | LOW (S) | **Shipped (first cut):** design name, counts, workspace state, zoom. Remaining pilot gap is honesty, not layout: distinguish "saved locally / synced / offline pending" and show scan as-of time rather than a green dot. | U15, U52 |
| Screenshot attached to every UI PR | Design quality survives only where it is visible in review. | LOW (S) | **Partial:** CDP capture used manually. Script before/after captures into `docs/ui/` for UI-touching PRs; regenerate a hero set per release. Reuse the existing `dom-to-image-more`/CDP paths. | U100 |

### Differentiators (keep, do not dilute)

These are shipped, or deliberately kept on the roadmap, because they reinforce the thesis. The milestone's job is to make the shipped ones *trustworthy*, not to add more of them. Each row names the invariant that must survive the Trust Gate.

| Feature | Value Proposition | Complexity | Notes | Source |
|---|---|---|---|---|
| What-if → hashed plan → named approval → apply | The safest execution primitive in the product: no mutating ARM call without a fresh single-use what-if token and an approval bound to `sha256:` of the plan. This is the moat against "AI/agent changed my cloud" tools. | Already shipped (keep) | Must survive: token single-use proven by a **server** test (currently only engine-level), approval ownership, and the token never burned by a failed preview. Any new surface (cost delta, policy preflight) must **fail closed** and must not issue a token. | v2 11.x, v3 N3/N7/N33 |
| Live topology + drift as tenant truth | Going from a drawing to "what is actually deployed, and what changed" without exporting anything is the second moat. `TopologyDeriver`, `ScanDiffer`, exposure/reachability run offline over a scan. | Already shipped (keep) | Pilot-proofing: scan as-of time and partial-failure list must be visible wherever a topology or drift verdict is shown (U52). A partial snapshot rendered as an all-clear is the fastest way to lose trust. | v2 8.x–9.x, U52 |
| Decommission safety (runbook-as-code, purge stage inert) | Refusing to delete is a feature; a planner that shows blast radius and never runs the purge stage is a credible governance story. | Already shipped (keep) | Keep the refusal path: cross-design/shared-resource evidence (N32) can strengthen it later; do not add a "force purge" escape hatch for pilot convenience. | v2 13.x |
| Read-only MCP surface (13 tools) | Agent-operable **without** write risk: an MCP client can answer questions from the same offline workspace the UI uses. Distinct from Azure MCP Server, which acts on live resources. | Already shipped (keep) | Add a contract snapshot test before the pilot (tool manifest + JSON schemas + read-only annotations + pagination bounds) so "read-only" is enforced, not asserted. Packaging/path auto-discovery (N42) is a pilot convenience, not a gate. | v2 MCP, v3 N6 |
| Offline-first workspace with exportable artifacts | A design that opens on a plane and exports ARM/Bicep/`.ads.json` is a real differentiator against portal-bound tools. | Already shipped (keep) | The **migration** work (EF migrations, WAL, packaged path) is what preserves this claim through the milestone; an upgrade that loses a design converts the differentiator into a defect. | v1/v2, N17/N18 |
| Provenance-honest numbers (cost, drift, findings) | Competitors show a number; ADS can show where it came from and when. Cheap to keep, expensive to retrofit. | LOW (S) | Extend the same pattern to policy/capacity findings later: "unknown" must render as unknown, never green. | U70, v3 N33/N34 |
| Canvas architecture linter (deterministic, offline) | Best value/effort net-new capability: `LinkTypeClassifier` now names every link, so Well-Architected rules (public IP straight to a VM, NSG unassociated, firewall without route table, private endpoint without DNS zone, hub-spoke with no peering) can be evaluated with no ARM call and unit-tested on today's fixtures. Feeds the existing validation drawer **and** a read-only MCP tool. | MEDIUM (M) | **After** the gate (next-phases P7). Not N33 — no policy engine, no API calls, no certainty claims. Reference-architecture gallery doubles as its golden fixtures. | assessment §7.1–7.2, next-phases P7 |

### Anti-Features (commonly requested, refuse)

Recorded with the *reason* so the argument does not restart. The first four are the milestone's explicit non-goals in `.planning/PROJECT.md`; the rest are new and specific to this milestone's shape.

| Feature | Why Requested | Why Problematic | Alternative |
|---|---|---|---|
| Multi-cloud providers (AWS/GCP) | "Architects work in more than Azure." | Every engine — stencil catalog, ARG scan, ARM export, policy, cost, drift — is Azure-shaped. Supporting a second cloud forks all of them and doubles the surface that has no tests. | Say "Azure-native" in the pilot positioning. Keep the topology/drift engines provider-neutral only where it is already free. |
| CRDT / real-time multi-user collaboration | "Figma for architecture." | No infrastructure, no demand, and it changes the execution-safety model: two authors editing a plan that is bound to a hash. | Optimistic concurrency + revision conflicts + design history (v3 N27) covers the actual multi-device case. Single-author today. |
| Home-grown Azure Policy evaluation engine | "Preflight policy without calling Azure." | Correctness requires Azure Policy's own evaluation (deny/audit/modify, exemptions, aliases). A reimplementation would confidently lie — the opposite of the product's value. | Read effective assignments/exemptions from Azure Policy at the deploy target; block or report **unknown** (v3 N33). |
| Moving tenant scans into WASM | "Then the browser is feature-complete." | WASM has no `Discovery` reference, no workspace, no live credential chain; the browser host is explicitly the design/estimate host. | Keep the host split; degrade Desktop-only surfaces through `AdsHostFeatures`; a server workspace API (N21) is the only legitimate path to browser inventory. |
| Cross-tenant connection store **in this milestone** | "The forest must show every tenant." | Correct, but it is a data-model project (account store, tenant id on scans/designs/plans, per-account token cache) that depends on scoped identity and migrations — both still open. Adding it now puts an unscoped multi-tenant query surface in front of a pilot. | Ship the forest for the signed-in tenant's scanned scopes; `ScanRecord.tenantId` + account switcher land in the data-foundation milestone (N22), then U54. |
| 3D/isometric canvas mode before the 2D performance gate | Screenshots look impressive. | The UI research is blunt about the constraint: 60 fps 3D at 10k nodes is not achievable, and every node is already a Blazor component — 2k nodes / 5k edges at 60 fps is **unproven**. Polish on top of an unmeasured renderer is rework. | Spend the effort on the U57 spike; if 2D LOD fails, the answer is a canvas/WebGL layer, not 3D. |
| Chasing native Windows 11 Mica through CSS | "The desktop should feel native." | WebView2 cannot request OS Mica/Acrylic via CSS; MAUI has no first-party Mica path. Claiming "Mica" while shipping `backdrop-filter` is a lie a reviewer will catch. | In-app glass (`backdrop-filter`) + depth cues, labelled as such; a native title bar is an optional spike (U10), not a pilot requirement. |
| A second E2E stack for desktop | "Playwright works everywhere." | Two harnesses fight Entra auth twice, double the CI minutes, and one will rot. The team already runs a CDP/WebView2 capture path manually for release gates — it caught a real dead-circuit crash — so check that in rather than starting over. | One Playwright suite for WASM, the existing CDP path for Desktop; share the seeded offline workspace. |
| Agent/LLM writes that apply directly (no approval) | "Let the agent fix it." | Deletes the only property that makes ADS trustworthy: nothing mutates without a fresh what-if and a named approval bound to the plan hash. | MCP "propose" tier later: tools may **create** a plan pending human approval, never apply. |
| Telemetry on by default for pilots | "We need usage data." | A governance tool that ships tenant identifiers to a cloud endpoint before consent fails the enterprise install review. | Opt-in diagnostics, field contract, local-first support bundle (N15/N49). |

## Feature Dependencies

```
Signed pilot MSIX
├──requires──> Real publisher identity + cert custody (Artifact Signing or OV cert)
├──requires──> Release provenance (SBOM, checksums, fail-closed signing)
│                 └──requires──> Pinned SDK + central build policy
├──requires──> Desktop PR build (Windows job)          [an unbuildable package is not a package]
└──requires──> Packaged-app workspace migration
                  └──requires──> EF migrations + backup/restore
                                     └──requires──> SQLite WAL/busy-timeout

Guardrail role gating (tenant-scoped policy)
├──requires──> Typed identity (oid/tid via TryParse)   [cannot scope by an id you cannot parse]
├──requires──> Entra app role defined + assigned (roles claim)
└──requires──> Server integration tests                [only proof of 403 / cross-tenant isolation]

Server request limits
├──enhances──> Design payload contract                 [payload cap must sit below transport cap]
└──requires──> Per-user concurrency semaphore         [raising gRPC receive to 16 MB multiplies memory]

Tenant forest (U45)
├──requires──> Paged inventory read path (N20)
├──requires──> Flattened index + Virtualize ItemsProvider
├──requires──> Shared selection store                  [AdsContext is canvas-only today]
└──enhances──> Local cost rail                         [selection drives the cost scope]

Local cost rail (U69)
├──requires──> Local cost engine over workspace + shipped price snapshot
├──enhances──> Provenance badges (U70)
└──conflicts──> Per-node server estimate (CostService.EstimateAsync today)

E2E suite (N2)
├──enables──> Screenshot-in-PR (U100)
├──enables──> axe/accessibility gate (U96)
└──enables──> DOM-level proof of palette/forest keyboard contracts

Component tests (N4)
└──enables──> Safe rewrite of AdsCommandService ownership (F4)

Packaged mode (WindowsPackageType=MSIX)
└──conflicts──> Unpackaged dev loop (WindowsPackageType=None, csproj today)
                  [different data roots → two explicit modes, never implicit]
```

### Dependency notes

- **Signed MSIX requires the publisher identity first.** The manifest publisher (`CN=Azure Design Studio`) and the certificate subject must be identical; generating the certificate before the publisher legal name is agreed just moves the failure. Decision order: publisher name → certificate path (Azure Artifact Signing vs OV cert) → manifest → signing step → install test.
- **MSIX requires the workspace migration, not the other way round.** If packaging lands before the path migration, every pilot who tried the unpackaged build appears to have lost their designs. Ship the import/migration and the MCP dual-path probe with the first packaged build.
- **EF migrations before packaged artifacts.** The desktop upgrade path is the one migration that a pilot actually exercises; the server migration can be rehearsed in CI. SQLite's inability to emit idempotent scripts (EF docs) means the desktop must migrate at runtime — so `quick_check` + backup must exist in the same change, or a failed migration bricks the only copy.
- **Guardrail scoping requires typed identity, and that requires a server test project** to be believable. These three are one deliverable, not three phases; splitting them produces code that compiles and proves nothing.
- **Forest requires paging.** `Virtualize` solves rendering, not data volume: `Filtered().ToList()` still materializes 100k rows. Paging first (SQL filters + cursor + stable sort + count-on-demand), then the virtualized tree; reversing the order doubles the work.
- **Cost rail conflicts with the current per-node estimate call.** The local engine must land before the rail is styled, otherwise the debounce target is decorative. Keep the server/Retail API as a cache refresher only.
- **E2E before the a11y gate.** axe-core needs a booted app with a seeded workspace; the harness delivers both, so U96 follows N2 rather than running in parallel with it.
- **Anti-feature guard:** the canvas linter and the MCP "propose" tier are the two extensions that *reinforce* the thesis; anything that needs a new cloud, a new execution path, or a second evaluation engine belongs outside this milestone.

## MVP Definition

The MVP here is the **pilot release**, not a new product. Everything below assumes v1/v2 features stay shipped.

### Launch With (pilot)

Ordered by "failure class retired per day", not by section. The first three are roughly one day of work (assessment §8) and remove findings that no feature work fixes.

- [ ] Land and push the current slice; remove `bd2.dll`/`bd_served.dll`; untrack `.omo`/`.planning`/`.swarm` — *nothing below is safe while 120 commits exist only on one machine* (assessment F5/F1/F2)
- [ ] `global.json` + Windows Desktop PR job (staged non-blocking) + vulnerable-package gate (next-phases P3)
- [ ] Owner-scoped command registry + stale pointer-zone fix — the two lifetime bugs in the surface being demoed (assessment F3/F4)
- [ ] Typed identity (401 not 500) with tests — enables everything tenant-scoped (N11)
- [ ] DEBUG crypto provider so a evaluator can run offline (N12)
- [ ] Request limits + health endpoints (N14) and the design payload contract (N26)
- [ ] Tenant-scoped, role-gated guardrail policy with server tests (N10 + N3)
- [ ] EF migrations + WAL + backup/restore for the local workspace and SQL Server (N17/N18)
- [ ] Credential model: no server-recoverable subscription secret (N9) — start day one, land behind a dual-run flag
- [ ] CSP + headers + SBOM; signing that fails closed (N13)
- [ ] Signed MSIX with the real publisher identity, timestamped, verified, installed on a clean machine
- [ ] Packaged-app workspace import + MCP dual-path discovery
- [ ] Support bundle + `ADS-####` error codes (N49)
- [ ] Three-zone layout with persisted widths, palette keyboard contract, InfoTips + doc registry (U11/U12/U79–U81)
- [ ] Accessibility gate: axe in CI, keyboard equivalents for drag, focus not obscured, 24 px targets (U96)
- [ ] Component tests (bUnit) + committed E2E smoke (N2/N4)

### Add After Validation (pilot → v3.1)

Add when a pilot user asks for it, or when the gate's exit criteria are met and CI minutes are known.

- [ ] Virtualized forest with connection highlighting and ancestor-preserving search (U45–U49) — after the paged read path (N20)
- [ ] Local cost rail with breakdown, provenance, budget state (U69–U71, U77)
- [ ] Canvas architecture linter + reference-architecture gallery (assessment §7.1–7.2) — cheapest net-new capability
- [ ] Policy and capacity preflight that fail closed/unknown (N33/N34)
- [ ] Pre-deploy cost delta + budget gate attached to the approval (N37)
- [ ] Incremental scans + scheduler (N23/N24) — pilot users scan the same tenant repeatedly
- [ ] Audit hash-chain verification (N16)
- [ ] MCP contract snapshot + packaging (N6/N42)

### Future Consideration (v3.2+)

- [ ] Server workspace API / cross-device read (N21) — only if tenant isolation is provable
- [ ] Multi-account/tenant switcher and cross-tenant search (N22/U54)
- [ ] IaC import depth, `.bicepparam` overlays, AVM output (N29–N31)
- [ ] Baseline conformance, savings backlog, cost attribution (N36/N38/N40)
- [ ] CLI + GitHub Action (N43/N44), MCP "propose" tier
- [ ] 3D/parallax, glass depth, native title bar (U33–U44, U10) — after the U57 performance gate proves 2D

**Deferral reasons:** every "future" row either needs a data model that does not exist (connection store, server workspace), depends on a gate that has not been measured (canvas performance), or extends an execution path whose trust story is still being closed. None of them are blocked on willingness.

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---|---|---|---|
| Signed MSIX + publisher identity + fail-closed signing | HIGH | MEDIUM | P1 |
| Windows Desktop PR build | HIGH | MEDIUM | P1 |
| Typed identity (401/403) | HIGH | LOW | P1 |
| Role-gated tenant-scoped guardrails | HIGH | MEDIUM | P1 |
| No server-recoverable subscription secret | HIGH | HIGH | P1 |
| Request limits + health endpoints | HIGH | MEDIUM | P1 |
| Design payload contract | HIGH | MEDIUM | P1 |
| EF migrations + WAL + backup/restore | HIGH | HIGH | P1 |
| Packaged-app workspace continuity | HIGH | MEDIUM | P1 |
| Server integration tests | HIGH | MEDIUM | P1 |
| Committed E2E smoke | HIGH | MEDIUM | P1 |
| Component tests for shell logic | MEDIUM | MEDIUM | P1 |
| Support bundle + error codes | MEDIUM | LOW | P1 |
| Three-zone layout + palette keyboard contract | HIGH | MEDIUM | P1 |
| WCAG 2.2 AA gate (drag alternatives, focus, targets) | HIGH | MEDIUM | P1 |
| InfoTips + doc registry + link CI | MEDIUM | MEDIUM | P1 |
| DEBUG crypto provider | MEDIUM | LOW | P1 |
| CSP + headers + SBOM | HIGH | MEDIUM | P1 |
| Telemetry privacy contract | MEDIUM | MEDIUM | P2 |
| Audit hash-chain | MEDIUM | MEDIUM | P2 |
| Virtualized forest | HIGH | HIGH | P2 |
| Local cost rail | HIGH | HIGH | P2 |
| Screenshot-in-PR | MEDIUM | LOW | P2 |
| Coverage/flake budget | MEDIUM | LOW | P2 |
| Canvas architecture linter | HIGH | MEDIUM | P2 |
| Policy/capacity preflight | HIGH | HIGH | P2 |
| Cost delta + budget gate | MEDIUM | MEDIUM | P2 |
| Incremental scans + scheduler | MEDIUM | MEDIUM | P2 |
| MCP contract snapshot | MEDIUM | LOW | P2 |
| Pinned SDK + central build policy (full) | MEDIUM | LOW | P2 |
| Server workspace / cross-device | MEDIUM | HIGH | P3 |
| Multi-tenant switcher | MEDIUM | HIGH | P3 |
| IaC import depth / AVM | MEDIUM | HIGH | P3 |
| Baseline conformance / FinOps closure | MEDIUM | HIGH | P3 |
| CLI + GitHub Action | MEDIUM | MEDIUM | P3 |
| 3D / native title bar | LOW | HIGH | P3 |

**Priority key:** P1 = pilot cannot ship without it; P2 = should have, add when the gate is green; P3 = future consideration.

## Competitor Feature Analysis

| Feature | Azure portal (+ ARG Explorer) | Azure Quick Review (`azqr`) | Cloud-visualization tools (Hava / Lucidscale / Cloudcraft) | Our approach |
|---|---|---|---|---|
| Trust of the installed client | Browser, no install | `winget` CLI, no installer signing story in scope | SaaS, no desktop artifact | Signed MSIX, timestamped, fail-closed build, clean-VM install proof |
| Tenant visualization | Resource Graph queries + portal blades; no design canvas | Excel/CSV inventory + recommendations | Auto-generated diagrams from cloud APIs | Design **and** live topology in one canvas, with planned-vs-live overlay |
| Live drift | Activity log / change history per resource, not design-anchored | Point-in-time scan | Periodic refresh of a generated diagram | Design↔live diff, drift badges, change feed, baseline conformance |
| Safe change | Portal writes immediately; ARM what-if available separately | Read-only | Read-only | what-if → hashed plan → named approval → apply, with audit |
| Cost | Cost Management actuals | Optional cost stage (last calendar month) | Estimated diagrams | Design estimate + snapshot provenance + (later) actuals, side by side |
| Governance findings | Azure Policy / Advisor / Defender portals | APRL + ARM rules, Advisor, Defender, optional policy stage — **strongest at breadth** | Limited | Canvas-anchored lint + policy preflight that fails closed; do not compete on rule count |
| Extensibility / agent surface | REST, CLI, PowerShell | CLI, JSON/CSV output | APIs, embeds | Read-only MCP over the **same offline workspace** the UI uses |
| Offline | No | No | No | Workspace-first; designs and scans open without a network |

Reading of the landscape: `azqr` already owns breadth-of-recommendations and is a CLI, so ADS should not chase rule count. The visualization tools own auto-generated diagrams from live cloud but cannot gate a change. The portal owns truth but cannot hold a design. ADS's defensible position is the **seam**: a design that is compared to truth and can only be changed through an approved, hashed plan — plus offline operation and an agent surface that cannot write.

Confidence: `azqr` and portal rows are HIGH (primary docs/README read in this pass). Visualization-tool rows are MEDIUM — general market knowledge, not re-verified here; treat them as positioning, not a benchmark.

## Repo-specific gaps called out

Consolidated so a planner can grep one table. "Evidence" is a doc section or file that already records the gap; this file adds the **feature consequence**.

| Gap | Evidence | Feature it blocks | Phase |
|---|---|---|---|
| Desktop host absent from all PR workflows | `build.yml` (no Desktop job), `publish.yml` (tags/manual only) | Signed MSIX, any Desktop regression gate | P3 |
| MSIX signing is conditional and silently unsigned | `publish.yml` `MSIX_PFX` check + comment | Signed MSIX, release provenance | P6 |
| Placeholder manifest publisher `CN=Azure Design Studio` | `Platforms/Windows/Package.appxmanifest` | Signed MSIX install on a clean machine | P6 |
| Packaging only via `-p:WindowsPackageType=MSIX`; csproj says `None` | `AzureDesignStudio.Desktop.csproj:41` | Reproducible packaging, local-vs-CI parity | P3/P6 |
| No `Migrations/`; `EnsureCreated` + ad-hoc DDL | v3 N17, next-phases state table | Workspace continuity, packaged upgrade | P4 |
| SQLite has no WAL/busy timeout; MCP shares the file | v3 N18, CONCERNS §Fragile Areas | Concurrent scan+browse, MCP reliability | P4 |
| No server test project; `Core.Tests` covers 3 projects only | v3 N3, TESTING.md §Known Coverage Gaps | Identity, tenancy, quota, token single-use proof | P5 |
| 26 `new Guid(...)` identity sites; legacy claim only | next-phases state table | Tenant scoping, 401 semantics | P4 |
| Global guardrail row (`GuardrailRowId = 1`) | `DeployService.cs:1261` | Role-gated policy | P4 |
| 8 `ClientSecretCredential` sites + stored `ClientSecret` | assessment §2 | Credential model | P6 |
| Zero rate limiter / message cap / health checks | v3 N13/N14, next-phases state table | Bounded server work | P4 |
| No CSP; inline scripts and App Insights snippet | v3 N13 | Security review | P6 |
| Workspace path from `FileSystem.Current.AppDataDirectory`; MCP assumes a hard-coded `%LOCALAPPDATA%` path | `MauiProgram.cs:58`, `Mcp/Program.cs:45` | Packaged-app data continuity, MCP discovery | P6 |
| Unbounded `%TEMP%` crash log; no error catalog | `MauiProgram.cs:27,163`, CONCERNS | Support bundle | P4/P5 |
| Singleton command registry; `TopMenu` has no `Dispose` | `Program.cs:72`, assessment F4 | Palette correctness | P2 |
| No bUnit project; shell components untested | assessment F7 | Shell regression safety | P5 |
| No E2E artifacts despite changelog claims | v3 N2 | Smoke suite, a11y gate, screenshots | P5 |
| Browse path `Filtered().Take(200)` / `ToList()` | v3 N20 | Forest, bulk actions | P7 |
| No forest component, no cost rail, no `InfoTip` | `src/AzureDesignStudio/Components/` (globbed) | Three-zone claim, help system | P7 |
| Hardcoded gradient + light-only CSS duplicated in two HTML entries | U2 notes | Theme consistency | P7 |
| No `global.json` / `Directory.Build.props` / CPM | assessment F6, v3 N47 | Reproducible build | P3 |

## Confidence assessment

| Area | Confidence | Basis |
|---|---|---|
| Trust/security table stakes | HIGH | Microsoft Learn primary docs (MSIX signing, rate limiting, health checks, gRPC config, EF migrations, app roles, `dotnet package list`) + repo citations |
| Delivery table stakes | HIGH | Same, plus the live `build.yml`/`publish.yml` and EF migration semantics; the one genuinely unknown number is Desktop build duration |
| Round 2 shell table stakes | HIGH for the contracts (WCAG W3C, APG tree view, Blazor `Virtualize`), MEDIUM for effort — the shell slice is real but unmeasured under load |
| Differentiators | HIGH | Shipped v1/v2 code and the plan documents agree; the invariant list is verifiable by test |
| Anti-features | HIGH | Explicit non-goals in `.planning/PROJECT.md` plus measured constraints in the UI research (WebView2 Mica, 3D node budget) |
| Competitor positioning | MEDIUM | `azqr` and portal rows verified; visualization-tool rows are market knowledge, not re-validated |
| Effort sizing (S/M/L) | MEDIUM | Repo's own scale, applied to cited evidence; N9, N17, U45 and U69 are discounted generously because their tails are known |

## Sources

External (all read in this session, 2026-10-21):

- Microsoft Learn — Sign an MSIX package: https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview (trusted-root requirement, timestamping, Azure Artifact Signing vs OV cert costs, sideload default since Windows 10 2004) — HIGH
- Microsoft Learn — Rate limiting middleware in ASP.NET Core: https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit (fixed/sliding/token-bucket/concurrency, per-user partitioning) — HIGH
- Microsoft Learn — Health checks in ASP.NET Core: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks (`AddHealthChecks`, `MapHealthChecks`, tags, host-filtering caveat) — HIGH
- Microsoft Learn — gRPC for .NET configuration: https://learn.microsoft.com/en-us/aspnet/core/grpc/configuration (`MaxReceiveMessageSize` default 4 MB; `EnableDetailedErrors` off by default) — HIGH
- Microsoft Learn — Applying EF Core migrations: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying (bundles vs scripts; SQLite cannot emit idempotent scripts; migration locking in EF Core 9+) — HIGH
- Microsoft Learn — `dotnet package list`: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list (`--vulnerable`, `--include-transitive`, `--format json`; availability since SDK 9.0.300) — HIGH
- Microsoft Learn — Add app roles and get them from a token: https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-app-roles-in-apps (`roles` claim, app roles vs groups, admin consent) — HIGH
- Microsoft Learn — Accessibility overview (Windows apps): https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview (accessibility as a build-time check, accessible names, high contrast) — HIGH
- Microsoft Learn — ASP.NET Core Razor component virtualization: https://learn.microsoft.com/en-us/aspnet/core/blazor/components/virtualization (`Virtualize<TItem>`, `ItemsProvider`, single item source, placeholder/empty content) — HIGH
- W3C — What's new in WCAG 2.2: https://www.w3.org/WAI/standards-guidelines/wcag/new-in-22/ (2.4.11 Focus Not Obscured AA, 2.5.7 Dragging Movements AA, 2.5.8 Target Size AA, 3.2.6, 3.3.7, 3.3.8; 4.1.1 removed) — HIGH
- W3C WAI-ARIA APG — Tree View Pattern: https://www.w3.org/WAI/ARIA/apg/patterns/treeview/ (keyboard model, `aria-expanded`/`aria-level`/`aria-posinset`/`aria-setsize` for dynamically loaded trees) — HIGH
- Azure Quick Review (`azqr`) README: https://github.com/Azure/azqr (ARG/APRL recommendations, Advisor/Defender/optional policy stages, Excel/CSV/JSON output, Reader permission) — HIGH

Repository evidence (read in this session):

- `.planning/PROJECT.md` (milestone requirements, out-of-scope, constraints)
- `.planning/codebase/TESTING.md`, `.planning/codebase/CONCERNS.md`
- `docs/v3-plan.md` (N1–N50, Phase 0 exit gate), `docs/assessment-2026-10-21.md` (F1–F7, proposals), `docs/next-phases-plan.md` (P1–P7, gates), `docs/top-100-ui-improvements.md` (U1–U100, M0 tiers, §0.1 constraints)
- `.github/workflows/build.yml`, `.github/workflows/publish.yml`
- `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:41`, `Platforms/Windows/Package.appxmanifest`, `MauiProgram.cs:27,58,163`, `src/AzureDesignStudio.Mcp/Program.cs:15,45-46`
- `src/AzureDesignStudio/Components/` (component inventory; absence of forest, cost rail, `InfoTip`)

---
*Feature research for: Azure Design Studio — pilot-ready signed milestone (subsequent milestone, brownfield)*
*Researched: 2026-10-21*
