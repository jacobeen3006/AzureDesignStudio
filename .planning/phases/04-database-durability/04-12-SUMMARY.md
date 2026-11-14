---
phase: 04-database-durability
plan: 12
subsystem: documentation
tags: [runbook, docs, agents-md, drift-guard, sqlite, sql-server, backup, restore]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-04 connection/MCP modes, 04-06 SQL Server rehearsal, 04-07..04-11 startup, backup, restore, dedupe, lock bound and Desktop drawer
provides:
  - docs/database-operations.md, the operator runbook for workspace.db, deploy.db and the SQL Server database
  - AGENTS.md entries that describe the new schema authority, backup layout and commands
  - DatabaseRunbookTests, a drift guard over the runbook and the AGENTS.md link
affects: [04-13]

plan_head_before: b24c4c9d9182cec27a407f2ed7c60eb3de326393
plan_head_after: 93e99fe8dd44eaa6c33ecfee38d3bdbc3f32f448

actuals:
  tokens: 12500   # chars/4 over git diff b24c4c9..93e99fe (49,918 chars)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "A runbook is a tested artifact: required recovery statements, cited repository paths and credential-free content are asserted by a text-only test"
    - "Recovery text is quoted from the code that prints it (the lock statement from WorkspaceUpgradeException), and a test ties the two"

key-files:
  created:
    - docs/database-operations.md
    - src/AzureDesignStudio.Core.Tests/DatabaseRunbookTests.cs
  modified:
    - AGENTS.md

key-decisions:
  - "The runbook documents what was built, including the gaps: the deploy.db migration is not time-bounded and only a failed deploy.db premigration backup reaches the failure page"
  - "No connection-string example appears anywhere in the runbook (not even with placeholders), so the credential guard is a plain substring test"
  - "The guard checks src/ paths twice: backtick-quoted tokens outside code fences (the plan's rule) and every src/ path anywhere, so a moved project named in a dotnet ef command also fails"

patterns-established:
  - "Documentation drift guards live in Core.Tests, find the repository root by walking up to global.json, and read text files only"

requirements-completed: []

duration: ~45min
completed: 2026-11-14
---

# Phase 4 Plan 12: Database runbook, AGENTS.md update and drift guard Summary

**An operator can now recover every failure this phase can produce from `docs/database-operations.md` (quarantine, stuck `__EFMigrationsLock`, failed upgrade, restore, MCP open modes, removed duplicate rows, SQL Server upgrade, backup, rollback and first deployment), AGENTS.md no longer states anything the phase made false, and a test fails when either drifts.**

## Performance

- **Tasks:** 3 (1 tracer, 2 auto), each committed individually
- **Files:** 2 created, 1 modified (no source or lock file touched)

## Accomplishments

- Runbook, workspace half: data locations on macOS and Windows with `ADS_DATA_DIR`, the ordered startup steps, backups (names, manifest fields, retention of the newest five non-manual plus 30 days of premigration, manual never pruned), restore (staged, verified, applied at next launch, previous workspace kept as `workspace.pre-restore-*`, the manual alternative), corruption and the `quick_check` blind spot with the `sqlite3` `integrity_check` command, lock recovery with the exact `DELETE FROM "__EFMigrationsLock";`, the `upgrade-removed-rows-<UTC>.jsonl` report, MCP `ReadOnly`/`ReadWriteQueryOnly`/`Immutable` and `ads-mcp --self-check` exit codes 0/2/3, WAL guidance, and `deploy.db` with its private `backups/deploy/` copies. Plaintext at rest and the client-secret warning are stated up front and again under `deploy.db`.
- Runbook, SQL Server half: the configuration keys, `Database:Migrate` and `Database:AllowCreate` (the bootstrapper's own error message already points at this file), single replica and the DDL-rights acceptance, first deployment, upgrade procedure, backup for self-hosted (`BACKUP ... WITH INIT, CHECKSUM, FORMAT`, `RESTORE VERIFYONLY ... WITH CHECKSUM`) and Azure SQL (point-in-time restore, `AS COPY OF`), rollback as the restore of the pre-upgrade backup (`SINGLE_USER WITH ROLLBACK IMMEDIATE`, `RESTORE ... WITH REPLACE, CHECKSUM`, `MULTI_USER`) mirroring `BackupMigrateRestore_ReturnsToThePreUpgradeState` and `src/RunSqlServerTests.sh`, `Down()` stated as unsupported, the session-lock note, and the three `dotnet ef migrations add` lines.
- `DatabaseRunbookTests` (7 tests): required workspace statements, required SQL Server statements, the `Down()` note, every cited `src/` path exists, the lock statement matches `WorkspaceSchema.cs`, no `Password=`/`User ID=`/`Server=`/key material, and AGENTS.md links the runbook. A mutation (a `Password=` line plus a path that does not exist) failed exactly the two matching tests.
- AGENTS.md: the two false sentences replaced (legacy T-SQL gate, `deploy.db` `EnsureCreated`), STRUCTURE annotations, two WHERE TO LOOK rows, three COMMANDS lines and seven NOTES bullets (schema authority and the three migrate sites, premigration backup invariant, connection discipline and MCP open modes, startup quarantine/restore/lock rules, SQL Server rollback and replica rule, the Design package and tool manifest, the runbook pointer).

## Task Commits

1. Task 1 (tracer): `85fb0df` - workspace and deploy database runbook with a guard test (4 tests)
2. Task 2: `ffb2391` - SQL Server upgrade, backup, rollback and developer commands (6 tests)
3. Task 3: `93e99fe` - AGENTS.md updates and the link test (7 tests); the commit names only `AGENTS.md` and `DatabaseRunbookTests.cs`

## Verification (evidence)

- `dotnet test src/AzureDesignStudio.Core.Tests --filter "FullyQualifiedName~DatabaseRunbookTests"`: Passed 4 after Task 1, 6 after Task 2, 7 after Task 3.
- Tracer gate (interactive, `end-of-phase`, automated-only verify): re-run after Task 1, passed before Task 2 started.
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: Passed 1779, Failed 0.
- `dotnet test src/AzureDesignStudio.Server.Tests --filter "FullyQualifiedName~SourceGuardTests"`: Passed 6, Failed 0.
- Acceptance greps: `grep -cE 'Password=|User ID=|Server=' docs/database-operations.md` is 0; `database-operations.md` appears 4 times in AGENTS.md; `ADS_DATA_DIR` 2; `legacy T-SQL upgrade` 0; `audit; .EnsureCreated.` 0; `git status --porcelain -- '*packages.lock.json'` empty after every run.

## Deviations from Plan

None - the plan was executed as written, with these choices inside its latitude:

- The files were written once and then committed in the plan's three steps (the Task 1 commit holds only the workspace half and four tests, Task 2 adds the server half, Task 3 adds AGENTS.md and the link test), so each commit is green on its own.
- The credential guard is stricter than the plan's list: it also rejects `AccountKey=`, `SharedAccessKey=` and `-----BEGIN`. A connection-string example, even with placeholders, was left out so `Server=` can be rejected outright.
- One extra test ties the runbook's lock statement to the string `WorkspaceSchema.cs` builds.

## Documented gaps (stated in the runbook, not fixed here)

- **deploy.db migration failures.** `MauiProgram` routes only an `InvalidOperationException` (a failed premigration backup) to the failure page; a `deploy.db` migration that itself throws ends the launch with the crash log. Also `DeployDbStartup.Migrate` has no time bound, so a stuck `deploy.db` lock would show as a launch that never finishes, unlike the workspace's 30-second message. The runbook says both plainly. Candidate follow-up for a later phase; no code was changed in this plan.
- **Windows paths.** The Windows Desktop target has not been built or run since the durability work, so the runbook tells the reader to take the Windows data folder from the drawer rather than asserting a path. The `ads-mcp` Windows default is given as the unpackaged location.
- **`ads-mcp` default path on macOS** does not match the Mac data folder; the runbook says to pass `--db`.
- **CI.** The first GitHub run of the `sqlserver` job has not happened yet; the runbook describes it as the per-build rehearsal and the local `src/RunSqlServerTests.sh` run is the evidence so far.
- **Azure SQL rollback** follows the service's documentation and is not rehearsed; the runbook says so.
- `Database:AllowCreate` defaults to false and the runbook says to set it only for a first deployment.
- A SIGTERM during a scheduled backup can leave `workspace-<UTC>-scheduled.db.tmp` in `backups/`; the runbook marks it as safe to delete.

## Known Stubs

None.

## Threat Flags

None - documentation, one test file and no new runtime surface. T-04-46 (no credential in the runbook) and T-04-47 (plaintext and the deploy.db backup warning) are mitigated and guarded by tests; T-04-45 is covered by backup-and-verify steps before every destructive step and by the rehearsed SQL Server sequence.

## Next Phase Readiness

04-13 can read the runbook end to end (the VALIDATION human check). The visual Desktop items listed in the 04-11 summary remain for that plan.

## Self-Check: PASSED

- FOUND: docs/database-operations.md
- FOUND: src/AzureDesignStudio.Core.Tests/DatabaseRunbookTests.cs
- FOUND: commits 85fb0df, ffb2391, 93e99fe (ancestors of HEAD)
