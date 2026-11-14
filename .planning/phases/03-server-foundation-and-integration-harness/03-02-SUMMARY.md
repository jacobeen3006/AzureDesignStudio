---
phase: 03-server-foundation-and-integration-harness
plan: 02
subsystem: testing
tags: [webapplicationfactory, grpc, testserver, authentication, required-scope, xunit]

requires:
  - phase: 03-server-foundation-and-integration-harness
    provides: Mvc.Testing in Server.Tests, restore-to-green (plan 03-01)
provides:
  - "public partial class Program: the real server is a WebApplicationFactory<Program> type argument"
  - "AdsServerFactory, TestAuthHandler, GrpcTestClients.CreateGrpcChannel in Server.Tests/Hosting"
  - "ServerHostTests: boot on checked-in sample config, design round trip, Unauthenticated, PermissionDenied, scope drift, three-service surface"
affects: [03-03, 03-04, 03-05, 03-06, 05-credentials]

actuals:
  tokens: 2200
  tasks: 2
  commits: 2
plan_head_before: be7f907f12f855f7948b78e0cd0dc94694016359
plan_head_after: a31d3be38b00734ebc566e9e3765eca31a7ecf10
commits: 2

tech-stack:
  added: []
  patterns:
    - "Header-driven test auth scheme lives only in the test assembly; the channel helper targets WebApplicationFactory<Program> so WithWebHostBuilder factories can reuse it"
    - "UseSetting (not ConfigureAppConfiguration) for values top-level Program.cs reads"

key-files:
  created:
    - src/AzureDesignStudio.Server.Tests/Hosting/AdsServerFactory.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/GrpcTestClients.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs
  modified:
    - src/AzureDesignStudio.Server/Program.cs

key-decisions:
  - "Factory runs on Database:Provider=InMemory via UseSetting in both configurations; plan 03-03 moves the database choice to configuration and a temp SQLite file"
  - "Environment is Testing, not the factory default Development"

requirements-completed: [TEST-01]

coverage:
  - id: D1
    description: "Real Program boots under WebApplicationFactory on the checked-in sample AzureAd/KeyVaultName/ApplicationInsights values; authenticated Design save, list and load round trip through TestServer"
    requirement: "TEST-01"
    verification:
      - kind: unit
        ref: "AzureDesignStudio.Server.Tests.Hosting.ServerHostTests.BootsWithTheCheckedInConfigurationAndRoundTripsADesign (Debug and Release)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Real pipeline enforces authentication and [RequiredScope]: anonymous is Unauthenticated, authenticated without the scope claim is PermissionDenied on Design and Deploy; issued scope equals AzureAd:Scopes"
    requirement: "TEST-01"
    verification:
      - kind: unit
        ref: "ServerHostTests.FailureAnonymousCallerIsUnauthenticated, FailureAuthenticatedCallerWithoutTheRequiredScopeIsPermissionDenied, BootsTestScopeMatchesTheConfiguredScope"
        status: pass
    human_judgment: false
  - id: D3
    description: "Exactly Cost, Deploy and Design are mapped; each answers one in-band call without reaching Azure or the price API"
    requirement: "TEST-01"
    verification:
      - kind: unit
        ref: "ServerHostTests.EveryServiceIsReachable; full suite dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false (Passed: 139, Failed: 0)"
        status: pass
    human_judgment: false

duration: 15min
completed: 2026-11-14
status: complete
---

# Phase 3 Plan 02: Server host harness Summary

**The real server now boots under `WebApplicationFactory<Program>` on the checked-in sample configuration, and a `GrpcChannel` over TestServer round-trips a design as a header-authenticated test user, with `Unauthenticated`, `PermissionDenied`, scope drift and the three-service surface pinned.**

## Performance

- **Duration:** ~15 min
- **Completed:** 2026-11-14
- **Tasks:** 2
- **Files modified:** 5 (1 production, 4 new test files)

## Accomplishments

- Task 1 (tracer): `public partial class Program { }` is the only production edit. `AdsServerFactory` uses environment `Testing`, `UseSetting` for `Database:Provider=InMemory` and an empty App Insights connection string, and replaces the default scheme with `TestAuthHandler` in `ConfigureTestServices`. No `KeyVaultName` or `AzureAd` value is set, so the boot proves no vault or credential is needed. The save/list/load round trip passed in Debug and Release (`-p:RunAOTCompilation=false`) before any expansion.
- Task 2: four more tests. An anonymous call is `Unauthenticated`. An authenticated call without the scope claim is `PermissionDenied` for both Design and Deploy, so `[RequiredScope]` is enforced by the real host. The handler's scope value must equal the host's `AzureAd:Scopes`. Endpoint enumeration (`EndpointDataSource` + `GrpcMethodMetadata`) finds exactly Cost, Deploy and Design, each answering one in-band call (Design list 200, Deploy guardrail read 200, anonymous Cost with an empty request 400).
- Full Release suite: 139 passed (134 existing + 5 new).

## Task Commits

1. **Task 1: tracer - boot the real server and round-trip a design** - `239e98b` (test)
2. **Task 2: auth contract and three-service surface** - `a31d3be` (test)

## Decisions Made

See `key-decisions`. Both follow the plan and research (InMemory is the database both build configurations take today; 03-03 changes that).

## Deviations from Plan

### Minor sequencing note

**1. [Scope ordering] No-scope switch landed with the tracer**
- Task 2's action adds `NoScopeHeader` to `TestAuthHandler` and `withScope` to `CreateGrpcChannel`. Both were written in Task 1 because the handler and helper were created there and the two features are two lines each. Task 2's commit therefore touches only `ServerHostTests.cs`, not the three files listed in its `<files>`. Behaviour and acceptance greps are unchanged.

### Auto-fixed Issues

**2. [Rule 3 - Blocking] xUnit2000 on argument order**
- **Found during:** Task 2 (build under warnings-as-errors)
- **Issue:** `Assert.Equal(configuration[...], TestAuthHandler.ScopeValue)` has the constant as the actual argument.
- **Fix:** swapped to expected-first.
- **Files modified:** `src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs`
- **Commit:** `a31d3be` (fixed before the commit)

Environment note (not a deviation): SDK 10.0.403 vs the 10.0.401 pin rewrote `src/AzureDesignStudio/packages.lock.json` on each test run; it was reverted with `git checkout -- <file>` before each commit and never staged.

## Issues Encountered

None.

## Known Stubs

None.

## Threat Flags

None. The test-only authentication scheme exists solely in `AzureDesignStudio.Server.Tests` (T-03-03: the acceptance grep for `TestAuthHandler|x-test-user|x-test-no-scope` under `src/AzureDesignStudio.Server` finds nothing); no test file names the price API, ARM or Key Vault host (T-03-04); the App Insights connection string is blanked (T-03-05); `[Authorize]` and `[RequiredScope]` are asserted against the real pipeline (T-03-06).

## Next Phase Readiness

Plan 03-03 can switch the host's database choice to configuration and move this factory onto a temporary SQLite file. `GrpcTestClients.CreateGrpcChannel` extends the base factory type, so a `WithWebHostBuilder` factory can use it.

## Self-Check: PASSED

- Created files exist: AdsServerFactory.cs, TestAuthHandler.cs, GrpcTestClients.cs, ServerHostTests.cs (under `src/AzureDesignStudio.Server.Tests/Hosting/`).
- Commits 239e98b and a31d3be are ancestors of HEAD; `git rev-list --count be7f907..HEAD` is 2 before this docs commit.
- `grep -c 'public partial class Program'` is 1; guard greps over `src/AzureDesignStudio.Server` are empty; `[Fact]` count is 5; `git status --porcelain -- '*packages.lock.json'` is empty.
- `dotnet test ... --filter ServerHostTests` Debug: Passed 5; Release full suite: `Passed! Failed: 0, Passed: 139, Total: 139`.
