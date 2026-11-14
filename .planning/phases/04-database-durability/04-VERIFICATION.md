---
phase: 04-database-durability
verified: 2026-11-14T18:00:00Z
status: passed
score: 5/5 must-haves verified
covered_files:
  - ".planning/phases/04-database-durability/04-01-PLAN.md"
  - ".planning/phases/04-database-durability/04-01-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-02-PLAN.md"
  - ".planning/phases/04-database-durability/04-02-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-03-PLAN.md"
  - ".planning/phases/04-database-durability/04-03-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-04-PLAN.md"
  - ".planning/phases/04-database-durability/04-04-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-05-PLAN.md"
  - ".planning/phases/04-database-durability/04-05-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-06-PLAN.md"
  - ".planning/phases/04-database-durability/04-06-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-07-PLAN.md"
  - ".planning/phases/04-database-durability/04-07-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-08-PLAN.md"
  - ".planning/phases/04-database-durability/04-08-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-09-PLAN.md"
  - ".planning/phases/04-database-durability/04-09-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-10-PLAN.md"
  - ".planning/phases/04-database-durability/04-10-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-11-PLAN.md"
  - ".planning/phases/04-database-durability/04-11-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-12-PLAN.md"
  - ".planning/phases/04-database-durability/04-12-SUMMARY.md"
  - ".planning/phases/04-database-durability/04-13-PLAN.md"
  - ".planning/phases/04-database-durability/04-13-SUMMARY.md"
  - "docs/database-operations.md"
  - "src/AzureDesignStudio.Desktop/MauiProgram.cs"
  - "src/AzureDesignStudio.Mcp/AdsWorkspace.cs"
  - "src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs"
  - "src/AzureDesignStudio.Server/Persistence/Migrations/20261114120545_Baseline.cs"
  - "src/AzureDesignStudio.Workspace/Backup/BackupRetention.cs"
  - "src/AzureDesignStudio.Workspace/Backup/RestoreStaging.cs"
  - "src/AzureDesignStudio.Workspace/Backup/WorkspaceBackup.cs"
  - "src/AzureDesignStudio.Workspace/Integrity/WorkspaceIntegrity.cs"
  - "src/AzureDesignStudio.Workspace/Migrations/20261114120047_Baseline.cs"
  - "src/AzureDesignStudio.Workspace/UniqueIndexGuard.cs"
  - "src/AzureDesignStudio.Workspace/WorkspaceConnection.cs"
  - "src/AzureDesignStudio.Workspace/WorkspaceSchema.cs"
  - "src/AzureDesignStudio.Workspace/WorkspaceStartup.cs"
covered_digest: "v4:sha256:481897f93ed5b2b42914dca601f9967c6142b43c6b1d7fb40d8df48ed44d3922"
behavior_unverified: 0
overrides_applied: 0
---

# Phase 4: Database Durability Verification Report

**Phase Goal:** Both databases get one schema authority: existing installations upgrade without losing a row, SQLite survives concurrent read/write, corruption is visible, and a user can restore a real backup.
**Verified:** 2026-11-14
**Status:** passed (with two honestly recorded open items, below; neither is a must-have failure)
**Re-verification:** No, initial verification

## Goal Achievement

I did not rely on the SUMMARY files. I read the production code and ran every automated gate myself on the finished tree (HEAD `6fc5c18`, which includes the eight review fixes WR-01..WR-08).

### Observable Truths (ROADMAP success criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | An existing `workspace.db` upgrades through EF migrations with rows intact, and a fresh and an upgraded database have the same schema | VERIFIED | `WorkspaceSchema.UpgradeAsync` plans pending migrations, takes a premigration `VACUUM INTO` backup, then `MigrateAsync` runs the guarded-SQL `20261114120047_Baseline` (legacy files get a history row). `WorkspaceUpgradeTests` passed: `LegacyL3_UpgradesKeepingEveryRow_AndMatchesAFreshWorkspace` asserts identical table counts before and after, one `Baseline` history row, and `SqliteSchemaDescriber.Describe(fresh) == Describe(upgraded)`; L1 and L2 fixtures (older shapes) also keep rows; `WorkspaceMigrationModelTests` (`PendingModelChanges_AreNone`, `EveryBaselineStatementIsGuarded`) passed. Legacy fixtures L1..L4 are checked-in SQL built from the old bootstrap DDL. `EnsureCreated` no longer exists in production Workspace code (grep: only test-only call sites and the Server bootstrapper's Sqlite/InMemory dev branch, which `SourceGuardTests` pins). |
| 2 | The server DB upgrades through EF migrations and a rehearsed, documented rollback restores the prior state | VERIFIED | `DatabaseBootstrapper.MigrateSqlServer` calls `Migrate()` with `Database:Migrate` / `Database:AllowCreate` switches and refuses a missing DB or pending migrations as configured. I ran `bash src/RunSqlServerTests.sh` against a real SQL Server 2022 container: **8 passed, 0 failed, 0 skipped**; the container was removed (no `ads-sqltest-*` left in `docker ps -a`). Includes `LegacyUpgrade_KeepsRowsAndWritesHistory`, `LegacyUpgrade_MatchesAFreshBaselineDatabase`, `FreshBaseline_MatchesAModelCreatedDatabase`, `ConcurrentMigrate_ThreeCallers_AllSucceed`, and `BackupMigrateRestore_ReturnsToThePreUpgradeState` (BACKUP with CHECKSUM, VERIFYONLY, migrate, write a row, RESTORE WITH REPLACE; asserts no history table, original row counts, legacy default back, and that it migrates again). Rollback is documented in `docs/database-operations.md` ("Rollback", "Upgrade procedure", "Backup"), states that `Down()` is not the supported path, and says plainly the Azure SQL variant is not rehearsed. `DatabaseRunbookTests` passed. |
| 3 | A concurrent scan write and MCP read finish without BUSY/LOCKED, with WAL, `busy_timeout`, `foreign_keys` and per-connection pragmas in force | VERIFIED | `WorkspaceConnection` is the one factory: no `Cache=Shared`, `PragmaInterceptor` applies `busy_timeout=5000`, `foreign_keys=ON`, `journal_size_limit` on every open (also pooled), WAL asserted once in `UpgradeAsync` (throws if the mode is not `wal`). `WorkspaceConcurrencyTests.WritersAndReadersRunWithoutBusyOrLocked` passed in the full Core run: two scan-page writers, two governance writers, two in-process readers and a real second process (`ads-mcp --self-check`) for 15 s, asserting an empty error bag, `errors=0` and reads greater than 0 in the child. `WorkspaceConnectionTests.PooledConnectionsCarryThePragmas_...` passed. |
| 4 | Startup `quick_check` surfaces a corrupted workspace instead of opening it; a user can take a `VACUUM INTO` backup and restore it with data verified | VERIFIED | `WorkspaceStartup.OpenAsync` runs `WorkspaceIntegrity.Check(Quick, LiveFile)`; `Corrupt` quarantines the file (never deleted) and returns `RecoveredFromCorruption` with a `WorkspaceNotice` naming the quarantine path and backups (the CONTEXT decision); `Inconclusive` throws and moves nothing. Tests passed: truncated, header-zeroed, plain-text, zero-byte-with-WAL files are quarantined; a healthy file is opened untouched. Backup is `VACUUM INTO` with a bound parameter, manifest and SHA-256; `BackupOfALiveWalWorkspace_ContainsUncheckpointedRows_AndIsVerifiedHashedAndListed` passed. Restore is staged, verified at `integrity_check` depth (rejects triggers, views, unknown tables, newer schema, manifest hash/row-count mismatch, truncated or non-database files) and applied before any connection at next launch: `AStagedBackup_IsRestoredAtTheNextStart_...` asserts 3 designs back (the 4th, only in the live WAL, is gone), full integrity OK, WAL mode, and the old workspace kept as `workspace.pre-restore-<UTC>.db` with its 4 designs. Desktop wiring is in `MauiProgram` (startup via `WorkspaceStartup`, `WorkspaceBackupService` 24 h loop, failure page); the user approved the restore drill and the Mac Catalyst corrupt launch in 04-13. |
| 5 | MCP opens WAL read-only (or reports the `ReadWrite + query_only` fallback) and passes a cold-workspace test; the baseline adopts existing DDL; duplicate rows are reported or deduped; `__EFMigrationsLock` recovery is documented | VERIFIED | `WorkspaceConnection.OpenReader` ladder: `ReadOnly`, then `ReadWriteQueryOnly`, then `Immutable` (only with no live WAL), descending only on permission-shaped failures; `AdsWorkspace.OpenMode`/`OpenDetail` expose the rung, and `AdsWorkspace` does not use `WorkspaceDb` (no schema writes). `AdsWorkspaceOpenTests` passed (fallback on code 8, corrupt file does not descend, missing file never created, query-only rung can read but not write, read-only folder gives immutable). `WorkspaceConnectionTests.PooledConnectionsCarryThePragmas_AndAColdWorkspaceOpensReadOnly` is the cold-workspace test and passed. Baseline adoption: `ModelBuilt_FileAdoptsTheBaseline_WithoutLosingARow`. Dedupe: `UniqueIndexGuardTests` passed (backup first, keep newest by instant via `julianday`, removed rows written to a report, fresh schema reached, rollback on delete failure). Lock: `MigrationLockTests` passed (abandoned lock fails within a size-scaled bound naming `DELETE FROM "__EFMigrationsLock";`, manual recovery lets the next upgrade succeed, caller cancellation is not reported as a stuck lock); recovery is in the runbook and `DatabaseRunbookTests.Runbook_QuotesTheLockStatementThatTheExceptionMessagePrints` pins the statement. |

**Score:** 5/5 truths verified (0 present-but-behavior-unverified)

### Automated gates I ran

| Command | Result |
|---------|--------|
| `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` | Passed 1799, Failed 0, Skipped 0 |
| `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` | Passed 208, Failed 0, Skipped 8 (the 8 SqlServer container tests, run in the next row) |
| `bash src/RunSqlServerTests.sh` (Docker, SQL Server 2022 container) | Passed 8, Failed 0, Skipped 0; container removed |
| `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst` | 0 errors, 1 warning (`PublishFolderType`, pre-existing) |
| `git status --short` after all of the above | clean; no `packages.lock.json` SDK drift, so there was nothing to revert |

I did not launch the Desktop app and touched no user data.

### Key Link Verification

| From | To | Via | Status |
|------|----|-----|--------|
| `MauiProgram` | `WorkspaceStartup.Open` | direct call, failures routed via `IsStartupFailure` to `StartupFailurePage` | WIRED |
| `MauiProgram` | `WorkspaceBackupService.RunLoopAsync` | started only when no startup failure | WIRED |
| `MauiProgram` | `DeployDbStartup.Migrate` (deploy.db) | scope over `DesignDbContext`, private backup in `backups/deploy` | WIRED |
| `WorkspaceStartup` | `RestoreStaging.ApplyPendingAsync` (before any connection), `WorkspaceIntegrity.Check`, `WorkspaceSchema.UpgradeAsync` | sequential in `OpenAsync` | WIRED |
| `WorkspaceSchema.UpgradeAsync` | `WorkspaceBackup.CreateAsync` then `UniqueIndexGuard.Repair` then `MigrateAsync` | backup before any mutation; failure to back up aborts the upgrade | WIRED |
| `AdsWorkspace` | `WorkspaceConnection.OpenReader` / `ReaderOptions` | reader ladder plus pragma interceptor | WIRED |
| Server `Program` | `DatabaseBootstrapper.Initialize` | single schema site, guarded by `SourceGuardTests` | WIRED |
| `.github/workflows/build.yml` `sqlserver` job | `Category=SqlServer` tests with `ADS_TEST_SQLSERVER` | present in the workflow; not yet run on GitHub (see open items) | WIRED, UNRUN |

### Requirements Coverage

Every ID in the phase's PLAN frontmatter is accounted for; the union across plans is exactly DATA-01, 02, 03, 04, 05, 06, 08 (the ROADMAP list). DATA-07 is mapped to Phase 11 in REQUIREMENTS.md and correctly is not claimed. No orphaned requirement maps to Phase 4.

| Requirement | Plans | Status | Evidence |
|-------------|-------|--------|----------|
| DATA-01 | 04-01, 04-02, 04-03 (SQL Server parity), 04-07 | SATISFIED | Truth 1; `WorkspaceUpgradeTests`, premigration backup, `WorkspaceStartupTests.ALegacyFile_IsUpgraded_AfterExactlyOneVerifiedPremigrationBackup` |
| DATA-02 | 04-01, 04-02, 04-03, 04-06, 04-08, 04-12 | SATISFIED | Truth 2; 8/8 container tests; `deploy.db` also under migrations (`DeployDbMigrationTests`, passed in the Server run); rollback runbook |
| DATA-03 | 04-04 | SATISFIED | Truth 3; `WorkspaceConcurrencyTests`, `WorkspaceConnectionTests` |
| DATA-04 | 04-05, 04-07, 04-09, 04-11 | SATISFIED | Truth 4; `WorkspaceIntegrityTests`, `WorkspaceStartupTests` |
| DATA-05 | 04-05, 04-09, 04-10, 04-11 | SATISFIED | Truth 4; `WorkspaceBackupTests`, `WorkspaceRestoreTests`, `WorkspaceBackupServiceTests`, `BackupRetentionTests`; human restore drill approved |
| DATA-06 | 04-04, 04-06 | SATISFIED | Truth 5; `AdsWorkspaceOpenTests`, cold-workspace test, `ads-mcp --self-check` exercised by the concurrency test |
| DATA-08 | 04-02, 04-10, 04-12 | SATISFIED | Truth 5; `ModelBuilt_FileAdoptsTheBaseline_...`, `UniqueIndexGuardTests`, `MigrationLockTests`, runbook sections "Migration lock recovery" and "Removed duplicate rows" |

REQUIREMENTS.md still shows these as `Pending`; the orchestrator closes them after this verification.

### CONTEXT decisions honored

Manual and automatic backups (both present: drawer plus 24 h service); quarantine plus fresh workspace plus notice naming the path and backups; back up first, keep newest, report removed rows; SQL Server rollback is backup/restore, rehearsed, with `Down()` not the supported path; baseline as guarded SQL; no `Cache=Shared`; staged restore at next launch; manual backups exempt from pruning; `deploy.db` included as a separable wave; checked-in legacy fixtures. One deliberate narrowing is documented in `WorkspaceIntegrity`: the CONTEXT line "treat any exception from quick_check as corruption" became "only SQLite codes 11, 24, 26 are corruption, anything else is inconclusive and moves nothing". That is safer than the original decision and matches its intent (a second process must never cause quarantine of a healthy file).

### Review-fix judgement (04-REVIEW.md WR-01..WR-08)

All eight fixes are present in the code, each has a test, and the whole suite is green on top of them.

- WR-01: `IntegrityCodes.IsCorrupt => 11 or 24 or 26` (allow-list). Correct; `TheClassificationTable_CoversExactlyTheThreeCorruptionCodes` guards it. One residual judgment: a corrupt file reported under another primary code now stops startup ("nothing was moved") instead of quarantining. That fails safe (no data movement), so acceptable.
- WR-02: `KeepNewestWithData` (default 2) keeps the newest two data-bearing backups beyond the newest-5 rule. Sound and tested. Policy trade-off (two extra files may persist) is documented.
- WR-03: `IsStartupFailure` routes IO, DB and upgrade errors to the failure page for both startup blocks; restore `chmod` is guarded and WAL/staging cleanup is in `finally`. Test added. The `MauiProgram` filter itself has only build-level coverage (Desktop is not test-referenced).
- WR-04: restoring the live `workspace.db` or a file with a non-empty `-wal` sidecar is rejected; tested.
- WR-05: keep-newest orders by `julianday(...)` then text then rowid; tested with mixed offsets.
- WR-06: `RemovedRowsMessage` is shown at launch (`MainPage.OnLoaded`); the message is unit-tested, the alert wiring is covered by the Mac Catalyst build only. The dedupe still commits before the migration (a later migration failure leaves rows removed); the failure text now says so and points at the report and backup. Acceptable, and recoverable from the premigration backup.
- WR-07: unique temp names, database moved before manifest, orphan manifests pruned. The review-fix report itself notes the concurrency test passes against the old code too (timing dependent), so it is a regression guard, not proof of the fix; the orphan-manifest test does fail without the fix.
- WR-08: the migration bound scales with file size (30 s + 1 s/MiB) and the lock-deletion advice appears only when a lock row exists. `SecondsPerMegabyte = 1` is an estimate, not measured against a real data migration.

IN-01..IN-05 (info) remain open by design: unencrypted live databases (the project notes plaintext at rest), unbounded quarantine/pre-restore accumulation, no `permissions:` block on the `sqlserver` CI job, the `query_only` rung can still create `-wal`/`-shm` files in the folder, and `Exists()` false-negative risk on SQL Server. None contradicts a success criterion.

### Anti-Patterns Found

No unreferenced `TBD`/`FIXME`/`XXX` markers were found in the phase's changed files; no stub returns in the delivered paths. Test code uses `EnsureCreated` only as a documented test-only shortcut. No blockers.

### Human Verification

Not outstanding. The three manual-only checks (Mac Catalyst L2 upgrade launch and corrupt-file launch, restore drill from the Desktop drawer, runbook read-through) were approved by the user in plan 04-13 (a single combined "Approved - all three passed"; per-step observations were not itemised, as 04-13-SUMMARY states). No `<human-check>` blocks exist in any PLAN.

### Open items (honestly recorded, non-blocking)

1. **GitHub-hosted `sqlserver` CI job has not run.** The branch `feat/wave-b2-b4-governance-preflight` is unpushed. The job is defined in `.github/workflows/build.yml`, and the identical test set passed locally against a real SQL Server 2022 container (8/8, via Docker under amd64 emulation). Push the branch and confirm the job is green to close the phase gate line fully.
2. **The Windows Desktop target (`net10.0-windows10.0.19041.0`) was not compiled on this macOS machine.** Only the Mac Catalyst build was run (0 errors). The Phase 4 Desktop changes (`MauiProgram`, `MainPage`, the data drawer) are shared code, but Windows-only branches (`#if WINDOWS`, `Platforms/Windows`) and the Windows lock file have not been exercised. The Azure SQL rollback variant is documented but, by the runbook's own statement, not rehearsed.

### Gaps Summary

None. Every ROADMAP success criterion is backed by code I read and by tests I ran green (1799 Core, 208 Server plus 8 container tests, Mac Catalyst build clean). The phase goal is achieved for the SQLite workspace, deploy.db, and the SQL Server database, subject only to the two open items above.

---

_Verified: 2026-11-14_
_Verifier: Claude (gsd-verifier)_
