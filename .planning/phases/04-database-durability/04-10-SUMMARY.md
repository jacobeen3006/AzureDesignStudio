---
phase: 04-database-durability
plan: 10
subsystem: database
tags: [sqlite, migrations, unique-index, dedupe, migration-lock, ef-core]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-07 WorkspaceSchema upgrade pipeline (premigration backup, legacy adoption, bounded MigrateAsync), 04-05 WorkspaceBackup
provides:
  - UniqueIndexGuard (Specs, Repair), UniqueIndexSpec, UniqueIndexRepair
  - WorkspaceUpgradeResult.UniqueIndexRepairs, filled by WorkspaceSchema.UpgradeAsync after the premigration backup
  - upgrade-removed-rows-<yyyyMMddTHHmmssZ>.jsonl report format in the backups folder
  - Proven abandoned-lock behaviour (bounded failure, recovery statement, manual recovery)
affects: [04-11, 04-12, 04-13]

plan_head_before: 1a6fdf7362d177432200c90b1be8b4a5316c08f3
plan_head_after: a380d83119d66dab6d5b27a7a209d8f8147f05f7

actuals:
  tokens: 9700   # chars/4 over git diff 1a6fdf7..HEAD -- src (38,802 chars)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Table-driven unique-index pre-flight: a later phase adds one UniqueIndexSpec row, no new code path"
    - "Report first, then one transaction: the jsonl is flushed to disk before any DELETE, and the deletes plus the same-named plain index drop commit or roll back together"
    - "Duplicate detection with FIRST_VALUE / ROW_NUMBER over a named window partitioned by the key columns, restricted to rows where every key column IS NOT NULL"

key-files:
  created:
    - src/AzureDesignStudio.Workspace/UniqueIndexGuard.cs
    - src/AzureDesignStudio.Core.Tests/UniqueIndexGuardTests.cs
    - src/AzureDesignStudio.Core.Tests/MigrationLockTests.cs
  modified:
    - src/AzureDesignStudio.Workspace/WorkspaceSchema.cs

key-decisions:
  - "The guard runs for any existing file with pending migrations, between the premigration backup and legacy adoption; it receives the backup path as a required argument and throws ArgumentException before touching data if it is empty or missing"
  - "The report goes into the same folder as the premigration backup (derived from the backup path), so the user-only backups folder holds both"
  - "All specs share one report file per run and one transaction; a spec's same-named non-unique index is dropped inside that transaction, only when duplicates are being repaired"
  - "WorkspaceUpgradeResult keeps its positional shape; UniqueIndexRepairs is an init property defaulting to empty, so existing constructors are untouched"
  - "A SqliteException or IOException from the guard becomes WorkspaceUpgradeException naming the backup path; nothing was removed in either case"

patterns-established:
  - "KeepOrderBy is a compile-time ORDER BY fragment ending in rowid DESC so ties are deterministic; SQL identifiers are quoted constants and row values are only read and serialised"

requirements-completed: []  # DATA-08 is closed by the orchestrator at phase completion

duration: 25min
completed: 2026-11-14
---

# Phase 04 Plan 10: Unique-index guard and abandoned-lock recovery Summary

**The first migration now survives data its new unique indexes would reject: after the premigration backup, `UniqueIndexGuard` keeps the most recently updated row per key, writes every removed row to `upgrade-removed-rows-<UTC>.jsonl` and deletes the rest in one transaction, and an abandoned `__EFMigrationsLock` row makes the bounded upgrade fail with the exact `DELETE FROM "__EFMigrationsLock"` recovery statement instead of hanging.**

## Accomplishments

- `UniqueIndexGuard.Repair(connection, specs, reportDirectory, nowUtc, requiredBackupPath)`: throws `ArgumentException` without an existing backup file; skips a spec whose table is missing or whose UNIQUE index of that name is already in place; finds duplicate groups only among rows whose key columns are all `IS NOT NULL`; keeps the first row of each group by the spec's order (`Designs`: `SavedAt DESC, rowid DESC`; `ChangeFeed`: `ChangeTime DESC, rowid DESC`); writes one JSON line per removed row (`table`, `index`, `keyColumns`, `keptRowId`, `removedRowId`, `row`) flushed to disk (0600 on Unix) before deleting by `rowid` in a single transaction.
- `WorkspaceSchema.UpgradeAsync` calls the guard only for an existing file with pending migrations; fresh and up-to-date files never reach it. `WorkspaceUpgradeResult.UniqueIndexRepairs` carries the repairs (empty when clean) to `WorkspaceStartupReport.Upgrade`.
- The L4 fixture upgrades end to end: the premigration backup still holds the duplicates, `d-dup-2` and ChangeFeed rows 2/4/5 survive (later time wins, the equal-time pair keeps the greater rowid), the report has one parseable line per removed row, both unique indexes exist and the logical schema equals a fresh workspace's.
- Abandoned lock: with a lock row present, the upgrade fails within the 2-second test bound (the whole three-test class runs in about 4 s) with a `WorkspaceUpgradeException` naming `__EFMigrationsLock`, the exact statement and the database path; the lock row and all table counts are untouched and a premigration backup exists. After the manual `DELETE`, the next upgrade applies the baseline with unchanged row counts and an empty lock table. A pre-cancelled caller token surfaces `OperationCanceledException`, never the lock message.

## Task Commits

1. **Task 1 (tracer): back up, dedupe keep-newest and report rows that violate the new unique indexes** - `74625e1` (feat)
2. **Task 2: table-driven behaviors of the unique-index guard** - `e227b51` (test)
3. **Task 3: abandoned migration lock fails bounded with the recovery statement** - `a380d83` (test)

## Verification

- Tracer gate: `UniqueIndexGuardTests` 2 passed; `WorkspaceUpgradeTests` + `WorkspaceStartupTests` 19 passed, so legacy files without duplicates upgrade as before.
- Task 2: `UniqueIndexGuardTests` 13 passed (the plan required 10): no-duplicates, existing unique index (a tripwire `KeepOrderBy` proves no scan), missing table, same-named plain index (dropped with duplicates, kept without), NULL keys (single and composite), equal ordering values and three rows per key, rollback on a `BEFORE DELETE` trigger abort (index restored too), refusal for empty, blank and missing backup paths (Theory, 3 cases), and a custom spec through the same path. Mutation check: letting NULL keys into the duplicate query made the NULL test fail, then the guard was restored.
- Task 3: `MigrationLockTests` 3 passed.
- Full offline suite `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1772 passed, 0 failed (1756 before this plan).
- Acceptance greps: `requiredBackupPath` and `upgrade-removed-rows` present in UniqueIndexGuard.cs; `UniqueIndexGuard.Repair` in WorkspaceSchema.cs 1; `IS NOT NULL` present; `DELETE FROM "__EFMigrationsLock"` and `MigrateTimeout` present in MigrationLockTests.cs; no `packages.lock.json` modified on this machine's SDK (git status clean apart from the plan files each time).
- Measured commits: `git rev-list --count 1a6fdf7..HEAD` = 3 at SUMMARY write time.

## Deviations from Plan

### Auto-fixed Issues

None - plan executed as written. Test-only adjustments: the second upgrade in a test uses its own `BackupsDirectory` because two premigration backups in the same UTC second collide by design ("never overwritten").

### TDD note

Task 2 is marked `tdd="true"`. The guard's rules were implemented in Task 1 (the tracer needed them), so the nine behavior tests passed on their first run and no separate red run was recorded. To show the tests are not vacuous, the NULL-key condition was temporarily broken and the NULL test failed, then the guard was restored from a backup copy (the committed file was never changed).

## Lock behaviour vs the research reproduction

Matches RESEARCH F12 on EF 10.0.12: a row in `__EFMigrationsLock` (`Id` INTEGER, `Timestamp` TEXT) makes `MigrateAsync` wait until the linked token fires; no fixture correction was needed.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model (T-04-36 to T-04-39): the report holds row data in plaintext inside the user-only backups folder (0600 file on Unix); it is never logged, and no package, endpoint or auth path was added. The runbook text for the report and the lock recovery is plan 04-12's job.

## Issues Encountered

- One accidental write attempt targeted a mistyped path outside the repository (`/Users/jacobeen/repositories/AzureDesignStudio.Core.Tests/placeholder`); the permission layer denied it, nothing was created, and the real test file was written under `src/AzureDesignStudio.Core.Tests/`.

## Next Phase Readiness

04-12 can document the report file, its plaintext-at-rest nature and the lock recovery statement. Phase 5 adds a tenant-scoped unique index by appending one `UniqueIndexSpec` to `UniqueIndexGuard.Specs`.

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Workspace/UniqueIndexGuard.cs
- FOUND: src/AzureDesignStudio.Core.Tests/UniqueIndexGuardTests.cs
- FOUND: src/AzureDesignStudio.Core.Tests/MigrationLockTests.cs
- FOUND commits: 74625e1, e227b51, a380d83
