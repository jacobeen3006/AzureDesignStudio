---
phase: 03-server-foundation-and-integration-harness
verified: 2026-11-14T00:00:00Z
status: passed
score: 8/8 must-haves verified
covered_files:
  - ".planning/phases/03-server-foundation-and-integration-harness/03-01-PLAN.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-01-SUMMARY.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-02-PLAN.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-02-SUMMARY.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-03-PLAN.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-03-SUMMARY.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-04-PLAN.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-04-SUMMARY.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-05-PLAN.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-05-SUMMARY.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-06-PLAN.md"
  - ".planning/phases/03-server-foundation-and-integration-harness/03-06-SUMMARY.md"
  - "AGENTS.md"
  - "Directory.Build.props"
  - "Directory.Build.targets"
  - "Directory.Packages.props"
  - "src/AzureDesignStudio.Server.Tests/Hosting/AdsServerFactory.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/CryptoProviderTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/DatabaseBootstrapperTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/DatabaseProviderTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/GrpcTestClients.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs"
  - "src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs"
  - "src/AzureDesignStudio.Server/Persistence/DatabaseProvider.cs"
  - "src/AzureDesignStudio.Server/Persistence/DesignDbContextFactory.cs"
  - "src/AzureDesignStudio.Server/Program.cs"
  - "src/AzureDesignStudio.Server/Properties/launchSettings.json"
  - "src/AzureDesignStudio.Server/Services/CryptoProvider.cs"
covered_digest: "v4:sha256:30551ff4d4b05e47381bbe39ebb1170d706c4916c1405b4fa992c9110207ddf5"
behavior_unverified: 0
overrides_applied: 0
---

# Phase 3: Server Foundation and Integration Harness Verification Report

**Phase Goal:** The server can be booted by a test, configured without Azure, and every RPC exercised in CI without credentials or a developer's database.
**Verified:** 2026-11-14
**Status:** passed
**Re-verification:** No, initial verification

## Goal Achievement

### Observable Truths (ROADMAP success criteria are the contract)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A server integration test boots the real host in CI with no Key Vault, no Azure credentials, and no `#if DEBUG` deciding providers | VERIFIED | `AdsServerFactory : WebApplicationFactory<Program>` boots the real `Program` with `Testing` environment, `KeyVaultName` pinned to empty, `Database:Provider=Sqlite`. `grep -rnE '^\s*#\s*(if\|elif\|else\|endif)'` over `src/AzureDesignStudio.Server` finds nothing. Providers come from `DatabaseProvider.Resolve` (config only) and `CryptoProvider.Register`. The vault is only built when `databaseKind == SqlServer && IsConfigured(KeyVaultName)`. Boot test `BootsWithTheCheckedInConfigurationAndRoundTripsADesign` passes. |
| 2 | A gRPC channel over TestServer reaches every service through a test auth handler; failures surface as RPC statuses, not host crashes | VERIFIED | `TestAuthHandler` (header driven) lives only in Server.Tests, and `ProductionServerHasNoTestAuthentication` guards that. `EveryServiceIsReachable` enumerates endpoints and finds exactly Cost, Deploy and Design, then calls each in-band. Anonymous call is `Unauthenticated`, no-scope call is `PermissionDenied`, and a dropped-table fault is `StatusCode.Unknown` with the same host still answering Deploy (200) and Cost (400). |
| 3 | A design-creating test writes to a temporary SQLite file; no test touches `workspace.db` or a live tenant | VERIFIED | `TempSqliteSaveCreatesTheDatabaseFileUnderTheTempDirectory` asserts `factory.DbFile` exists directly under `Path.GetTempPath()` and the provider name is Sqlite. Dispose test proves the file plus `-wal/-shm/-journal` are removed, and two hosts share no data. `grep` of `workspace.db`, `deploy.db` and `AppDataDirectory` in Server.Tests returns nothing. No Azure endpoint is called: crypto path answers 503 in-band, Cost answers 400 before the price API. |
| 4 | `Program` is reachable from the test project and the RPC hot path no longer calls `EnsureCreated` | VERIFIED | `public partial class Program { }` is the type argument of the factory. `EnsureCreated` appears in Server and Services only in `Persistence/DatabaseBootstrapper.cs`, called once from `Program.cs` after `Build()`. `EnsureCreatedIsCalledOnlyByTheDatabaseBootstrapper` guards it. The only other hits are Desktop's own one-time startup (`MauiProgram.cs`) and `Workspace`, neither a server RPC path. |
| 5 | Provider choice is configuration-only: `SqlServer` default, `Sqlite`, `InMemory`, case-insensitive; unknown value stops startup naming the key and values; missing connection string stops host before it serves | VERIFIED | `DatabaseProvider.Resolve/Configure` read; `DatabaseProviderTests` and `DatabaseBootstrapperTests` pin each rule. Development launch profiles set `Database__Provider=InMemory` as configuration. |
| 6 | Legacy T-SQL upgrade runs only for SQL Server, never SQLite, and is idempotent | VERIFIED | `NeedsLegacyUpgrade => database.IsSqlServer()`, gate has a unit test, bootstrap-twice test on SQLite passes. |
| 7 | `Crypto:Provider` accepts only `KeyVault`; seam works over RPC | VERIFIED | `CryptoProvider.Register`; `CryptoProviderTests`; real `CryptoService` answers 503 with the sample vault, a `FakeCryptoService` yields 200 plus listed link. |
| 8 | Build authority: only AutoMapper audit exceptions added, Mvc.Testing 10.0.15 central, Server references EF Sqlite and Core, only Server and Server.Tests locks changed | VERIFIED | `git diff d8dc9f6^ HEAD` over lock files and csproj shows only the Server and Server.Tests locks, two csproj files and `Directory.Packages.props`. `Directory.Build.props/targets` carry GHSA-gpwp-5797-mjff and GHSA-6vhm-j9h7-f9gh under the same expiry property (commit 8895f19). |

**Score:** 8/8 truths verified (0 present, behavior-unverified)

### Required Artifacts

| Artifact | Status | Details |
|----------|--------|---------|
| `Server/Program.cs` | VERIFIED | partial Program, no `#if`, `DatabaseBootstrapper.Initialize(app.Services)` after Build |
| `Server/Persistence/DatabaseProvider.cs` | VERIFIED | substantive, wired from Program and tests |
| `Server/Persistence/DatabaseBootstrapper.cs` | VERIFIED | sole `EnsureCreated` caller, SQL Server gated legacy DDL |
| `Server/Persistence/DesignDbContextFactory.cs` | VERIFIED | covered by `DatabaseBootstrapperTests` |
| `Server/Services/CryptoProvider.cs` | VERIFIED | wired in Program |
| `Server/Properties/launchSettings.json` | VERIFIED | `Database__Provider=InMemory` in both profiles |
| `Server.Tests/Hosting/*` (factory, auth handler, channel helper, 6 test classes) | VERIFIED | all present and passing |
| `AGENTS.md` | VERIFIED | documents `Database:Provider`, reflection gating, harness and `UseSetting` pitfall; no remaining "InMemory in DEBUG" claim |

### Key Link Verification

| From | To | Status |
|------|----|--------|
| Program.cs `public partial class Program` | `WebApplicationFactory<Program>` | WIRED |
| TestAuthHandler claims | `[Authorize]` and `[RequiredScope("AzureAd:Scopes")]` on Hosted services | WIRED (scope-mismatch test pins `Server.Access` to configured scopes) |
| Program.cs `Resolve` and `Configure(kind)` | AddDbContext callback | WIRED |
| Program.cs | `DatabaseBootstrapper.Initialize` | WIRED |
| SourceGuardTests | Program.cs and bootstrapper via `global.json` root walk | WIRED |

### Behavioral Spot-Checks and Test Runs

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Full Server.Tests, Debug | `dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false` | 192 passed, 0 failed | PASS |
| Full Server.Tests, Release (CI shape) | `-c Release -p:RunAOTCompilation=false` | 192 passed, 0 failed | PASS |
| Static guards | `--filter SourceGuardTests` | 4 of 4 passed | PASS |
| Guard discriminates | appended `#if DEBUG` / `#endif` to Program.cs, re-ran, restored with `git checkout` | `ProgramHasNoPreprocessorConditionals` FAILED as required, other 3 passed; clean re-run 4 of 4 | PASS |
| Working tree afterwards | `git status --short` | clean (no `src/AzureDesignStudio/packages.lock.json` drift this run) | PASS |

### Probe Execution

SKIPPED: no probe scripts are declared by the phase.

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|--------------|-------------|--------|----------|
| TEST-01 | 03-01 through 03-06 (all declare `requirements: [TEST-01]`) | A server integration suite boots the real host and runs in CI without Azure credentials | SATISFIED | Truths 1 to 4 above. REQUIREMENTS.md marks it Complete and maps it only to Phase 3. No orphaned requirement IDs. |

### Anti-Patterns Found

None blocking. No TBD/FIXME/XXX in the files modified. No stubs: all new classes are substantive and exercised.

### Review-Fix Judgment (03-REVIEW-FIX.md flags needing human verification)

**WR-01 (b46aaa8), `Database:Provider` resolved once.** Judgment: correct and safe. `Program.cs` calls `DatabaseProvider.Resolve` once into `databaseKind`, uses it for the vault decision and passes the same value into `DatabaseProvider.Configure(options, kind, config)` from the lazy `AddDbContext` callback. The two-argument overload remains a thin resolve-then-delegate wrapper, so the rule tests are unaffected. The vault and EF provider can no longer come from two reads. A late `ConfigureAppConfiguration` value is now ignored rather than silently disagreeing (documented in the code comment and AGENTS.md). Only residual trade-off: a `Database:Provider` supplied by the vault itself is not honoured, which is correct since the vault decision depends on it.

**WR-02 (615e452), reflection endpoint now `RequireAuthorization()`.** Judgment: correct and appropriately scoped. Reflection is still registered and mapped only under `IsDevelopment()` (config/environment, not `#if`), and the mapping now carries `RequireAuthorization()`, so a stray `ASPNETCORE_ENVIRONMENT=Development` on a deployed host no longer exposes the service surface to anonymous callers. Tests: `ReflectionIsNotMappedOutsideDevelopment` and `ReflectionInDevelopmentRequiresAnAuthenticatedCaller` (asserts `IAuthorizeData` metadata on every reflection endpoint). The test checks endpoint metadata rather than sending an anonymous call, which is the mechanism ASP.NET Core uses to enforce it, so I accept it. The default policy requires an authenticated user only, not the `Server.Access` scope, which is acceptable for a development-only diagnostic surface. Side effect to be aware of: local `grpcurl` and similar tools against a Development server now need a bearer token. Intentional and documented. No human blocker.

### Observations (non-blocking)

- "Every RPC" in the goal sentence is satisfied at service granularity per SC2: the TestServer channel reaches all three services with one in-band call each (Design Save/GetSaved/Load, Deploy GetGuardrailPolicy/SaveSubscriptionInfo/LoadSubscriptionInfo, Cost GetRetailPrices). The remaining RPCs (WhatIf, stacks, decom and others) are covered by the pre-existing direct-call tests (`WhatIfRpcTests`, `StackRpcTests`, `DeployServiceTests`), not through the full auth/interceptor pipeline. This matches the ROADMAP contract and is not a gap.
- Desktop `MauiProgram.cs` keeps its own one-time startup `EnsureCreated`; out of scope for the server hot-path criterion.

### Human Verification Required

None.

### Gaps Summary

No gaps. All four ROADMAP success criteria hold in the code, the full suite is green in Debug and Release (192 tests), the source guards pass and are shown to discriminate, and TEST-01 is accounted for.

---

_Verified: 2026-11-14_
_Verifier: Claude (gsd-verifier)_
