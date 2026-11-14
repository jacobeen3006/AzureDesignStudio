---
gsd_state_version: "1.0"
current_phase: 05
current_phase_name: Identity, Guardrails, and Offline Development
status: executing
stopped_at: Completed 05-10-PLAN.md
last_updated: "2026-11-14T16:21:54.831Z"
last_activity: 2026-11-14
last_activity_desc: Phase 05 execution started
state_head: 4f227a881097228de483b276eee415edfc883cc0
progress:
  total_phases: 11
  completed_phases: 3
  total_plans: 45
  completed_plans: 42
  percent: 27
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-10-21)

**Core value:** Design, observe, and change Azure from one workspace — canvas fidelity, live tenant truth, and gated execution are all first-class.
**Current focus:** Phase 05 — Identity, Guardrails, and Offline Development

## Current Position

Phase: 05 (Identity, Guardrails, and Offline Development) — EXECUTING
Plan: 11 of 11
Status: Ready to execute
Last activity: 2026-11-14 — Phase 05 execution started

Progress: [███░░░░░░░] 27%

## Performance Metrics

**Velocity:**
- Total plans completed: 27
- Average duration: 0 min
- Total execution time: 0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 1 | 8 | - | - |
| 03 | 6 | - | - |
| 04 | 13 | - | - |

**Recent Trend:**
- Last 5 plans: none
- Trend: n/a

**Per-Plan Metrics:**

| Plan | Duration | Tasks | Files |
|------|----------|-------|-------|
| Phase 01 P06 | 9min | 3 tasks | 1 files |
| Phase 01 P07 | 9 | 3 tasks | 1 files |
| Phase 03 P01 | 8 min | 2 tasks | 7 files |
| Phase 03 P02 | 15min | 2 tasks | 5 files |
| Phase 03 P03 | 20min | 3 tasks | 7 files |
| Phase 03 P04 | 15min | 3 tasks | 4 files |
| Phase 03 P05 | 10min | 3 tasks | 1 files |
| Phase 03 P06 | 10min | 2 tasks | 2 files |
| Phase 04 P01 | 25min | 2 tasks | 14 files |
| Phase 04 P02 | 30min | 3 tasks | 10 files |
| Phase 04 P03 | 15min | 3 tasks | 11 files |
| Phase 04 P04 | 25min | 3 tasks | 13 files |
| Phase 04 P05 | 40min | 3 tasks | 9 files |
| Phase 04 P06 | 95min | 3 tasks | 10 files |
| Phase 04 P07 | 30min | 2 tasks | 4 files |
| Phase 04 P08 | 40min | 3 tasks | 11 files |
| Phase 04 P09 | 30min | 3 tasks | 5 files |
| Phase 04 P10 | 25min | 3 tasks | 4 files |
| Phase 04 P11 | 35min | 3 tasks | 10 files |
| Phase 04 P12 | 45min | 3 tasks | 3 files |
| Phase 04 P13 | n/a (human checkpoints) | 4 tasks | 0 files |
| Phase 05 P01 | 25min | 2 tasks | 11 files |
| Phase 05 P02 | 35min | 3 tasks | 10 files |
| Phase 05 P03 | 20min | 2 tasks | 3 files |
| Phase 05 P04 | 45min | 3 tasks | 14 files |
| Phase 05 P05 | 25 min | 2 tasks | 6 files |
| Phase 05 P06 | 35min | 3 tasks | 8 files |
| Phase 05 P07 | 35 min | 3 tasks | 10 files |
| Phase 05 P08 | 40min | 3 tasks | 12 files |
| Phase 05 P09 | 35min | 3 tasks | 4 files |
| Phase 05 P10 | 40min | 3 tasks | 8 files |

## Accumulated Context

### Decisions

- 2026-10-21: Phase order is land → reproducible build → server harness → data durability → identity/guardrails → bounds/payload → shell → verification/diagnostics → credential model → post-gate capabilities → signed MSIX. Shell starts in parallel with Phase 2.
- 2026-10-21: Publisher legal name and signing path are decided in Phase 1; the signed artifact is produced in Phase 11 so certificate procurement is not on the critical path.
- 2026-10-21: Data durability precedes guardrail tenant scoping — the tenant column needs the migration baseline, not a fourth hand-written DDL statement.
- 2026-10-21: Post-gate capabilities (POST) stay after the Trust Gate closes; no new UI surface before then.
- [Phase 01]: Pushed pre-land tip e9078d1 by fast-forward refspec e9078d1:refs/heads/main first, then main (3127ba3), verifying ls-remote equality after each; no force, lease, or rebase (D-10/D-12).
- [Phase 01]: Recovery bundle AzureDesignStudio-3127ba3-20261021.bundle (99616738 bytes) cut at parent directory and clone-verified to restore 3127ba3 with a clean tree; retained for plan 01-08's re-cut and deletion (D-11).
- [Phase 01]: SC#2 recorded as clone HEAD == recorded BUNDLE_SHA plus zero non-.planning unpushed paths; no live-head equality; DOM probe true so SC#4 primary proof is the 01-02 harness.
- [Phase 03]: AutoMapper 12.0.1 advisories GHSA-gpwp-5797-mjff and GHSA-6vhm-j9h7-f9gh suppressed under the single 2027-01-31 expiry; allowlist stays AutoMapper-only
- [Phase 03]: Microsoft.AspNetCore.Mvc.Testing pinned at 10.0.15 (not the roadmap's stale 10.0.13)
- [Phase 03]: Server host harness runs on Database:Provider=InMemory via UseSetting and environment Testing until 03-03 moves the database choice to configuration
- [Phase 03]: Default Database:Provider stays SqlServer so a forgotten setting fails fast
- [Phase 03]: Legacy DDL guard is IsSqlServer, never IsRelational (T-SQL on SQLite fails)
- [Phase 03]: Development zero-setup is configuration (Database__Provider=InMemory in launch profiles), not compilation
- [Phase 03]: Crypto:Provider accepts only KeyVault until Phase 5 (CRED-01/02)
- [Phase 03]: 03-04: DesignDbContextFactory targets SQL Server on LocalDB with integrated security so the design-time model carries ADS_CHECK_JSON; never opened, no Design package added (Phase 4)
- [Phase 03]: 03-05: no production change needed; handler faults map to StatusCode.Unknown and real CryptoService answers 503 in-band without a vault
- [Phase 03]: 03-06: source guards match call shape and line-start directives; test-auth text banned from the production Server tree
- [Phase 04]: 04-01: dotnet-ef 10.0.13 pinned in a root tool manifest; EF Design 10.0.12 central, PrivateAssets=all in Workspace and Server only
- [Phase 04]: 04-01: legacy workspace fixtures are plain SQL built from shipped DDL; schema parity compared via pragmas, never sqlite_master text; fixture loader refuses non-temp targets
- [Phase 04]: 04-02: workspace baseline migration is guarded SQL derived from EF's generated DDL; legacy adoption adds the Scans columns before it; Migrate bounded to 30s with lock-recovery message
- [Phase 04]: 04-03: SQL Server migrations live in the Server project (SqlServerDesignDbContext); baseline Up is guarded T-SQL; Database:AllowCreate defaults to false and Database:Migrate=false with pending migrations refuses to start
- [Phase 04]: Reader ladder descends only on SQLite codes 8 and 14; corrupt or foreign files fail loudly
- [Phase 04]: synchronous stays FULL, journal_mode never set per open; one shared PragmaInterceptor per profile
- [Phase 04]: Immutable rung refused when a non-empty -wal exists
- [Phase 04]: 04-05: backup free-space probe is an internal CreateAsync overload parameter (not a mutable static) so parallel tests cannot interfere
- [Phase 04]: 04-05: integrity check classifies SQLite codes 3,5,6,7,8,10,13,14 as Inconclusive, never Corrupt; live file checked read-write, snapshots read-only
- [Phase 04]: 04-05: .gitignore un-ignores src/AzureDesignStudio.Workspace/Backup/ (VS template ignores Backup*/)
- [Phase 04]: 04-06: rollback is the rehearsed BACKUP/RESTORE sequence (a test runs it); Down() stays empty; a generated PK name is compared as PK_<table> and generated names match {8,16} hex digits
- [Phase 04]: 04-07: premigration backup lives in WorkspaceSchema.UpgradeAsync (after the pending-migration read, before legacy adoption), so every entry point including new WorkspaceDb(path) shares it; a backup failure aborts the upgrade
- [Phase 04]: 04-07: startup quarantine moves main+wal+shm to workspace.corrupt-<UTC>.db[-wal|-shm] (undo on partial failure); Inconclusive integrity results throw and move nothing
- [Phase 04]: 04-08: deploy.db backups live only in backups/deploy with stem deploy (user-only, newest 3), invisible to the workspace backup list; the SQLite migration set sits in provider-free Services with its design-time factory in the Server
- [Phase 04]: 04-08: Migrate call-site guard ignores DeployDbStartup.Migrate(...) via lookbehind; Server.Tests compiles Desktop/DeployDbStartup.cs by a linked Compile item
- [Phase 04]: 04-09: restore is staged into restore-staging.new and swapped over restore-staging only after acceptance, so a rejected file keeps an earlier pending restore
- [Phase 04]: 04-09: a stale -wal/-shm that cannot be deleted at the restore target throws instead of being swallowed (a surviving log could be replayed onto the restored file)
- [Phase 04]: 04-09: scheduled backup is due when the newest scheduled/premigration backup is more than 24 h old; manual backups never count and are never pruned
- [Phase 04]: 04-10: unique-index guard runs after the premigration backup (required argument), keeps newest per key, writes a jsonl report before one-transaction delete; abandoned __EFMigrationsLock stays manual-recovery
- [Phase 04]: 04-11: a WorkspaceUpgradeException or deploy.db InvalidOperationException at launch sets MauiProgram.StartupFailure and App.CreateWindow shows the native StartupFailurePage before touching any service
- [Phase 04]: 04-11: ADS_DATA_DIR redirects the Desktop data root (workspace.db, deploy.db, backups) for testing only; VerifyDesktopWorkspace.sh polls workspace.db with a normal connection because -readonly cannot open a WAL database without an -shm file
- [Phase 04]: 04-12: the runbook documents the real gaps (deploy.db migration not time-bounded; only a failed deploy.db premigration backup reaches the failure page) instead of describing intended behavior
- [Phase 04]: 04-12: DatabaseRunbookTests guards the runbook (required statements, cited src/ paths, no credentials) and the AGENTS.md link
- [Phase 05]: AdsIdentity lives in Services (no ASP.NET Core type); scope key = tid, or the user's oid for the personal-account tenant (D-11, D-17)
- [Phase 05]: Single-user host marker is set only by InProcessServerCallContext via UserState, counted only as boolean true (D-04)
- [Phase 05]: 05-02: one ServiceTools.IdentityInvalidMessage text; responses without an error_message field carry the 401 status only (no proto change)
- [Phase 05]: 05-02: the caller is resolved before request validation in every RPC, including WhatIf, CreateDeployment, GetStack and the shared stack preparation
- [Phase 05]: 05-03: Cost.GetRetailPrices is the only anonymous gRPC method (literal list in the test); FallbackPolicy requires an authenticated user; mapping helper unit-tested because Hosted [Authorize] attributes mask it on the real host
- [Phase 05]: 05-04: guardrail policy rows keyed by caller.ScopeId; the NULL-tenant Id=1 row is the inherited default (replace semantics), never written by an RPC; an own row with an empty body is the empty policy
- [Phase 05]: 05-04: TenantGuardrails SQL Server Up is five guarded commands (column, filtered unique index, two tables, events index) with no default constraint; proved in the mssql 2022 container (9 passed, 0 skipped)
- [Phase 05]: 05-05: development crypto key-ring folder is created lazily on first use (0700 on Unix, DPAPI keys on Windows); an existing folder is never chmod-ed
- [Phase 05]: 05-05: Crypto:Provider=Development is refused in Production before anything registers; dev1: prefix isolates development ciphertext from Key Vault blobs
- [Phase 05]: 05-06: a gate token issued without a scope is Stale for any scoped consume and invisible to the scope-aware peek (fail closed); guardrail audit event is added inside UpsertGuardrailRowAsync so the retry path re-adds exactly one
- [Phase 05]: 05-07: tenant settings Set replaces both values; GetGuardrailPolicy keeps replace semantics while only enforcement (LoadGuardrailPolicy) honors the per-tenant mode; unrecognised stored mode is judged as merge; interpretation of 'Option 1 and option 2' (both modes selectable, replace default) still to confirm
- [Phase 05]: 05-08: approval backfill to the empty-GUID scope is an UPDATE, never a column default; pending legacy approvals fail closed and must be re-approved
- [Phase 05]: 05-08: plan and decommission approvals keyed by (TenantId, hash); SQL Server key rebuild proven in the container (11 passed, 0 skipped)
- [Phase 05]: 05-09: role check precedes hash/preview/setting lookups; self-approval is a separate rule with its own code; the recorded requester deploys, a row without one keeps approver-only; decommission approvals need the role with no self-approval rule
- [Phase 05]: 05-10: precache excludes only appsettings*.json; operator guide documents the tenant-settings call recipe (token via ADS_TOKEN) and records the replace/merge reading as INTERPRETATION TO CONFIRM

### Pending Todos

None yet.

### Blockers/Concerns

- Phase 1: publisher legal name and Azure Artifact Signing eligibility are external; start them at phase entry.
- Phase 2: Desktop Release duration is unmeasured; the Windows job starts non-blocking and becomes blocking only after two green runs (BUILD-05).
- Phase 4: no SQL Server legacy-schema fixture exists yet; build it from the current bootstrap DDL before migration 2.
- Phase 6: the what-if token gate is in-process with two replicas; pin replicas to 1 for the pilot or move the gate to the database before load testing.
- Phase 11: packaged data-root behavior is inferred, not verified; spike it before packaging work starts.

## Deferred Items

| Category | Item | Status | Deferred At | Milestone |
|----------|------|--------|-------------|-----------|
| Server & tenancy | SRV-01, SRV-02 | Deferred | 2026-10-21 | v3.1+ |
| IaC & automation | IAC-01, IAC-02, MCP-01 | Deferred | 2026-10-21 | v3.1+ |
| Experience | UX-01, UX-02 | Deferred | 2026-10-21 | v3.1+ |

## Session Continuity

Last session: 2026-11-14T16:21:54.694Z
Stopped at: Completed 05-10-PLAN.md
Resume file: None
