---
phase: 04-database-durability
plan: 06
subsystem: database
tags: [sqlserver, efcore, migrations, docker, ci, rollback-rehearsal, xunit]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-03 SqlServerDesignDbContext, guarded Baseline migration, DatabaseBootstrapper with Database:Migrate / Database:AllowCreate
provides:
  - SQL Server test infrastructure in Server.Tests (SqlServerFact, throwaway-database fixture, schema describer, legacy fixture SQL)
  - SqlServerMigrationTests (8 tests, Category=SqlServer) run against a real SQL Server 2022 container
  - Migration NormaliseLegacyDefaults (drops the unnamed default on AdsPlanApprovals.ApprovedAtUtc; snapshot unchanged)
  - src/RunSqlServerTests.sh one-command local run
  - Independent `sqlserver` job in .github/workflows/build.yml
affects: [04-08, 04-12, 04-13]

plan_head_before: 6aaf8e63cfc6e7680e8f7ea0ed7bf7566476efa1
plan_head_after: a09fc09383519fdcd54afa713849be5a47bf83ce

actuals:
  tokens: 12700   # chars/4 over the 10-file diff (generated Designer included)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "SqlServerFact: a FactAttribute subclass that sets Skip when ADS_TEST_SQLSERVER is unset (xUnit 2.9 has no runtime skip)"
    - "One uniquely named throwaway database per test, dropped on dispose; the container is the only server ever touched"
    - "Schema parity by a normalised description string (column order ignored, generated constraint names normalised)"

key-files:
  created:
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerFactAttribute.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerDatabaseFixture.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerSchemaDescriber.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationTests.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/Fixtures/sqlserver-legacy.sql
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114123056_NormaliseLegacyDefaults.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114123056_NormaliseLegacyDefaults.Designer.cs
    - src/RunSqlServerTests.sh
  modified:
    - src/AzureDesignStudio.Server.Tests/AzureDesignStudio.Server.Tests.csproj
    - .github/workflows/build.yml

key-decisions:
  - "Rollback is rehearsed as BACKUP, RESTORE VERIFYONLY, Migrate, write, RESTORE WITH REPLACE; a test runs it on every SQL Server run. Down() stays empty"
  - "A generated primary-key name reads as PK_<table> in the schema description (the baseline and the model name their keys, the legacy tables did not); generated defaults read as <system-name>"
  - "Generated-name pattern is ^(PK|DF)__.+__[0-9A-F]{8,16}$: SQL Server's suffix is longer than the 8 hex digits the plan assumed"
  - "The drop of the legacy default builds the ALTER text in a variable before EXEC: EXEC(N'..' + QUOTENAME(x)) is a syntax error"

patterns-established:
  - "Category=SqlServer tests: run with bash src/RunSqlServerTests.sh locally, or by the sqlserver CI job"

requirements-completed: []  # DATA-02 is closed by the orchestrator at phase completion

duration: 95min
completed: 2026-11-14
---

# Phase 04 Plan 06: SQL Server migration proof and rollback rehearsal Summary

**The server baseline and a second migration now run against a real SQL Server 2022 container: a legacy database upgrades with every row kept, legacy, fresh and model-created schemas agree, concurrent migrates are safe, and the backup/restore rollback is executed (not just documented) by a test.**

## Accomplishments

- Tracer: `LegacyUpgrade_KeepsRowsAndWritesHistory` builds the shipped legacy schema (unnamed primary keys, the unnamed `GETUTCDATE()` default, the four ALTER-ed-in columns missing, no `ApplyId` indexes, no history table) with seed rows in all seven tables, calls the real `Migrate()`, and checks every row count, the `_Baseline` history row and the four added columns.
- `NormaliseLegacyDefaults` (`Up` only edited; model snapshot untouched) drops the unnamed default on `AdsPlanApprovals.ApprovedAtUtc`; it is a no-op on a baseline-created database.
- Seven further tests: legacy-upgraded equals fresh-by-`Migrate()`; fresh equals model-created (`EnsureCreated`, test-only); no pending model changes after the upgrade; three concurrent `Migrate()` calls leave one history row per migration; backup, migrate, write, restore returns to the pre-upgrade rows with no history table and migrates again; the bootstrapper refuses a missing database unless `Database:AllowCreate` (and creates it, with a history table, when allowed - Assumption A7 confirmed); it refuses pending migrations when `Database:Migrate` is false.
- `src/RunSqlServerTests.sh`: starts `mcr.microsoft.com/mssql/server:2022-latest` (amd64 under emulation on Apple silicon), waits for the ready log line, exports `ADS_TEST_SQLSERVER`, runs `Category=SqlServer`, removes the container in an EXIT trap and propagates the test exit code.
- `sqlserver` job in `build.yml`: service container, locked restore, lock-drift check, Release build, wait for port 1433, `Category=SqlServer` tests. The `build`, `codeql` and `desktop` jobs are unchanged (the diff is additions only).

## Task Commits

1. **Task 1 (tracer): legacy upgrade in a container, one-command run** - `ea5a5ea` (test)
2. **Task 2: parity, concurrency, rehearsal, refusals, migration 2** - `07ede07` (test)
3. **Task 3: sqlserver CI job** - `a09fc09` (ci)

## Verification

- `bash src/RunSqlServerTests.sh` -> Passed 8, Failed 0, Skipped 0; container removed afterwards
- Without `ADS_TEST_SQLSERVER`: the tracer reports Skipped 1, Failed 0 (Task 1); `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` -> Passed 198, Failed 0, Skipped 8
- Workflow YAML parses and lists build, codeql, desktop, sqlserver; `Category=SqlServer` and the image tag each appear once; the `build.yml` diff has no removed lines
- Acceptance: five `.cs` files in `Migrations/` (two migrations, two designers, one snapshot); the snapshot is not in the Task 2 commit; script mode `100755`; no `packages.lock.json` changed
- Not verified here: the first GitHub run of the `sqlserver` job (needs a push; planned as a phase gate in 04-13)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Migration 2 T-SQL was rejected by the server**
- **Found during:** Task 2 (first container run, as research Assumption A8 expected)
- **Issue:** `EXEC(N'ALTER TABLE ... DROP CONSTRAINT ' + QUOTENAME(@name))` is a syntax error ("Incorrect syntax near 'QUOTENAME'"); all 8 tests failed on the second migration
- **Fix:** Build the statement into an `nvarchar` variable, then `EXEC(@drop)`
- **Files modified:** `20261114123056_NormaliseLegacyDefaults.cs`
- **Commit:** `07ede07`

**2. [Rule 1 - Bug] Describer pattern for generated names too narrow, and named keys not comparable**
- **Found during:** Task 2 (parity test)
- **Issue:** real generated key names look like `PK__AdsDecom__D981DF21CC...` (suffix longer than 8 hex digits); and the baseline's named keys (`PK_<table>`) never equal a legacy generated name, so parity could not hold with the plan's `<system-name>` rule for keys
- **Fix:** pattern `{8,16}`; a generated primary-key name is reported as `PK_<table>`; generated defaults still read `<system-name>` (the test asserts the legacy default exists before migrating)
- **Files modified:** `SqlServerSchemaDescriber.cs`
- **Commit:** `07ede07`

**3. [Rule 1 - Bug] Local script wait loop failed intermittently**
- **Found during:** Task 2 (script run reported "did not become ready" for a container that was ready)
- **Issue:** `docker logs | grep -q` under `set -o pipefail` returns 141 (SIGPIPE) when grep matches and exits early
- **Fix:** read the log into a variable, then grep it; also added `SQLTEST_WAIT` (default 240 s)
- **Files modified:** `src/RunSqlServerTests.sh`
- **Commit:** `07ede07`

## Known Stubs

None.

## Threat Flags

None. T-04-19 (throwaway uniquely named databases, tests skip without the variable, no real host anywhere), T-04-20 (ephemeral SA password, stated in comments), T-04-21 (the rollback sequence is executed by a test) and T-04-22 (three concurrent migrates tested) are implemented as planned.

## Issues Encountered

- The emulated amd64 SQL Server image takes several minutes on its first start on this Apple-silicon machine; the Docker image pull and first container start dominate the run time. The pipefail bug above made two early runs look like start failures.
- Docker containers started for the work (`ads-sqltest-*`, one development container) were all removed.

## Self-Check: PASSED

- FOUND: SqlServerFactAttribute.cs, SqlServerDatabaseFixture.cs, SqlServerSchemaDescriber.cs, SqlServerMigrationTests.cs, Fixtures/sqlserver-legacy.sql, 20261114123056_NormaliseLegacyDefaults.cs, RunSqlServerTests.sh
- FOUND commits: ea5a5ea, 07ede07, a09fc09
