---
phase: 03-server-foundation-and-integration-harness
plan: 04
subsystem: testing
tags: [ef-core, sqlite, xunit, design-time-factory, configuration]

requires:
  - phase: 03-server-foundation-and-integration-harness
    provides: DatabaseProvider, DatabaseBootstrapper, CryptoProvider seams (plan 03-03)
provides:
  - "DatabaseBootstrapperTests: schema on a temp SQLite file through DI, idempotence, Sqlite/InMemory warnings, missing-connection-string message, design-time factory"
  - "DatabaseProviderTests: Resolve and Configure pinned rule by rule"
  - "CryptoProviderTests: Register accepted values and rejection message"
  - "DesignDbContextFactory: dotnet ef design-time entry point (SQL Server model, no connection)"
affects: [03-05, 03-06, 04-migrations]

actuals:
  tokens: 9000
  tasks: 3
  commits: 3
plan_head_before: f408b88aee4796720a8f632b17b207668950199a
plan_head_after: c3107faa58ded9c971e31da79536bd1de79ded89
commits: 3

tech-stack:
  added: []
  patterns:
    - "Internal enum DatabaseKind is reached from public xunit signatures by carrying its name as a string"
    - "Design-time model inspected through IDesignTimeModel to read relational check constraints without a connection"

key-files:
  created:
    - src/AzureDesignStudio.Server/Persistence/DesignDbContextFactory.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/DatabaseBootstrapperTests.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/DatabaseProviderTests.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/CryptoProviderTests.cs
  modified: []

key-decisions:
  - "DesignDbContextFactory targets SQL Server on LocalDB with integrated security so the model it builds carries ADS_CHECK_JSON; the string is never opened"
  - "No EF Design package added; the factory interface compiles against the EF Core package the Server already references (Phase 4 owns the Design package)"

requirements-completed: []

coverage:
  - id: D1
    description: "DatabaseBootstrapper.Initialize through DI on a temp SQLite file creates all seven tables, runs no T-SQL, is idempotent, warns for Sqlite/InMemory and stops with the operator message when no connection string is set"
    requirement: "TEST-01"
    verification:
      - kind: unit
        ref: "DatabaseBootstrapperTests (5 tests) Debug"
        status: pass
    human_judgment: false
  - id: D2
    description: "DatabaseProvider.Resolve/Configure and CryptoProvider.Register rules each pinned (default, case-insensitivity, allow-list, ads-main precedence, sample values, InMemory isolation, accepted crypto value)"
    verification:
      - kind: unit
        ref: "DatabaseProviderTests + CryptoProviderTests (32 passed incl. theory rows) Debug"
        status: pass
    human_judgment: false
  - id: D3
    description: "Design-time factory reports SQL Server, builds the model with ADS_CHECK_JSON, holds no credential, never connects"
    verification:
      - kind: unit
        ref: "DatabaseBootstrapperTests.DesignTimeFactoryReportsSqlServerAndBuildsTheModelWithoutConnecting; full Release suite 178 passed, 0 failed"
        status: pass
    human_judgment: false

duration: 15min
completed: 2026-11-14
status: complete
---

# Phase 3 Plan 04: Provider, bootstrap and crypto rule tests plus design-time factory Summary

**The three configuration seams from plan 03-03 are pinned rule by rule (including their failure messages), the bootstrapper is proven on a real temp SQLite file wired the way `Program.cs` wires it, and `DesignDbContextFactory` gives `dotnet ef` a connectionless SQL Server model entry point.**

## Performance

- **Duration:** ~15 min
- **Completed:** 2026-11-14
- **Tasks:** 3
- **Files:** 4 new (1 production, 3 test)

## Accomplishments

- Task 1 (tracer): `DatabaseBootstrapperTests` builds a `ServiceCollection` with `AddDbContext` + `DatabaseProvider.Configure` and calls `DatabaseBootstrapper.Initialize`. Five tests: all seven `DbSet`s answer on a fresh SQLite file (no T-SQL syntax error), a second `Initialize` keeps a stored design, Sqlite and InMemory warnings are logged, and an empty configuration throws with `No database is configured` and `ConnectionStrings__ads-main`. Each test owns a unique temp file, cleared with `ClearAllPools` and side-file deletion on dispose.
- Task 2: `DatabaseProviderTests` (default SqlServer, blank, case-insensitive and padded values, unknown value message, per-provider `ProviderName`, `ads-main` over `ConnectionStrings`, missing and `<connection string>` samples, InMemory shared/isolated by name, `adsdb-inmem` constant) and `CryptoProviderTests` (descriptor is a singleton `CryptoService` for unset/blank/`KeyVault`; `DevKeys` is rejected naming key, value and `KeyVault`). 32 tests including theory rows.
- Task 3: `DesignDbContextFactory` in the Server (`UseSqlServer` on LocalDB, integrated security, no credential, no new package). The test checks the provider, the absence of `Password`/`User ID`, `Integrated Security`, the `DesignModel` entity, and the `ADS_CHECK_JSON` constraint read from the design-time relational model.
- Verification: Debug filters green (5, 32, 6); full Release suite (`-p:RunAOTCompilation=false`) 178 passed, 0 failed. No `packages.lock.json` was rewritten.

## Task Commits

1. **Task 1: tracer, bootstrap on temp SQLite through DI** - `d285b99` (test)
2. **Task 2: DatabaseProvider and CryptoProvider rules** - `826df2d` (test)
3. **Task 3: design-time DbContext factory** - `c3107fa` (feat)

## Decisions Made

See `key-decisions`. Both follow the plan.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Internal enum in a public test signature**
- **Found during:** Task 2
- **Issue:** `DatabaseKind` is `internal`, so a `[Theory]` method taking it as a parameter failed with CS0051 (inconsistent accessibility); xunit requires public test classes.
- **Fix:** The theory carries the expected kind as its name (`string`) and compares with `Resolve(...).ToString()`. Assertions are unchanged in strength.
- **Files modified:** `DatabaseProviderTests.cs`
- **Commit:** `826df2d` (fixed before the commit)

Otherwise the plan executed as written. The check constraint is read through `IDesignTimeModel` and `GetRelationalModel().Tables[].CheckConstraints`, one of the two routes the plan allowed; it compiled without an obsolete warning.

## Issues Encountered

None.

## Known Stubs

None.

## Threat Flags

None. T-03-13: the factory string has integrated security only and a test asserts it carries no password or user id and is never opened. T-03-14: each provider rule has its own test and the operator message is asserted by fragment. T-03-SC: no package added (`grep -c Microsoft.EntityFrameworkCore.Design` on the Server csproj is 0).

## Next Phase Readiness

Plans 03-05 and 03-06 can proceed; this plan touched no file of theirs. TEST-01 is intentionally not marked complete here; plan 03-06 closes it. Phase 4 can add the EF Design package and generate migrations against `DesignDbContextFactory`.

## Self-Check: PASSED

- Files exist: DesignDbContextFactory.cs, DatabaseBootstrapperTests.cs, DatabaseProviderTests.cs, CryptoProviderTests.cs.
- Commits d285b99, 826df2d and c3107fa are ancestors of HEAD; `git rev-list --count f408b88..HEAD` was 3 before this docs commit.
- `grep -ci password` on the factory = 0; `git status --porcelain -- '*packages.lock.json'` empty.
