# Requirements: Azure Design Studio

**Defined:** 2026-10-21
**Core Value:** Design, observe, and change Azure from one workspace — canvas fidelity, live tenant truth, and gated execution are all first-class.

## v1 Requirements

Requirements for the pilot-ready milestone. Each maps to exactly one roadmap phase.

### Identity & Authorization (IDA)

- [ ] **IDA-01**: A caller with a missing or malformed `oid`/`tid` claim receives a typed 401 response, never a 500.
- [ ] **IDA-02**: A caller whose token carries the legacy `.../objectidentifier` URI claim can still read their existing designs, subscriptions, and quota.
- [ ] **IDA-03**: Every gRPC method is deny-by-default; only an explicit anonymous list is callable without authentication, proven by a reflection-based integration test.
- [ ] **IDA-04**: Only a caller holding the `Guardrail.Admin` app role can read or mutate guardrail policy; every other authenticated caller receives 403.
- [ ] **IDA-05**: Guardrail policies and plan approvals are tenant-scoped; two tenants cannot read or overwrite each other's records.
- [ ] **IDA-06**: Only a caller holding `Deploy.Approver` can approve a plan, and self-approval is checked as a separate rule.

### Credentials (CRED)

- [ ] **CRED-01**: A developer can register and exercise a subscription in DEBUG with no Key Vault and no Azure credentials.
- [ ] **CRED-02**: Production refuses development crypto keys and still requires Key Vault.
- [ ] **CRED-03**: No subscription client secret is stored in a form the server can recover and replay; the audit record names the credential type used.
- [ ] **CRED-04**: Existing subscriptions migrate to the new credential model behind a dual-run flag with a documented rollback.
- [ ] **CRED-05**: Desktop user-scoped ARM reads and writes use the desktop token broker rather than a server-stored secret.

### Server Bounds & Payload (BOUND)

- [ ] **BOUND-01**: A caller exceeding the per-user rate limit receives a typed 429/`ResourceExhausted` response and the client can identify it as retryable.
- [ ] **BOUND-02**: Oversized requests are rejected before persistence: 16 MB transport ceiling and a design cap below it.
- [ ] **BOUND-03**: Long-running deploy server streams are not killed by a global deadline; deadlines are calibrated per method.
- [ ] **BOUND-04**: Per-user concurrency is capped so one caller cannot exhaust server memory with parallel 16 MB requests.
- [ ] **BOUND-05**: `/health/live` and `/health/ready` answer without authentication, without an HTTPS redirect, and without fallback-policy interference.
- [ ] **BOUND-06**: Every design payload carries a version and a size contract enforced by the client and the server; legacy designs still load.
- [ ] **BOUND-07**: All clients share the cap constants, use `GrpcWebText` for server-streaming deploy, and raise their own receive limits.

### Data Durability (DATA)

- [ ] **DATA-01**: An existing `workspace.db` upgrades through EF migrations with designs, scans, and plans intact and row counts unchanged.
- [ ] **DATA-02**: The server database upgrades through EF migrations, and rollback is documented and rehearsed.
- [ ] **DATA-03**: SQLite runs WAL with `busy_timeout` and `foreign_keys`; a concurrent scan write and MCP read does not fail with `SQLITE_BUSY` or `SQLITE_LOCKED`.
- [ ] **DATA-04**: Startup `quick_check` surfaces workspace corruption instead of silently continuing.
- [ ] **DATA-05**: A user can create and restore a WAL-safe backup (`VACUUM INTO`), and the restore is verified.
- [ ] **DATA-06**: MCP opens a WAL workspace read-only with an explicit, reported fallback mode and passes a cold-workspace test.
- [ ] **DATA-07**: A packaged app finds or imports an existing unpackaged workspace, and MCP discovery probes both install roots.
- [ ] **DATA-08**: The migration baseline adopts existing DDL, reports or dedupes rows that violate new unique indexes, and documents `__EFMigrationsLock` recovery.

### Build & CI (BUILD)

- [ ] **BUILD-01**: A clean clone restores and builds with the SDK pinned at 10.0.401 and locked package versions.
- [ ] **BUILD-02**: The Desktop host builds on pull requests, and a deliberate error in `MauiProgram.cs` fails the job.
- [ ] **BUILD-03**: Package versions are centralized, app projects treat warnings as errors, and changed files pass a format check.
- [ ] **BUILD-04**: CI fails on High/Critical vulnerable packages, with one dated AutoMapper allowlist entry that expires.
- [ ] **BUILD-05**: The non-blocking Windows Desktop job records its duration and becomes blocking after two consecutive green runs.

### Tests (TEST)

- [ ] **TEST-01**: A server integration suite boots the real host and runs in CI without Azure credentials.
- [ ] **TEST-02**: Server tests prove user and tenant isolation, the 10-design quota, malformed names, what-if token single-use, approval ownership, and decommission kind-gating.
- [ ] **TEST-03**: Component tests cover palette filter and keyboard navigation, owner-scoped registry removal, inspector selection, and status-bar counts.
- [ ] **TEST-04**: A committed E2E smoke suite covers boot, stencil drop, port link, undo/redo, save→reload, offline save, scan browse, error toast, and zero console exceptions.
- [ ] **TEST-05**: New test projects are wired into both the build and test workflow steps, and CI asserts each project's test count.
- [ ] **TEST-06**: New-code coverage is collected with an 80% gate, and flaky tests are quarantined by name with an expiry.

### Diagnostics (DIAG)

- [ ] **DIAG-01**: Every user-facing error carries a stable `ADS-####` code mapped to a remediation.
- [ ] **DIAG-02**: Logs roll and stay bounded; they no longer append without limit to `%TEMP%`.
- [ ] **DIAG-03**: A user can produce a one-click support bundle containing versions, OS, workspace stats, redacted logs, and active config, with no secrets.

### Signing & Provenance (SIGN)

- [ ] **SIGN-01**: A tagged release fails when signing material is absent; only manual dispatch may explicitly request an unsigned build.
- [ ] **SIGN-02**: The MSIX `Publisher` equals the signing certificate subject and `Identity Name` is stable; CI asserts both.
- [ ] **SIGN-03**: The MSIX is timestamped, its signature verifies, and it installs and launches on a clean Windows machine.
- [ ] **SIGN-04**: A release publishes a CycloneDX SBOM, per-artifact SHA-256 checksums, and a recorded clean-machine install verification.
- [ ] **SIGN-05**: The package version comes from nbgv through XML-node editing and matches `^\d+\.\d+\.\d+\.0$` without rewriting `MinVersion`.

### Shell (SHELL)

- [ ] **SHELL-01**: The three-zone layout exposes a named zone contract with persisted widths, snap-to-collapse, and keyboard resize.
- [ ] **SHELL-02**: The command palette opens with Ctrl/Cmd+K, supports arrow/Enter/Esc, returns focus, and unregisters commands when its owner disposes.
- [ ] **SHELL-03**: The status bar distinguishes saved-locally, synced, and offline-pending states and shows the scan's as-of time.
- [ ] **SHELL-04**: Theme tokens load in both HTML entry points with no flash, using one documented AntDesign theme axis.
- [ ] **SHELL-05**: An `InfoTip` component backed by one doc registry renders definitions and Learn links, and CI fails on any non-200 link.
- [ ] **SHELL-06**: An axe gate runs in CI, drag surfaces have keyboard/single-pointer equivalents, focus is never obscured, and targets meet 24×24 CSS px.
- [ ] **SHELL-07**: UI-touching PRs attach scripted before/after screenshots, and a hero set is regenerated per release.

### Post-Gate Capabilities (POST)

- [ ] **POST-01**: Users can browse a 100k-row tenant inventory through a paged read path and a virtualized tree with APG semantics.
- [ ] **POST-02**: The cost rail recomputes locally within 200 ms and every figure carries a provenance badge; actuals stay Desktop-only.
- [ ] **POST-03**: A deterministic offline architecture linter reports Well-Architected rule violations in the validation drawer and through one read-only MCP tool.
- [ ] **POST-04**: Audit streams are hash-chained, a verify command detects tampering, and export reproduces the chain.
- [ ] **POST-05**: Telemetry fields are classified and redacted, identifiers are hashed, and desktop diagnostics are opt-in with a persisted setting.

## v2 Requirements

Deferred to the next milestone. Tracked but not mapped to the current roadmap.

### Server & Tenancy

- **SRV-01**: A server workspace API enables browser read-only inventory and cross-device design access with tenant isolation.
- **SRV-02**: A multi-account/tenant switcher with per-account token cache and tenant id on scans, designs, and plans.

### IaC & Automation

- **IAC-01**: ARM/Bicep/Terraform import depth with `.bicepparam` overlays and Azure Verified Modules output.
- **IAC-02**: A headless CLI and GitHub Action over the same engines for drift and BOM automation.
- **MCP-01**: MCP "propose" tier that can create plans pending human approval but never apply.

### Experience

- **UX-01**: 3D/isometric overlay, parallax, and native title bar — only after the 2D performance gate is measured.
- **UX-02**: Real-time collaboration / CRDT multi-user editing.

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Multi-cloud providers (AWS/GCP) | Every engine is Azure-shaped; supporting another cloud forks the catalog, scan, export, policy, cost, and drift surfaces. |
| Home-grown Azure Policy evaluation engine | Correctness requires Azure Policy's own evaluation; a reimplementation would lie with confidence. |
| Tenant scans in WASM | The browser host has no Discovery reference, workspace, or live credential chain. |
| Cross-tenant connection store in this milestone | Depends on scoped identity and migrations that are still being landed; it would put an unscoped query surface before a pilot. |
| Agent/LLM writes that apply without approval | Removes the what-if + hashed-plan + named-approval property that makes the product trustworthy. |
| Telemetry on by default | A governance tool must not ship tenant identifiers before consent. |
| Chasing native Mica through CSS | WebView2 cannot request OS Mica; claiming it while shipping `backdrop-filter` is dishonest. |
| A second desktop E2E stack | Two harnesses fight Entra auth twice and one will rot; use one Playwright suite plus the existing CDP path. |

## Traceability

Updated 2026-10-21 during roadmap creation. Every v1 requirement maps to exactly one phase.

| Requirement | Phase | Status |
|-------------|-------|--------|
| IDA-01 | Phase 5 | Pending |
| IDA-02 | Phase 5 | Pending |
| IDA-03 | Phase 5 | Pending |
| IDA-04 | Phase 5 | Pending |
| IDA-05 | Phase 5 | Pending |
| IDA-06 | Phase 5 | Pending |
| CRED-01 | Phase 5 | Pending |
| CRED-02 | Phase 5 | Pending |
| CRED-03 | Phase 9 | Pending |
| CRED-04 | Phase 9 | Pending |
| CRED-05 | Phase 9 | Pending |
| BOUND-01 | Phase 6 | Pending |
| BOUND-02 | Phase 6 | Pending |
| BOUND-03 | Phase 6 | Pending |
| BOUND-04 | Phase 6 | Pending |
| BOUND-05 | Phase 6 | Pending |
| BOUND-06 | Phase 6 | Pending |
| BOUND-07 | Phase 6 | Pending |
| DATA-01 | Phase 4 | Pending |
| DATA-02 | Phase 4 | Pending |
| DATA-03 | Phase 4 | Pending |
| DATA-04 | Phase 4 | Pending |
| DATA-05 | Phase 4 | Pending |
| DATA-06 | Phase 4 | Pending |
| DATA-07 | Phase 11 | Pending |
| DATA-08 | Phase 4 | Pending |
| BUILD-01 | Phase 2 | Pending |
| BUILD-02 | Phase 2 | Pending |
| BUILD-03 | Phase 2 | Pending |
| BUILD-04 | Phase 2 | Pending |
| BUILD-05 | Phase 2 | Pending |
| TEST-01 | Phase 3 | Pending |
| TEST-02 | Phase 8 | Pending |
| TEST-03 | Phase 8 | Pending |
| TEST-04 | Phase 8 | Pending |
| TEST-05 | Phase 8 | Pending |
| TEST-06 | Phase 8 | Pending |
| DIAG-01 | Phase 8 | Pending |
| DIAG-02 | Phase 8 | Pending |
| DIAG-03 | Phase 8 | Pending |
| SIGN-01 | Phase 11 | Pending |
| SIGN-02 | Phase 11 | Pending |
| SIGN-03 | Phase 11 | Pending |
| SIGN-04 | Phase 11 | Pending |
| SIGN-05 | Phase 11 | Pending |
| SHELL-01 | Phase 7 | Pending |
| SHELL-02 | Phase 7 | Pending |
| SHELL-03 | Phase 7 | Pending |
| SHELL-04 | Phase 7 | Pending |
| SHELL-05 | Phase 7 | Pending |
| SHELL-06 | Phase 7 | Pending |
| SHELL-07 | Phase 7 | Pending |
| POST-01 | Phase 10 | Pending |
| POST-02 | Phase 10 | Pending |
| POST-03 | Phase 10 | Pending |
| POST-04 | Phase 10 | Pending |
| POST-05 | Phase 10 | Pending |

**Coverage:**
- v1 requirements: 57 total
- Mapped to phases: 57
- Unmapped: 0
- Mapped to more than one phase: 0

Phase 1 (Land and Stabilize) is an enabling gate with no mapped v1 requirement; it protects the baseline every mapped phase depends on.

---
*Requirements defined: 2026-10-21*
*Last updated: 2026-10-21 after roadmap creation*
