---
phase: 03-server-foundation-and-integration-harness
plan: 05
subsystem: testing
tags: [xunit, grpc, sqlite, webapplicationfactory, crypto-seam, fault-injection]

requires:
  - phase: 03-server-foundation-and-integration-harness
    provides: AdsServerFactory, GrpcTestClients, DatabaseProvider and CryptoProvider seams (plans 03-02, 03-03)
provides:
  - "FailureHandlerFaultIsAnUnknownStatusAndTheHostKeepsServing: a dropped table under a running host is StatusCode.Unknown and Deploy/Cost keep answering"
  - "TempSqlite lifecycle tests: db file created under the OS temp dir, removed with side files on dispose, SQLite and InMemory hosts share nothing"
  - "Crypto-over-RPC tests: real CryptoService with the sample vault answers 503 in-band; FakeCryptoService through WithWebHostBuilder stores and lists the link"
affects: [03-06]

actuals:
  tokens: 6000
  tasks: 3
  commits: 3
plan_head_before: 8bda14f82a38fb4ef436a1f6777beab87446735c
plan_head_after: 9743062005e56bc0d61b52adfbecdfa61df8471f
commits: 3

tech-stack:
  added: []
  patterns:
    - "Destructive tests own a private AdsServerFactory; the shared class fixture is never damaged"
    - "Per-test service override on the shared fixture through factory.WithWebHostBuilder(ConfigureTestServices)"
    - "Cleanup assertions glob only the factory's own unique file name, never the whole temp directory"

key-files:
  created: []
  modified:
    - src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs

key-decisions:
  - "No production code touched: the host already maps a handler fault to StatusCode.Unknown and the real CryptoService already answers 503 in-band without a vault"

requirements-completed: []

coverage:
  - id: D1
    description: "A handler fault (AdsDesigns dropped under a running host) surfaces as StatusCode.Unknown, and the same host still answers an authenticated Deploy call with 200 and an anonymous Cost call with 400"
    requirement: "TEST-01"
    verification:
      - kind: integration
        ref: "ServerHostTests.FailureHandlerFaultIsAnUnknownStatusAndTheHostKeepsServing (filter ServerHostTests.Failure: 3 passed) Debug"
        status: pass
    human_judgment: false
  - id: D2
    description: "The temp SQLite database is created directly under the OS temp directory, deleted with its -wal/-shm/-journal side files when the factory is disposed, two SQLite hosts get two files and share no data, two InMemory hosts share no store and create no file"
    requirement: "TEST-01"
    verification:
      - kind: integration
        ref: "ServerHostTests.TempSqlite* (4 passed) Debug; ServerHostTests (10 passed at that point) Release"
        status: pass
    human_judgment: false
  - id: D3
    description: "Deploy.SaveSubscriptionInfo through the real host answers 503 in-band with the real CryptoService and the sample vault name, and 200 with a stored, listed link when FakeCryptoService is substituted"
    requirement: "TEST-01"
    verification:
      - kind: integration
        ref: "ServerHostTests (12 passed) Debug; Server.Tests full Release suite 184 passed, 0 failed"
        status: pass
    human_judgment: false

duration: 10min
completed: 2026-11-14
status: complete
---

# Phase 3 Plan 05: Fault, temp-file lifecycle and crypto-seam tests Summary

**Six more hosted-server tests: a handler fault is an RPC `Unknown` status while the host keeps serving, the temp SQLite database is created, isolated and removed with its side files, and the crypto seam answers 503 without a vault and 200 with a substituted fake.**

## Performance

- **Duration:** ~10 min
- **Completed:** 2026-11-14
- **Tasks:** 3
- **Files:** 1 modified (`ServerHostTests.cs`, tests only)

## Accomplishments

- Task 1 (tracer): `FailureHandlerFaultIsAnUnknownStatusAndTheHostKeepsServing` runs on its own `AdsServerFactory`, drops `AdsDesigns` through `ExecuteSqlRawAsync`, and asserts `Design.Save` throws an `RpcException` with `StatusCode.Unknown`; the same host then answers `Deploy.GetGuardrailPolicy` with 200 and an anonymous `Cost.GetRetailPrices` with 400. The tracer gate (re-run end-to-end, 3 passed) held before expanding.
- Task 2: `TempSqliteDisposeRemovesTheDatabaseFileAndItsSideFiles` (file exists and sits directly under `Path.GetTempPath()`, nothing matching the factory's unique name remains after dispose), `TempSqliteTwoHostsDoNotShareDataAndEachGetsItsOwnFile`, and `TempSqliteInMemoryHostsDoNotShareAStoreEither` (no file is created for InMemory hosts).
- Task 3: `DeploySaveSubscriptionWithoutAKeyVaultAnswers503InBand` on the shared fixture, and `DeploySaveSubscriptionWithAFakeCryptoServiceStoresTheLink` using `WithWebHostBuilder` + `ConfigureTestServices` to replace `ICryptoService`; `LoadSubscriptionInfo` lists exactly the saved id and name. No ARM-reaching RPC is called.
- Verification: `ServerHostTests` 12 passed (Debug); full `Server.Tests` Release run (`-c Release -p:RunAOTCompilation=false`) 184 passed, 0 failed. No `ads-test-*` file remained in the temp directory after the runs, and no `packages.lock.json` was rewritten.

## Task Commits

1. **Task 1: tracer, handler fault is an RPC status** - `19073e6` (test)
2. **Task 2: temp database lifecycle and host isolation** - `48a234b` (test)
3. **Task 3: crypto seam over RPC** - `9743062` (test)

## Decisions Made

See `key-decisions`. The plan prohibited production edits and none were needed.

## Deviations from Plan

None - plan executed exactly as written. (A `using Microsoft.AspNetCore.TestHost;` was added to the test file for `ConfigureTestServices`, as the plan implied.)

## Issues Encountered

None.

## Known Stubs

None.

## Threat Flags

None. T-03-15 (fault does not take the host down) is covered by the fault test; T-03-16 and T-03-17 by the lifecycle and isolation tests; T-03-18 by using the sample vault name (answered in-band before any network) and the in-memory fake. T-03-SC: no package added.

## Next Phase Readiness

`ServerHostTests` now holds 12 tests. Plan 03-06 can proceed; TEST-01 is intentionally not marked complete here, plan 03-06 closes it.

## Self-Check: PASSED

- File exists: `src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs`; all six new test names present.
- Commits 19073e6, 48a234b and 9743062 are ancestors of HEAD; `git rev-list --count 8bda14f..HEAD` was 3 before this docs commit.
- `git status --porcelain -- '*packages.lock.json'` empty.
