---
phase: 03-server-foundation-and-integration-harness
plan: 03
subsystem: infra
tags: [ef-core, sqlite, configuration, bootstrap, crypto-seam, webapplicationfactory]

requires:
  - phase: 03-server-foundation-and-integration-harness
    provides: WebApplicationFactory harness and AdsServerFactory (plan 03-02)
provides:
  - "DatabaseProvider: Database:Provider (SqlServer default, Sqlite, InMemory) selects the EF provider by configuration; Debug and Release identical"
  - "DatabaseBootstrapper.Initialize: the only EnsureCreated caller; legacy T-SQL DDL moved verbatim and gated on IsSqlServer"
  - "CryptoProvider: Crypto:Provider seam, KeyVault the only accepted value"
  - "Program.cs with no preprocessor conditional; reflection gated by IsDevelopment()"
  - "AdsServerFactory on a unique temp SQLite file (or its own InMemory name) with pool-clearing cleanup"
affects: [03-04, 03-05, 03-06, 04-migrations, 05-credentials]

actuals:
  tokens: 9000
  tasks: 3
  commits: 3
plan_head_before: 3eec735ac35dde660f3c94100044d51b7d29497b
plan_head_after: 5b28dddd1c15752c2b2566efac52c15df22e72af
commits: 3

tech-stack:
  added: []
  patterns:
    - "Top-level Program.cs reads Database:Provider and Crypto:Provider once (tests supply them with UseSetting); the connection string is read lazily inside the AddDbContext callback"
    - "Key Vault configuration source only for the SqlServer provider"

key-files:
  created:
    - src/AzureDesignStudio.Server/Persistence/DatabaseProvider.cs
    - src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs
    - src/AzureDesignStudio.Server/Services/CryptoProvider.cs
  modified:
    - src/AzureDesignStudio.Server/Program.cs
    - src/AzureDesignStudio.Server/Properties/launchSettings.json
    - src/AzureDesignStudio.Server.Tests/Hosting/AdsServerFactory.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs

key-decisions:
  - "Default provider stays SqlServer so a forgotten setting fails fast instead of silently losing data"
  - "Legacy DDL guard is IsSqlServer, not IsRelational: it is T-SQL and SQLite rejects it"
  - "Development zero-setup is configuration (Database__Provider=InMemory in the two Development launch profiles), not compilation"
  - "Crypto:Provider accepts only KeyVault in this phase; development provider and production refusal stay with Phase 5 (CRED-01, CRED-02)"

requirements-completed: []

coverage:
  - id: D1
    description: "A design saved through the real host lands in a unique temp SQLite file chosen by Database:Provider, in Debug and Release; the file is removed on factory disposal"
    requirement: "TEST-01"
    verification:
      - kind: unit
        ref: "ServerHostTests.TempSqliteSaveCreatesTheDatabaseFileUnderTheTempDirectory and the other five ServerHostTests (Debug and Release)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Program.cs has no preprocessor conditional; EnsureCreated has a single caller; legacy DDL identical to the parent commit's and gated on SQL Server"
    verification:
      - kind: unit
        ref: "grep -cE '^#(if|else|elif|endif)' Program.cs = 0; cmp of extracted DDL against 11ccf98 (68 lines identical); full Release suite Passed 140, Failed 0"
        status: pass
    human_judgment: false
  - id: D3
    description: "Crypto registration selected by Crypto:Provider with KeyVault as the only value; host boots with no vault"
    verification:
      - kind: unit
        ref: "ServerHostTests (6) Debug and full Release suite (140)"
        status: pass
    human_judgment: false

duration: 20min
completed: 2026-11-14
status: complete
---

# Phase 3 Plan 03: Database, bootstrap and crypto by configuration Summary

**`Database:Provider` (SqlServer default, Sqlite, InMemory) now picks the server database in every build configuration, the schema bootstrap lives in `DatabaseBootstrapper`, `Crypto:Provider` has its seam, and the test host runs on a unique temporary SQLite file that is deleted on disposal.**

## Performance

- **Duration:** ~20 min
- **Completed:** 2026-11-14
- **Tasks:** 3
- **Files modified:** 7 (3 new production, 2 edited production, 2 test)

## Accomplishments

- Task 1 (tracer): `DatabaseProvider.Resolve/Configure` replaces the `#if DEBUG` / `#else` provider block. Key Vault loads only for SqlServer with a real `KeyVaultName`. The `AddDbContext` callback is lazy, so the vault or late-configured connection string is honoured and a missing one still stops the host in the startup bootstrap. The temporary guard on the legacy DDL moved from `IsRelational` to `IsSqlServer`. `AdsServerFactory` gets `DatabaseProviderName` (default Sqlite), `DbFile`, `InMemoryName`, and a `ClearAllPools` plus side-file cleanup on dispose. New test `TempSqliteSaveCreatesTheDatabaseFileUnderTheTempDirectory` passed in Debug and Release.
- Task 2: `DatabaseBootstrapper.Initialize(app.Services)` after `Build()` warns once for InMemory or Sqlite, calls `EnsureCreated`, and runs the legacy DDL only on SQL Server. The DDL is byte-identical (modulo leading whitespace) to the parent commit's: the extract comparison against `11ccf98` printed 68 identical lines. Reflection is gated by `IsDevelopment()`; both Development launch profiles set `Database__Provider=InMemory`. `Program.cs` has zero preprocessor directives.
- Task 3: `CryptoProvider.Register` reads `Crypto:Provider`; blank or `KeyVault` registers `CryptoService`, anything else throws naming the key, the value and `KeyVault`. `CryptoService` untouched.
- Full Release suite (the CI shape, `-p:RunAOTCompilation=false`): 140 passed, 0 failed. Debug: 140 passed.

## Task Commits

1. **Task 1: tracer, configuration-selected temp SQLite round trip** - `cdfb79b` (feat)
2. **Task 2: DatabaseBootstrapper and no preprocessor conditionals** - `b111a1e` (refactor)
3. **Task 3: Crypto:Provider seam** - `5b28ddd` (feat)

## Decisions Made

See `key-decisions`. All follow the plan.

## Deviations from Plan

### Minor adjustments (no rule triggered)

- Task 2 also removed `using Microsoft.EntityFrameworkCore;` from `Program.cs` (the plan asks to drop unneeded usings); this is a plain consequence of the move.
- The Debug-build bootstrap in the Task 1 interim state still skipped the legacy DDL (the `#if !DEBUG` block remained until Task 2), as the plan sequences it; Release host tests on SQLite proved the `IsSqlServer` guard.

Environment note: with SDK 10.0.403 vs the 10.0.401 pin, no `packages.lock.json` was rewritten in this plan's runs; `git status --porcelain -- '*packages.lock.json'` was empty before each commit.

## Issues Encountered

None. (A macOS `sed -i` flag mistake while removing a using was fixed by editing with Python; nothing was committed from it.)

## Known Stubs

None.

## Threat Flags

None. The only committed addition outside code is the provider name `InMemory` in `launchSettings.json` (T-03-12); no connection string, key or vault value was written to any committed file. `Database:Provider` is an allow-list (T-03-07), the legacy T-SQL is SQL-Server-only (T-03-08), the vault source is SqlServer-only (T-03-10), and the crypto seam admits only `KeyVault` (T-03-11). No package was added (T-03-SC).

## Next Phase Readiness

Plan 03-04 can unit-test `DatabaseProvider.Resolve/Configure` and the bootstrapper's warnings and SQLite path: all three types are `internal` and visible to `Server.Tests`. TEST-01 is intentionally not marked complete here; plan 03-06 closes it.

## Self-Check: PASSED

- Files exist: DatabaseProvider.cs, DatabaseBootstrapper.cs, CryptoProvider.cs (Server), SUMMARY.
- Commits cdfb79b, b111a1e and 5b28ddd are ancestors of HEAD; `git rev-list --count 3eec735..HEAD` was 3 before this docs commit.
- `grep -cE '^#(if|else|elif|endif)' Program.cs` = 0; `.EnsureCreated(` appears only in `Persistence/DatabaseBootstrapper.cs`; `IF OBJECT_ID` count in bootstrapper = 11 and in Program.cs = 0; `git status --porcelain -- '*packages.lock.json'` empty.
