---
phase: 03-server-foundation-and-integration-harness
plan: 06
subsystem: testing
tags: [xunit, source-guards, regression, agents-md, documentation]

requires:
  - phase: 03-server-foundation-and-integration-harness
    provides: DatabaseBootstrapper single EnsureCreated call site, Program.cs without preprocessor conditionals, AdsServerFactory and TestAuthHandler (plans 03-02, 03-03)
provides:
  - "SourceGuardTests: four read-only source guards (no #if in Program.cs, EnsureCreated only in DatabaseBootstrapper, no test authentication in the production Server, Services csproj free of provider and ASP.NET Core packages)"
  - "AGENTS.md corrected (no InMemory-in-DEBUG, no reflection-only-in-DEBUG) and extended with Database:Provider, Crypto:Provider, Persistence/, the integration harness and the UseSetting pitfall"
affects: []

actuals:
  tokens: 9000
  tasks: 2
  commits: 2
plan_head_before: 3bceabdcecd0c12aea262ab2ab07ec6e5df5a524
plan_head_after: e85f85120132f6ff09e99dca0a3c775ab246e2b7
commits: 2

tech-stack:
  added: []
  patterns:
    - "Source guards locate the checkout by walking up from AppContext.BaseDirectory to global.json and throw (never pass) when it is missing"
    - "Guards report offenders as repository-relative path:line so a failure names the exact regression"

key-files:
  created:
    - src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs
  modified:
    - AGENTS.md

key-decisions:
  - "Guards match call shape (`.EnsureCreated(`) and line-start directives, so comments that merely mention the word do not trip them"

requirements-completed: [TEST-01]

coverage:
  - id: D1
    description: "CI fails if Program.cs regains a preprocessor conditional, EnsureCreated is called outside DatabaseBootstrapper, test authentication reaches the production Server, or the Services csproj takes a provider or ASP.NET Core dependency"
    requirement: "TEST-01"
    verification:
      - kind: unit
        ref: "SourceGuardTests (4 passed) Debug"
        status: pass
    human_judgment: false
  - id: D2
    description: "The first guard discriminates: appending '#if DEBUG / #endif' to Program.cs makes ProgramHasNoPreprocessorConditionals fail (names Program.cs:139-140); the file was restored and git diff is clean"
    requirement: "TEST-01"
    verification:
      - kind: other
        ref: "mutation probe: exit code 1, 'Failed ...ProgramHasNoPreprocessorConditionals', Program.cs restored via git checkout"
        status: pass
    human_judgment: false
  - id: D3
    description: "AGENTS.md matches the code: configuration-selected database and crypto providers, single bootstrap call site, Development-only reflection, harness and UseSetting pitfall"
    verification:
      - kind: other
        ref: "Task 2 automated grep block (stale sentences absent; Database:Provider, Crypto:Provider, AdsServerFactory, UseSetting, DatabaseBootstrapper, Server.Tests/Hosting present)"
        status: pass
    human_judgment: false
---

# Phase 3 Plan 06: Source guards and AGENTS.md Summary

**Four read-only source guards that fail CI if the phase's structural guarantees regress (the first one seen to go red), plus an AGENTS.md that describes the configuration-selected providers and the real-host test harness.**

## Performance

- **Duration:** ~10 min
- **Started:** 2026-11-14T10:26Z
- **Completed:** 2026-11-14T10:36Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- `SourceGuardTests` pins: no `#if`/`#elif`/`#else`/`#endif` in `Server/Program.cs`; `.EnsureCreated(` only in `Persistence/DatabaseBootstrapper.cs` across Server and Services; no `TestAuthHandler`, `x-test-user` or `x-test-no-scope` text under Server; Services csproj free of `Microsoft.EntityFrameworkCore.SqlServer`, `...Sqlite`, `Microsoft.AspNetCore`.
- Mutation check recorded: a temporary `#if DEBUG`/`#endif` appended to `Program.cs` turned `ProgramHasNoPreprocessorConditionals` red with the message naming `Program.cs:139` and `:140`; `Program.cs` restored, working tree clean.
- AGENTS.md: replaced the stale "InMemory EF Core in DEBUG" and "reflection only in DEBUG" notes; added the integration-harness note with the `ConfigureAppConfiguration` versus `UseSetting` pitfall, the shared-InMemory-store leak, and the SDK lock drift warning; added the `Persistence/` tree row, the Server.Tests `Hosting/` mention, the Testing-convention sentence and two WHERE TO LOOK rows. Generated header untouched.

## Task Commits

1. **Task 1: source guards plus mutation check** - `aa0635c` (test)
2. **Task 2: AGENTS.md correction and extension** - `e85f851` (docs)

## Phase gate

- `dotnet test src/AzureDesignStudio.Server.Tests` Debug: 188 passed, 0 failed.
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 188 passed, 0 failed.
- `dotnet restore src/AzureDesignStudio.Server.Tests --locked-mode`: the only error is the tolerated NU1004 for `src/AzureDesignStudio/AzureDesignStudio.csproj` (SDK 10.0.403 versus pinned 10.0.401, ILLink.Tasks 10.0.12 vs 10.0.14); none for Server or Server.Tests.
- `grep -cE '^#(if|else|elif|endif)' Program.cs`: 0.
- `git diff --name-only 8895f19~1..HEAD -- '*packages.lock.json'`: only the Server and Server.Tests locks.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Missing `using Xunit;` in SourceGuardTests**
- **Found during:** Task 1 first build (CS0246 on `Fact`)
- **Issue:** Server.Tests has no global xunit using; sibling tests import it explicitly.
- **Fix:** added `using Xunit;`
- **Files modified:** SourceGuardTests.cs (before the commit, so it is in `aa0635c`)

Otherwise none - plan executed as written. The new `Total: 188` suite count is the whole Server.Tests project.

## Issues Encountered

None beyond the tolerated SDK-drift NU1004 under locked restore; no lock file was rewritten in the working tree.

## Known Stubs

None.

## Threat Flags

None. T-03-19 is mitigated by the four guards and the recorded mutation run.

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs
- FOUND commits: aa0635c, e85f851
