---
phase: 04-database-durability
plan: 01
subsystem: database
tags: [dotnet-ef, efcore, sqlite, fixtures, schema-parity, nuget-lock]
status: complete

requires:
  - phase: 03-server-foundation-and-integration-harness
    provides: design-time DesignDbContextFactory in the Server, locked-restore CI convention
provides:
  - Repository-root tool manifest pinning dotnet-ef 10.0.13
  - Microsoft.EntityFrameworkCore.Design 10.0.12 as one central package row, private to Workspace and Server
  - InternalsVisibleTo AzureDesignStudio.Core.Tests on AzureDesignStudio.Workspace
  - Four legacy workspace.db fixtures (L1 oldest, L2 pre-governance, L3 current legacy, L4 duplicates) as SQL scripts
  - WorkspaceFixtures loader (temp-directory only), TempWorkspaceDirectory, SqliteSchemaDescriber (pragma-based logical schema)
affects: [04-02, 04-03, 04-04, 04-05, 04-06, 04-07, 04-08, 04-09, 04-10, 04-11, 04-12, 04-13]

plan_head_before: cb97653ca744df858d8d899923163100cb98aee7
plan_head_after: 27aeccbef08cec532050770114195f1f756be891

actuals:
  tokens: 21500   # chars/4 over the 14-file diff of this plan (lock files included)
  tasks: 2
  commits: 2

tech-stack:
  added: [dotnet-ef 10.0.13 (tool), Microsoft.EntityFrameworkCore.Design 10.0.12 (design-time, PrivateAssets=all)]
  patterns:
    - "Legacy-shape fixtures are plain SQL scripts executed on a raw SqliteConnection, never binary database files"
    - "Schema parity is compared through pragma_table_info/index_list/index_info/foreign_key_list, never sqlite_master text"
    - "Fixture helpers refuse any write target outside Path.GetTempPath()"

key-files:
  created:
    - .config/dotnet-tools.json
    - src/AzureDesignStudio.Core.Tests/Fixtures/workspace-legacy-L1-oldest.sql
    - src/AzureDesignStudio.Core.Tests/Fixtures/workspace-legacy-L2-pre-governance.sql
    - src/AzureDesignStudio.Core.Tests/Fixtures/workspace-legacy-L3-current-legacy.sql
    - src/AzureDesignStudio.Core.Tests/Fixtures/workspace-legacy-L4-duplicates.sql
    - src/AzureDesignStudio.Core.Tests/Support/WorkspaceFixtures.cs
    - src/AzureDesignStudio.Core.Tests/Support/SqliteSchemaDescriber.cs
    - src/AzureDesignStudio.Core.Tests/WorkspaceFixtureShapeTests.cs
  modified:
    - Directory.Packages.props
    - src/AzureDesignStudio.Workspace/AzureDesignStudio.Workspace.csproj
    - src/AzureDesignStudio.Workspace/packages.lock.json
    - src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj
    - src/AzureDesignStudio.Server/packages.lock.json
    - src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj
  deleted:
    - src/AzureDesignStudio.Server/.config/dotnet-tools.json   # stale dotnet-ef 6.0.4 pin

key-decisions:
  - "Fixtures capture the DDL the unmodified WorkspaceDb emits (dumped from a fresh file), with the hand-written unnamed primary keys of NodePositions and DecomWindows and the ALTER-appended Scans columns reproduced for L3/L2/L4"
  - "The describer lists only origin='c' indexes and never reads sqlite_master.sql, so PK constraint naming cannot cause false parity failures"
  - "Newtonsoft.Json 13.0.3 -> 13.0.4 in the Server lock is a real consequence of the Design package's dependency range, kept (not SDK drift)"

patterns-established:
  - "Later plans load a fixture with WorkspaceFixtures.Load(name, TempWorkspaceDirectory.Path(...)), upgrade it, then assert SqliteSchemaDescriber.Describe equals a fresh file's"

requirements-completed: [DATA-01, DATA-02, DATA-08]  # foundation only; the orchestrator closes these at phase completion

duration: 25min
completed: 2026-11-14
---

# Phase 04 Plan 01: Migration tooling pin and legacy workspace fixtures Summary

**dotnet-ef 10.0.13 and the EF Design package are pinned and proven against the real Server model, and four legacy workspace.db fixtures plus a pragma-based schema describer now anchor every later "upgraded equals fresh" claim.**

## Performance

- **Duration:** about 25 min
- **Completed:** 2026-11-14
- **Tasks:** 2 (1 tracer, 1 TDD-flagged auto)
- **Files:** 14 changed (1 deleted, 1343 insertions)

## Accomplishments

- Root tool manifest at `.config/dotnet-tools.json` (dotnet-ef 10.0.13); the Server-directory manifest pinning EF 6.0.4 is removed so it can no longer shadow the root one. `dotnet ef --version` prints 10.0.13 and `dotnet ef dbcontext info --context DesignDbContext` reports `Microsoft.EntityFrameworkCore.SqlServer` through the existing design-time factory.
- `Microsoft.EntityFrameworkCore.Design` 10.0.12 is one central row, referenced with `PrivateAssets=all` only by Workspace and Server; only those two lock files changed.
- Four fixtures under `Core.Tests/Fixtures/`, copied to the test output: L1 (six model tables, `Scans` without the two later columns), L2 (L3 minus the governance table), L3 (the file as today's code leaves it, no migration history), L4 (L3 without the two unique indexes, holding a duplicate `Designs.Name` pair, a duplicate `ChangeFeed` pair with different times and a duplicate pair with identical times).
- `WorkspaceFixtureShapeTests` (13 cases across 6 facts/theories) pins L3 to a file created by an unmodified `new WorkspaceDb`. A mutation (dropping one index from the built L3 copy) made the parity and L2 tests fail, so the comparison is not vacuous.

## Task Commits

1. **Task 1: Tracer - pin dotnet-ef and the EF Design package** - `a972da0` (build)
2. **Task 2: Legacy fixtures, loader, describer, shape tests** - `27aeccb` (test)

## Package identity (T-04-01 / T-04-SC)

Checked against nuget.org before install: the flat-container index lists `dotnet-ef` 10.0.13 and `Microsoft.EntityFrameworkCore.Design` 10.0.12 (both also list later 10.0.14 and 10.0.15, so the pins are deliberate); the search entries for both are `verified: true`, owners include `Microsoft` (Design also lists `EntityFramework`, `aspnet`, `dotnetframework`). Recorded in the Task 1 commit message.

## Verification

- `dotnet tool restore && dotnet ef --version` -> 10.0.13
- `dotnet ef dbcontext info ...` -> provider `Microsoft.EntityFrameworkCore.SqlServer`
- `dotnet restore src/AzureDesignStudio.Server.Tests --locked-mode` -> only the tolerated pinned-SDK drift pair for `src/AzureDesignStudio/AzureDesignStudio.csproj` (NU1004 on `Microsoft.NET.ILLink.Tasks` 10.0.12 -> 10.0.14); nothing names the Server or Server.Tests lock
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "WorkspaceFixtureShapeTests|WorkspaceTests"` -> 28 passed (13 new, 15 existing), 0 failed
- Task 2 diff touches nothing under `src/AzureDesignStudio.Workspace`; `git status --porcelain -- '*packages.lock.json'` is empty

## Decisions Made

See `key-decisions` above. The notable one: Newtonsoft.Json moved 13.0.3 -> 13.0.4 in the Server lock because `Microsoft.EntityFrameworkCore.Design`'s dependency chain requires it; this is a genuine dependency effect, not SDK drift, so it stays.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking/convention] Reverted SDK-drift rewrite of the WASM client lock**
- **Found during:** Task 1 (non-locked restore on SDK 10.0.403)
- **Issue:** `src/AzureDesignStudio/packages.lock.json` was rewritten (`Microsoft.NET.ILLink.Tasks` and `Microsoft.AspNetCore.App.Internal.Assets` 10.0.12 -> 10.0.14), exactly the drift the plan names.
- **Fix:** `git checkout -- src/AzureDesignStudio/packages.lock.json`; only the Workspace and Server locks were staged.
- **Commit:** a972da0 (absent from it by design)

**2. [Rule 1 - Bug] Fixture header comments tripped the plan's own acceptance greps**
- **Found during:** Task 2
- **Issue:** my first headers named the migration-history table and (in L2) the governance table, which the acceptance criteria require to be absent from those scripts.
- **Fix:** reworded the comments before committing.
- **Commit:** 27aeccb

The task was TDD-flagged, but it pins existing behavior (no production change), so the tests passed on first run and there is no RED commit; the mutation check above stands in as proof the assertions can fail.

## Issues Encountered

None blocking. The throwaway DDL-dump test used to read today's real schema was deleted before commit.

## Known Stubs

None.

## Threat Flags

None. The only new surface is the fixture loader, which is the T-04-02 mitigation itself (temp-directory guard, asserted by `LoaderRefusesATargetOutsideTheTempDirectory`).

## Next Phase Readiness

Plans 04-02 onward can load any of the four shapes into a temp file, run the upgrade, and compare with `SqliteSchemaDescriber.Describe` of a fresh file. `InternalsVisibleTo` is in place for internal Workspace seams.

## Self-Check: PASSED

- FOUND: `.config/dotnet-tools.json`, the four `Fixtures/*.sql`, `Support/WorkspaceFixtures.cs`, `Support/SqliteSchemaDescriber.cs`, `WorkspaceFixtureShapeTests.cs`
- FOUND commits: a972da0, 27aeccb
