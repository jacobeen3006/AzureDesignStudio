---
phase: 03-server-foundation-and-integration-harness
fixed_at: 2026-11-14T00:00:00Z
review_path: .planning/phases/03-server-foundation-and-integration-harness/03-REVIEW.md
iteration: 1
findings_in_scope: 4
fixed: 4
skipped: 0
status: all_fixed
---

# Phase 03: Code Review Fix Report

**Fixed at:** 2026-11-14
**Source review:** .planning/phases/03-server-foundation-and-integration-harness/03-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 4 (WR-01..WR-04; Info findings IN-01..IN-06 are out of scope)
- Fixed: 4
- Skipped: 0

Verification ran in the main checkout (no worktree, as instructed by the caller), with
`dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false`: 192 passing
(188 before, plus 4 new tests). The SDK-induced drift in `src/AzureDesignStudio/packages.lock.json`
was reverted before each commit.

## Fixed Issues

### WR-01: `Database:Provider` is resolved twice and the two reads can diverge

**Files modified:** `src/AzureDesignStudio.Server/Program.cs`, `src/AzureDesignStudio.Server/Persistence/DatabaseProvider.cs`
**Commit:** b46aaa8
**Status:** fixed: requires human verification (logic change)
**Applied fix:** Added `DatabaseProvider.Configure(options, DatabaseKind, configuration)`. `Program.cs` resolves the kind once (before the vault decision) and passes that value to the `AddDbContext` callback. The two-argument `Configure` stays as a thin wrapper that resolves and delegates, so the existing tests that call it directly are unchanged.

### WR-02: gRPC reflection gated only on the environment name

**Files modified:** `src/AzureDesignStudio.Server/Program.cs`, `src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs`, `AGENTS.md`
**Commit:** 615e452
**Status:** fixed: requires human verification (security gate)
**Applied fix:** `MapGrpcReflectionService()` now has `.RequireAuthorization()` (the review's alternative, chosen over a new config key as the minimal change). Reflection stays Development-only. Two tests were added: reflection is not mapped in the `Testing` host, and a host switched to `Development` maps the reflection endpoint with `IAuthorizeData` metadata. The second test was confirmed to fail with the fix removed. AGENTS.md notes that grpcurl now needs a bearer token.

### WR-03: SQL Server legacy-DDL gate has no positive test

**Files modified:** `src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs`, `src/AzureDesignStudio.Server.Tests/Hosting/DatabaseBootstrapperTests.cs`
**Commit:** 7554107
**Applied fix:** Extracted `internal static bool NeedsLegacyUpgrade(DatabaseFacade)` (still `IsSqlServer()`) and made `LegacySqlServerUpgrade` internal. New connection-free tests assert the gate is true for the design-time SQL Server context and false for Sqlite and InMemory, and that the statement still holds the guarded `OBJECT_ID`/`COL_LENGTH` DDL (all five `CREATE TABLE`s behind an `IS NULL` guard). Release/SqlServer behavior is unchanged.

### WR-04: `AdsServerFactory` is not hermetic against ambient environment variables

**Files modified:** `src/AzureDesignStudio.Server.Tests/Hosting/AdsServerFactory.cs`
**Commit:** 0209c81
**Applied fix:** The factory pins `KeyVaultName`, `KeyVaultKeyName`, `ApplicationInsights:ConnectionString` and `APPLICATIONINSIGHTS_CONNECTION_STRING` to empty with `UseSetting` and with a last-added in-memory configuration layer (which beats environment variables for lazily read values). `Database:Provider` was already set through `UseSetting`. Verified by running the whole suite with `KeyVaultName`, `KeyVaultKeyName`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, `Database__Provider=SqlServer` and `ConnectionStrings__ads-main` exported: 192 passing.

---

_Fixed: 2026-11-14_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
