---
phase: 04-database-durability
plan: 09
subsystem: database
tags: [sqlite, restore, backup, staging, wal, untrusted-input, scheduler]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-05 WorkspaceBackup/BackupManifest/BackupRetention/WorkspaceIntegrity, 04-07 WorkspaceStartup state machine
provides:
  - RestoreStaging (VerifyAsync, StageAsync, GetPending, CancelPending, ApplyPendingAsync) with RestoreVerification, PendingRestore, RestoreOutcome
  - WorkspaceStartup step 0 that applies a pending restore before the integrity check and upgrade; WorkspaceStartupReport.Restore
  - WorkspaceBackupService (CreateManualAsync, List, RunScheduledIfDueAsync, RunLoopAsync, StageRestoreAsync, PendingRestore, CancelPendingRestore)
affects: [04-10, 04-11, 04-12, 04-13]

plan_head_before: 7b74227de90dd8030008feb2fc1fbf890ae1f901
plan_head_after: ccdfd76c3c23185bb229a39cb428bfd267d6b08a

actuals:
  tokens: 14400   # chars/4 over the 5-file diff (git diff 7b74227..HEAD, 57,643 chars)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Restore = stage + verify the COPY + SHA-256 marker, apply at the next launch before any connection; the live file and its sidecars move to workspace.pre-restore-<UTC>.db, never deleted"
    - "Candidate schema allow-list read from the WorkspaceDbContext model and Database.GetMigrations(), checked on a ReadOnly, unpooled connection with trusted_schema off"
    - "Staging happens in restore-staging.new and replaces restore-staging only after acceptance, so a rejected file never disturbs an earlier pending restore"

key-files:
  created:
    - src/AzureDesignStudio.Workspace/Backup/RestoreStaging.cs
    - src/AzureDesignStudio.Workspace/Backup/WorkspaceBackupService.cs
    - src/AzureDesignStudio.Core.Tests/WorkspaceRestoreTests.cs
    - src/AzureDesignStudio.Core.Tests/WorkspaceBackupServiceTests.cs
  modified:
    - src/AzureDesignStudio.Workspace/WorkspaceStartup.cs

key-decisions:
  - "StageAsync stages into restore-staging.new and swaps it over restore-staging only when the candidate is accepted; a rejected candidate leaves no folder and keeps an earlier pending restore"
  - "Restore reasons are collected, not first-fail; an accepted candidate with no sidecar returns Accepted with an informational note in Reasons"
  - "A stale -wal/-shm that cannot be deleted at the restore target throws instead of being swallowed, because a surviving log could be replayed onto the restored file"
  - "Names read from the candidate (triggers, tables, migration ids, manifest errors) are control-character-stripped and truncated before they reach a message"
  - "The pre-restore file name uses the literal workspace stem like the corrupt-quarantine files; collisions get -1, -2 suffixes"
  - "The scheduled-due rule is strictly more than 24 h (a backup exactly 24 h old is not yet due); a manual backup never counts as recent"
  - "RunLoopAsync(ct) delegates to an internal overload taking the period, so loop tests use 20 ms instead of 24 real hours"

patterns-established:
  - "Loop errors go to an optional Action<Exception>; prune problems are routed to the same callback as IOException"

requirements-completed: []  # DATA-05 is closed by the orchestrator at phase completion

duration: 30min
completed: 2026-11-14
---

# Phase 04 Plan 09: Staged verified restore and the backup service Summary

**A backup chosen for restore is copied to a staging folder, verified there (header, `integrity_check`, no triggers/views/virtual tables, only model tables, only known migration ids, manifest SHA-256 and row counts) and applied at the next launch before any connection opens, with the previous workspace moved to `workspace.pre-restore-<UTC>.db` and no stale WAL replayed; `WorkspaceBackupService` fronts manual backups, the 24-hour schedule with pruning and the restore for the Desktop host.**

## Accomplishments

- `RestoreStaging.StageAsync` copies the picked file (and its `<picked>.manifest.json` sidecar) into a private (0700/0600 on Unix) staging folder, verifies the COPY and writes `restore.pending.json` (source name, staged UTC, SHA-256) only on acceptance. `ApplyPendingAsync` re-hashes the candidate, `ClearAllPools`, moves the live `.db`/`-wal`/`-shm` aside, moves the candidate in, deletes any `-wal`/`-shm` left at the target, and removes the marker and staging folder. A failed move undoes the moves already made and throws `WorkspaceUpgradeException` telling the user to close `ads-mcp` and any other user of the file; the restore stays pending.
- `WorkspaceStartup.OpenAsync` runs the apply step first; the existing steps then treat the restored file like any other: integrity check, premigration backup of an older candidate, migration, WAL assertion. `WorkspaceStartupReport.Restore` (optional last parameter) carries the `RestoreOutcome`.
- Verification rules, all collected: file exists and non-empty, SQLite header, `Full` integrity; then on a `Mode=ReadOnly;Pooling=False` connection with `trusted_schema=OFF` no `trigger`, no `view`, no `CREATE VIRTUAL TABLE`, no table outside the model (+ `__EFMigrationsHistory`, `__EFMigrationsLock`, `sqlite_sequence`, `sqlite_stat1`), and every `MigrationId` known to `Database.GetMigrations()` (otherwise "comes from a newer version of the app"). A file with no history table (legacy L2) is accepted and migrated forward by startup. With a sidecar: SHA-256 and every per-table row count must match; no sidecar is an informational note.
- `WorkspaceBackupService`: `CreateManualAsync`, `List` (workspace stem only, so `deploy` backups in the same folder are ignored), `RunScheduledIfDueAsync` (due when no scheduled/premigration backup is newer than 24 h; backs up, prunes with the default retention, then an opportunistic `wal_checkpoint(TRUNCATE)` whose failures are swallowed), `RunLoopAsync` (due check at once, then a `PeriodicTimer` every 24 h; tick exceptions go to `onError`, cancellation ends it quietly), and the restore facade.

## Task Commits

1. **Task 1 (tracer): staged restore of a verified backup, applied before the workspace opens** - `a5d2a9b` (feat)
2. **Task 2: verify restore candidates against triggers, views, unknown tables, newer schemas and manifests** - `77d16e2` (feat)
3. **Task 3: backup service with a 24 hour schedule, pruning and a restore facade** - `ccdfd76` (feat)

## Verification

- Tracer gate (re-run before Task 2): `WorkspaceRestoreTests` 2 passed; `WorkspaceStartupTests` 12 passed, so the new step 0 changes nothing when no restore is pending. The tracer test holds a read transaction open so the live `-wal` is provably non-empty (4th design in it), stages a 3-design backup, restarts and finds 3 designs live, 4 in the kept `workspace.pre-restore-20261114T123045Z.db`, `journal_mode` `wal`, `Full` integrity ok, staging gone.
- Task 2: `WorkspaceRestoreTests` 18 passed (12 required). Written first; the 8 rule tests (trigger, view, virtual table, unknown table, newer schema, manifest row count, manifest SHA-256, no-sidecar note) failed against the tracer, then passed once the rules were added. Header-valid-but-damaged (0xFF page), truncated, text and zero-byte candidates, the legacy L2 candidate (accepted, then `Upgraded` with a premigration backup and a `GovernanceFindings` table), the post-staging tamper (discarded, live file byte-identical, no pre-restore file) and `CancelPending` are covered.
- Task 3: `WorkspaceBackupServiceTests` 9 passed (6 required): manual backup survives nine scheduled prunes; due/skip/due around 24 h; a premigration backup counts as recent while a manual one does not; seven scheduled backups leave five scheduled + manual + premigration within 30 days and delete the 40-day one with its manifest; loop runs once then waits and ends cleanly on cancel; a failing tick (a file where the backups folder should be) reports at least three errors and the loop keeps running; facade stage/pending/cancel; `deploy` backups are not listed.
- Full offline suite `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1756 passed, 0 failed, 0 skipped (1729 before this plan).
- Acceptance greps: `ApplyPendingAsync` in WorkspaceStartup.cs 1; `File|Directory.Delete` in RestoreStaging.cs 2 (staging folder, and the `-wal`/`-shm` sidecars at the target); `pre-restore` present; `trusted_schema` 2; `newer` 2; `Fact|Theory` in WorkspaceRestoreTests.cs 18; `PeriodicTimer` 1; `wal_checkpoint` 1; no `packages.lock.json` modified.
- Measured commits: `git rev-list --count 7b74227..HEAD` = 3.

## Deviations from Plan

### Auto-fixed Issues

None - plan executed as written. Judgment calls inside the plan's latitude are listed under key-decisions (staging via `restore-staging.new`, stripped/truncated names in messages, throwing on an undeletable stale sidecar, strict `>` 24 h).

### TDD note

Task 3 is marked `tdd="true"`; the test file and the service were written in consecutive steps and the first test run was the green one, so no separate red run was recorded for it (the tests would not compile without the service). Task 2's red run was recorded (8 failures).

## Known Stubs

None.

## Threat Flags

None. The new surface is the restore path the plan's threat model covers (T-04-31 to T-04-35); no network endpoints, auth paths or package were added.

## Issues Encountered

- Row-count comparison first failed on `sqlite_sequence` (the manifest from `WorkspaceBackup` counts everything except `sqlite_*` tables); the candidate count now uses the same set.

## Next Phase Readiness

04-10/04-11 can wire the Desktop host: call `new WorkspaceBackupService(workspacePath, backupsDirectory, onError: ...)`, start `RunLoopAsync` after startup, use `StageRestoreAsync` with the file-picker path, show `PendingRestore`, and surface `WorkspaceStartupReport.Restore` (including a non-null `Problem` when a staged candidate was discarded).

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Workspace/Backup/RestoreStaging.cs
- FOUND: src/AzureDesignStudio.Workspace/Backup/WorkspaceBackupService.cs
- FOUND: src/AzureDesignStudio.Core.Tests/WorkspaceRestoreTests.cs
- FOUND: src/AzureDesignStudio.Core.Tests/WorkspaceBackupServiceTests.cs
- FOUND commits: a5d2a9b, 77d16e2, ccdfd76
