---
phase: 04-database-durability
plan: 03
subsystem: database
tags: [efcore, sqlserver, migrations, baseline, bootstrap, source-guard]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-01 dotnet-ef pin and EF Design package in the Server project
provides:
  - SqlServerDesignDbContext (public sealed, Server project) that owns the SQL Server migrations
  - Generated Baseline migration for SQL Server (Up = one guarded T-SQL constant GuardedSql, Down empty), Designer and SqlServerDesignDbContextModelSnapshot
  - DatabaseProvider.AddDesignDbContext, the one registration used by Program.cs and the bootstrapper tests
  - DatabaseBootstrapper.Decide / BootstrapAction / MigrateKey / AllowCreateKey, config keys Database:Migrate (default true) and Database:AllowCreate (default false)
  - SourceGuardTests.MigrateIsCalledOnlyBySchemaOwners
affects: [04-06, 04-08, 04-12]

plan_head_before: 9927426af224d291ab11d716642400d3fb905d4d
plan_head_after: 27346f0645ed16ede1d0506023d2f5a9cc870a76

actuals:
  tokens: 14800   # chars/4 over the 11-file diff (generated Designer and snapshot included)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Derived context in the host project owns provider-specific migrations; services keep asking for the base DesignDbContext (AddDbContext<DesignDbContext, SqlServerDesignDbContext>)"
    - "Baseline Up is one guarded-SQL constant; generated Designer and snapshot untouched"
    - "Startup decision as a pure function (Decide) so refusal paths test without a SQL Server"

key-files:
  created:
    - src/AzureDesignStudio.Server/Persistence/SqlServerDesignDbContext.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114120545_Baseline.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114120545_Baseline.Designer.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/SqlServerDesignDbContextModelSnapshot.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationModelTests.cs
  modified:
    - src/AzureDesignStudio.Server/Persistence/DesignDbContextFactory.cs
    - src/AzureDesignStudio.Server/Persistence/DatabaseProvider.cs
    - src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs
    - src/AzureDesignStudio.Server/Program.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/DatabaseBootstrapperTests.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs

key-decisions:
  - "SQL Server migrations live in AzureDesignStudio.Server (derived context), not Services: the generated snapshot needs the SqlServer provider package (CONTEXT [Research C1])"
  - "Baseline creates AdsDesigns and AzureSubscriptions behind IF OBJECT_ID guards (with the ADS_CHECK_JSON ISJSON check); the five legacy tables keep the old guarded DDL except named primary keys and no default on AdsPlanApprovals.ApprovedAtUtc in the create branch"
  - "A missing database is refused before pending-migration checks; Database:Migrate=false with pending migrations refuses to start; invalid boolean values stop the host rather than being guessed"
  - "Rollback is restore of the pre-upgrade backup; Down is empty"

patterns-established:
  - "Server.Tests reads the SQL Server model through DesignDbContextFactory and asserts HasPendingModelChanges and the offline GenerateScript()"

requirements-completed: []  # DATA-02 is closed by the orchestrator at phase completion

duration: 15min
completed: 2026-11-14
---

# Phase 04 Plan 03: SQL Server baseline migration and fail-closed bootstrap Summary

**The server database now upgrades through a generated, guarded T-SQL baseline in `SqlServerDesignDbContext`, applied by `Migrate()` at startup unless the database is missing (refused by default) or migrating is switched off with migrations pending (refused).**

## Accomplishments

- `SqlServerDesignDbContext` plus a design-time factory targeting it; `dotnet ef migrations add Baseline` generated the migration, Designer and snapshot. Only the migration's `Up` and `Down` were edited.
- `Baseline.GuardedSql`: seven tables behind `IF OBJECT_ID`, the four `COL_LENGTH` column-add guards and both `INDEXPROPERTY` index guards kept as they were. A legacy database gets a history row and no table is recreated.
- `DatabaseProvider.AddDesignDbContext` replaces the inline registration in `Program.cs`; the bootstrapper tests use it too.
- `DatabaseBootstrapper`: SQL Server runs `Database.Migrate()` guarded by `Decide`; Sqlite and InMemory keep the single remaining model-creation call. `LegacySqlServerUpgrade` and `NeedsLegacyUpgrade` are gone.
- `MigrateIsCalledOnlyBySchemaOwners` allows `Migrate`/`MigrateAsync` only in the server bootstrapper, `WorkspaceSchema.cs` and `DeployDbStartup.cs`, and never in MCP (generated `Migrations` folders skipped).

## Task Commits

1. **Task 1 (tracer): derived context, guarded baseline, shared registration, offline script check** - `66ef758` (feat)
2. **Task 2: Migrate in the bootstrapper with fail-closed switches** - `cb351e6` (feat)
3. **Task 3: migration call-site guard** - `27346f0` (test)

## Verification

- `SqlServerMigrationModelTests` -> 3 passed (no pending model changes, first migration `_Baseline`, offline script guards all seven tables and contains `ADS_CHECK_JSON`)
- `ServerHostTests` + design-time factory test -> 15 passed
- `DatabaseBootstrapper` filter -> 11 passed (4 Decide facts plus the surviving bootstrap tests)
- `SourceGuardTests` -> 5 passed; planting `.Migrate()` in a probe file under `src/AzureDesignStudio.Mcp/` made the new guard fail (probe removed)
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` -> 198 passed, 0 failed
- `dotnet ef migrations has-pending-model-changes` -> "No changes have been made to the model since the last migration."
- Acceptance greps: 3 migration files; `AddDesignDbContext` 1 in Program.cs, `AddDbContext<DesignDbContext>` 0; `migrationBuilder.Sql` 1; `DEFAULT GETUTCDATE` 0; `LegacySqlServerUpgrade` 0; `EnsureCreated()` 1 in the bootstrapper; no lock file changed (one drift in `src/AzureDesignStudio/packages.lock.json` from `dotnet ef` on this SDK was reverted before the commit)

## Deviations from Plan

None. The plan was executed as written. The Decide tests of Task 2 were written before the implementation (the project did not compile until `Decide` existed); no separate RED commit was made because the plan's three-commit structure has one commit per task.

## Not verified here

The baseline T-SQL has not run against a real SQL Server on this machine: the live checks (fresh versus legacy parity, row retention, the AllowCreate refusal, backup-restore rollback) belong to plan 04-06 as the threat register states. Offline, the script is generated and inspected only.

## Known Stubs

None.

## Threat Flags

None. T-04-06 (AllowCreate defaults to false, `Decide` refuses a missing database), T-04-08 (every CREATE and column add is guarded; nothing drops) and T-04-09 (call-site guard, MCP forbidden) are implemented; T-04-07 stays accepted.

## Issues Encountered

- `dotnet ef` on SDK 10.0.403 rewrote `src/AzureDesignStudio/packages.lock.json`; reverted with `git checkout --` and never staged.

## Self-Check: PASSED

- FOUND: SqlServerDesignDbContext.cs, Migrations/ (3 files), SqlServerMigrationModelTests.cs
- FOUND commits: 66ef758, cb351e6, 27346f0
