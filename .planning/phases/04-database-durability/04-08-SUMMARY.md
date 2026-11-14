---
phase: 04-database-durability
plan: 08
subsystem: database
tags: [sqlite, efcore, migrations, deploy-db, desktop, premigration-backup, source-guard]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-03 derived-context migration pattern and Migrate call-site guard, 04-05 WorkspaceBackup (stem-generic) and retention
provides:
  - SqliteDesignDbContext (public sealed, provider-free Services) owning the SQLite migration set
  - Generated SQLite Baseline (Up = one guarded IF NOT EXISTS constant GuardedDdl, Down empty), designer and snapshot in Services/Migrations/Sqlite
  - SqliteDesignDbContextFactory (Server) so dotnet ef can build the SQLite set
  - DeployDbStartup.Migrate (Desktop): private premigration backup, legacy column adoption, Migrate, prune to 3
  - Desktop registers DesignDbContext as SqliteDesignDbContext and no longer calls the model-creation method
  - SourceGuardTests.NoOtherProjectCallsTheModelCreationMethod
affects: [04-11, 04-12]

plan_head_before: 110e8e4181937594fd4121126cb045b86df8bced
plan_head_after: 161166a8bdd1e0c27420cdd58c342ad5fd2a92e2

actuals:
  tokens: 14800   # chars/4 over the 11-file diff 110e8e4..HEAD (59,373 chars, generated Designer and snapshot included)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Derived context per provider: Services owns the SQLite set (snapshot compiles without a provider), Server owns the SQL Server set; the design-time factories both live in the Server"
    - "A Desktop plain-C# file the MAUI project owns is compiled into Server.Tests by a linked Compile item, so it is tested on real SQLite files"

key-files:
  created:
    - src/AzureDesignStudio.Services/Models/SqliteDesignDbContext.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114130156_Baseline.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114130156_Baseline.Designer.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/SqliteDesignDbContextModelSnapshot.cs
    - src/AzureDesignStudio.Server/Persistence/SqliteDesignDbContextFactory.cs
    - src/AzureDesignStudio.Desktop/DeployDbStartup.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/DeployDbMigrationTests.cs
  modified:
    - src/AzureDesignStudio.Desktop/MauiProgram.cs
    - src/AzureDesignStudio.Server.Tests/AzureDesignStudio.Server.Tests.csproj
    - src/AzureDesignStudio.Server.Tests/packages.lock.json
    - src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs

key-decisions:
  - "Deploy backups: stem deploy in backups/deploy (parent and folder 0700, files 0600 on Unix), so the workspace list (stem workspace) never returns them; newest 3 kept, no age window"
  - "A failed backup (including the backups path being a regular file) throws InvalidOperationException 'not upgraded' before any change to the file; asserted by identical SHA-256 and no history table"
  - "Legacy column adoption runs only when no migration history exists, on an unpooled connection, with constant identifiers and bound-parameter existence checks"
  - "The Migrate call-site guard ignores DeployDbStartup.Migrate(...) with a lookbehind; db.Database.Migrate() anywhere else (probe in Desktop) still fails it"

patterns-established:
  - "Old-file tests compare schemas ignoring column position, because SQLite can only append a column"

requirements-completed: []  # DATA-02 is closed by the orchestrator at phase completion

duration: 40min
completed: 2026-11-14
---

# Phase 04 Plan 08: Deploy database under SQLite migrations Summary

**The Desktop `deploy.db` now has a generated, guarded SQLite baseline in the provider-free Services project, is upgraded at startup behind a private `premigration` backup, and a guard test enforces that only the server bootstrapper still creates a database from the model.**

## Accomplishments

- `SqliteDesignDbContext` plus `SqliteDesignDbContextFactory` (Server); `dotnet ef migrations add Baseline` generated three files. Only `Up`/`Down` were edited: `Up` runs one `GuardedDdl` raw string (seven `CREATE TABLE IF NOT EXISTS`, two `CREATE INDEX IF NOT EXISTS`, text equal to the printed `migrations script 0 Baseline` plus the guards). `AzureDesignStudio.Services.csproj` gained no package reference.
- `DeployDbStartup.Migrate(db, deployDbPath, backupsDirectory, clock)`: existing file with pending migrations gets a `Premigration` backup via `WorkspaceBackup.CreateAsync(..., "deploy")` (thread-pool hop); then the four legacy columns (`AdsPlanApprovals.RevertPlanJson`, `AdsPlanApplyEvents.RevertPlanHash/RevertPlanJson/TargetScope`) are added where the table exists and no history table does; then `Migrate()`; then `PruneAsync` with `RetentionPolicy(3, 0)`, swallowing IO errors.
- `MauiProgram`: `AddDbContext<DesignDbContext, SqliteDesignDbContext>` with the same `UseSqlite` source, and the post-`Build()` model-creation scope block replaced by one `DeployDbStartup.Migrate` call into `<appData>/backups/deploy`. Workspace lines untouched.
- `NoOtherProjectCallsTheModelCreationMethod` scans Workspace, Desktop and Mcp (skipping bin, obj, Migrations).

## Task Commits

1. **Task 1 (tracer): SQLite migration set for the deploy database** - `5f362ac` (feat)
2. **Task 2: DeployDbStartup and the Desktop swap** - `cc3d5cd` (feat)
3. **Task 3: model-creation guard** - `161166a` (test)

## Verification

- Tracer gate: `DeployDbMigrationTests` (3) + `ServicesProjectStaysFreeOfWebHostAndProviderPackages`: 4 passed.
- `DeployDbMigrationTests`: 9 passed (plan required 7): fresh migrated file equals a model-built file; model-built file with a row adopts the baseline (one `_Baseline` history row, row intact); no pending model changes; old file (3 tables and 4 columns dropped, a row in each remaining table) upgrades with every row kept and a schema equal to a fresh migrated file; the premigration backup exists with stem `deploy`, manifest counts match the seed, `LatestMigrationId` null, `WorkspaceBackup.List(backups, "workspace")` and `List(backups/deploy, "workspace")` empty, Unix modes 0700/0600; failing backup throws `InvalidOperationException` and leaves the file byte-identical (SHA-256), no history table, no restored tables; a second `Migrate` takes no backup; a fresh file takes none; only the newest 3 deploy backups survive.
- `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst`: Build succeeded, 0 errors (one pre-existing `PublishFolderType` warning from the Mac SDK).
- `SourceGuardTests`: 6 passed. Mutations run and reverted: a probe `d.Database.EnsureCreated()` in `src/AzureDesignStudio.Desktop` fails the new guard; a probe `d.Database.Migrate()` there fails `MigrateIsCalledOnlyBySchemaOwners`.
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 208 passed, 0 failed, 8 skipped (the SQL Server container tests, which need Docker).
- `dotnet ef migrations has-pending-model-changes --context SqliteDesignDbContext ...`: "No changes have been made to the model since the last migration."
- Acceptance: 3 files in `Migrations/Sqlite`; `IF NOT EXISTS` 10 in the baseline; provider-package grep on Services 0; `DeployDbStartup.Migrate` 1 and `SqliteDesignDbContext` 1 in `MauiProgram.cs`; `"deploy"` 1 in `DeployDbStartup.cs`; model-creation callers in Workspace/Desktop/Mcp: 0.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Server.Tests lock file gained the Workspace project entry**
- **Found during:** Task 2
- **Issue:** the tests need `WorkspaceBackup`/`BackupManifest`, so Server.Tests gained a `ProjectReference` to Workspace; locked restore (CI) requires the lock file to list it. The plan's "no lock file modified" assumed no new reference.
- **Fix:** committed the one added `azuredesignstudio.workspace` project entry in `src/AzureDesignStudio.Server.Tests/packages.lock.json` (no package version changed). SDK drift in `packages.maccatalyst.lock.json` (215 lines) from the Desktop build was reverted.
- **Commit:** `cc3d5cd`

**2. [Rule 1 - Bug] The Migrate call-site guard flagged `DeployDbStartup.Migrate(`**
- **Found during:** Task 3 (the Task 2 filtered run did not include `SourceGuardTests`, so the Task 2 commit leaves that guard red until Task 3)
- **Issue:** `MauiProgram.cs` now contains `DeployDbStartup.Migrate(`, which the guard's regex treated as an EF migration call outside the allowed sites.
- **Fix:** a `(?<!DeployDbStartup)` lookbehind; the probe mutation in Desktop confirms a real `db.Database.Migrate()` still fails the guard.
- **Commit:** `161166a`

**3. [Shape choice] Old-file schema comparison ignores column position**
- SQLite can only append, so `TargetScope`/`RevertPlanHash`/`RevertPlanJson` end up after `TimestampUtc` while a fresh table has them in model order. The test compares the description without column ids (columns sorted per table); names, types, nullability, indexes and keys still compare.

## Not verified here

- The Windows MAUI target (`net10.0-windows10.0.19041.0`) was not compiled on this Mac; `DeployDbStartup.cs` has no platform conditionals and is compiled for Mac Catalyst and net10.0 (Server.Tests) only. The Windows desktop CI job is the check.
- The Desktop was not launched: `Migrate` is exercised on real SQLite files in tests, not through a running MAUI app, and Release trimming of the migration classes on Mac Catalyst (`UseInterpreter=true`) is not exercised.
- The `ClientSecret` plaintext-at-rest statement of the threat register is for the runbook (plan 04-12); nothing here writes it.
- The deploy backups' exclusion from manual restore and a future support bundle is by folder and stem only; those features do not exist yet (04-09/04-10 must keep reading the workspace stem only).

## Known Stubs

None.

## Threat Flags

None. T-04-27 (stem and folder separation, user-only modes, newest 3, tested), T-04-28 (backup first and abort tested, `IF NOT EXISTS` baseline, row retention tested), T-04-29 (constant identifiers, bound-parameter checks) and T-04-30 (Services guard green, factory in Server) are implemented; T-04-SC accepted (no package added).

## Self-Check: PASSED

- FOUND: SqliteDesignDbContext.cs, Migrations/Sqlite (3 files), SqliteDesignDbContextFactory.cs, DeployDbStartup.cs, DeployDbMigrationTests.cs
- FOUND commits: 5f362ac, cc3d5cd, 161166a
