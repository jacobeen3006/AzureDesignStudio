---
phase: 04-database-durability
fixed_at: 2026-11-14T00:00:00Z
review_path: .planning/phases/04-database-durability/04-REVIEW.md
iteration: 1
findings_in_scope: 8
fixed: 8
skipped: 0
status: all_fixed
---

# Phase 04: Code Review Fix Report

**Source review:** `.planning/phases/04-database-durability/04-REVIEW.md`
**Scope:** Critical and Warning (WR-01..WR-08). Info findings IN-01..IN-05 are out of scope and remain open.
**Where verification ran:** the main checkout (no worktree), branch `feat/wave-b2-b4-governance-preflight`.

**Summary:**
- Findings in scope: 8
- Fixed: 8 (3 flagged for human verification because they are policy or logic choices)
- Skipped: 0

Final gates, all green: `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` 1799 passed; `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` 208 passed, 8 skipped (SQL Server container tests); `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst` 0 errors. `DatabaseRunbookTests` stays green. No `packages.lock.json` drift was produced.

## Fixed Issues

### WR-01: Integrity classification quarantined a healthy workspace on unlisted SQLite codes

**Status:** fixed: requires human verification (logic change)
**Commit:** dafbc15
**Files modified:** `src/AzureDesignStudio.Workspace/Integrity/WorkspaceIntegrity.cs`, `src/AzureDesignStudio.Core.Tests/WorkspaceIntegrityTests.cs`, `docs/database-operations.md` (commit a88b641 carries the runbook line)
**Applied fix:** deny-list inverted to an allow-list: only SQLite primary codes 11 (corrupt), 24 (format) and 26 (notadb) are `Corrupt`; every other code, including 15 (protocol), 9, 4, 21, 23, 17, 1 and unknown codes, is `Inconclusive`, so startup stops with "nothing was moved". Real corruption (truncated file, zeroed header, plain-text file, `quick_check` rows) still quarantines. Tests: a theory over 19 codes plus an exhaustive 0..299 table check (`11, 24, 26`); it fails against the old table.
**Human check:** confirm that a corrupt file that SQLite reports with some other primary code (none known in the existing corruption tests) should indeed stop startup rather than quarantine.

### WR-02: Retention could evict the only good backups after a quarantine

**Status:** fixed: requires human verification (policy choice)
**Commit:** a88b641
**Files modified:** `BackupRetention.cs`, `WorkspaceBackup.cs`, `BackupRetentionTests.cs`, `docs/database-operations.md`
**Applied fix:** `RetentionPolicy` gained `KeepNewestWithData` (default 2, 0 for the deploy backups). In addition to the existing rules, retention keeps the newest two scheduled or premigration backups whose manifest `tableCounts` show rows in any non-`__EF*` table, so backups of an empty post-quarantine workspace can no longer push every backup holding data out of the newest five. A missing or unreadable manifest counts as "holds data" (bounded to two files). `BackupRetention.Plan` takes an optional `holdsData` probe so it stays pure; `PruneAsync` supplies it from the manifests. Documented under Retention in `docs/database-operations.md`. Tests: plan level (7 empty plus 3 with data keeps exactly the newest 2 with data), rule off/no probe, and `PruneAsync` reading real manifests.
**Not done:** skipping scheduled backups while the recovery notice is unacknowledged (the alternative offered); the data-bearing exemption alone covers the stated risk.
**Human check:** the policy trade-off (two extra files stay until two newer backups with data exist, including after a user deliberately empties a workspace) is documented; confirm it is acceptable.

### WR-03: Startup exceptions bypassed the failure page; restore chmod ordering bug

**Status:** fixed
**Commit:** 8cbdbc3
**Files modified:** `src/AzureDesignStudio.Desktop/MauiProgram.cs`, `src/AzureDesignStudio.Workspace/Backup/RestoreStaging.cs`, `src/AzureDesignStudio.Core.Tests/WorkspaceRestoreTests.cs`, `docs/database-operations.md`
**Applied fix:** both startup blocks in `MauiProgram` now catch through `IsStartupFailure` (`WorkspaceUpgradeException`, `InvalidOperationException`, `IOException`, `UnauthorizedAccessException`, any `DbException` such as `SqliteException`, EF `DbUpdateException`) and show `StartupFailurePage`; other exceptions are bugs and still crash. In `RestoreStaging.ApplyPendingAsync` the unix-mode call is guarded with `File.Exists(stem + ".db")` and is best effort, and the wal/shm cleanup plus staging folder deletion run in a `finally` once the candidate is in place. Runbook sentence about deploy.db migration failures updated (they now reach the failure page). Test: restore when only a `-wal` of the live workspace remains (failed with `FileNotFoundException` before the fix, passes now). The `MauiProgram` filter has no unit test (Desktop is not test-referenced); verified by a clean maccatalyst build.

### WR-04: "Restore from file" accepted the live workspace.db or a file with a WAL sidecar

**Status:** fixed
**Commit:** 554ea18
**Files modified:** `RestoreStaging.cs`, `WorkspaceRestoreTests.cs`, `docs/database-operations.md`
**Applied fix:** `StageAsync` rejects (with a message pointing to "Back up now") a picked path equal to `<appData>/workspace.db` (case-insensitive except on Linux) and any picked database with a non-empty `<picked>-wal`. An empty `-wal` is accepted. Tests cover both rejections (nothing staged) and the empty-sidecar acceptance.

### WR-05: Dedupe "keep newest" ordered DateTimeOffset values by text

**Status:** fixed: requires human verification (ordering logic)
**Commit:** 93af293
**Files modified:** `src/AzureDesignStudio.Workspace/UniqueIndexGuard.cs`, `src/AzureDesignStudio.Core.Tests/UniqueIndexGuardTests.cs`
**Applied fix:** `KeepOrderBy` is now `julianday(SavedAt) DESC, SavedAt DESC, rowid DESC` (and the `ChangeTime` equivalent), so the kept row is the latest instant across UTC offsets; the text and rowid break ties and keep unparseable values deterministic. Test with rows in `+02:00` and `+01:00` for both tables fails against the old order. Existing L4 fixture tests still pass.
**Human check:** `julianday` precision is about 40 microseconds; rows within that window fall back to text then rowid, which is fine for "newest save wins".

### WR-06: Rows removed by the dedupe were not announced at launch

**Status:** fixed
**Commit:** b54fc0d
**Files modified:** `WorkspaceSchema.cs`, `src/AzureDesignStudio.Desktop/MainPage.xaml.cs`, `UniqueIndexGuardTests.cs`, `docs/database-operations.md`
**Applied fix:** `WorkspaceUpgradeResult.RemovedRowsMessage` (table, row and key counts, report path, premigration backup) is shown by `MainPage.OnLoaded` as a "Duplicate rows removed" alert. The repair/migration ordering is unchanged (minimal change as instructed); instead, a migration failure after a cleanup now says the duplicate rows stay removed and points to the report and backup, and the failure text says "schema is as it was" rather than "database is as it was". Runbook "Removed duplicate rows" section updated. Test: message content for the duplicates fixture, null for a clean workspace. The alert wiring itself is covered by the maccatalyst build only.
**Not done:** running the repair and the migration in one scope.

### WR-07: Backup creation could replace an existing manifest and left orphan manifests

**Status:** fixed
**Commit:** 1a0ab6c
**Files modified:** `WorkspaceBackup.cs`, `WorkspaceBackupTests.cs`, `docs/database-operations.md`
**Applied fix:** temp files get a per-call random suffix (`.<8 hex>.tmp`), the database is moved first with `overwrite: false` and the manifest second, and `PruneAsync` deletes manifests whose backup file is gone (only names that parse as our backups for that stem). Runbook updated for the new `.tmp` name and orphan cleanup. Tests: orphan-manifest pruning (fails without the fix), a four-way concurrent create asserting exactly one winner whose manifest hash matches its database and no leftovers, and the existing-target test now also asserts the manifest is untouched. Note: the concurrency test is a regression guard; it also passes against the old code on this machine because the race is timing dependent.

### WR-08: The 30-second bound covered the migration itself

**Status:** fixed: requires human verification (timing policy)
**Commit:** b66cf74
**Files modified:** `WorkspaceSchema.cs`, `MigrationLockTests.cs`, `docs/database-operations.md`
**Applied fix:** EF's SQLite migrator acquires the lock inside `MigrateAsync` in a poll loop, so lock acquisition cannot be bounded separately without risking an unbounded hang. Instead the bound is documented and scaled: 30 s base plus 1 s per whole MiB of the database file (`MigrateBound`, `SecondsPerMegabyte`), so small files still fail fast on a stuck lock while a large data migration is not cancelled. `StuckUpgrade` now checks the lock table: with a lock left it keeps the exact `DELETE FROM "__EFMigrationsLock";` advice (plus a warning to do it only when nothing else is running); with no lock left it says the upgrade was slow, rolled back, nothing should be deleted. Runbook step 6 and "Migration lock recovery" updated. Tests: bound scaling and both message variants.
**Human check:** the 1 s per MiB allowance is an estimate; adjust `SecondsPerMegabyte` if a real data migration is slower.

## Skipped Issues

None.

---

_Fixed: 2026-11-14_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
