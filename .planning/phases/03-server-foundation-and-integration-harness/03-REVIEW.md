---
phase: 03-server-foundation-and-integration-harness
reviewed: 2026-11-14T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - AGENTS.md
  - Directory.Build.props
  - Directory.Build.targets
  - Directory.Packages.props
  - src/AzureDesignStudio.Server.Tests/AzureDesignStudio.Server.Tests.csproj
  - src/AzureDesignStudio.Server.Tests/Hosting/AdsServerFactory.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/CryptoProviderTests.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/DatabaseBootstrapperTests.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/DatabaseProviderTests.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/GrpcTestClients.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs
  - src/AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs
  - src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj
  - src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs
  - src/AzureDesignStudio.Server/Persistence/DatabaseProvider.cs
  - src/AzureDesignStudio.Server/Persistence/DesignDbContextFactory.cs
  - src/AzureDesignStudio.Server/Program.cs
  - src/AzureDesignStudio.Server/Properties/launchSettings.json
  - src/AzureDesignStudio.Server/Services/CryptoProvider.cs
findings:
  critical: 0
  warning: 4
  info: 6
  total: 10
  necessity: 0
status: issues_found
---

# Phase 03: Code Review Report

**Reviewed:** 2026-11-14
**Depth:** standard
**Files Reviewed:** 19 (lock files excluded)
**Status:** issues_found

## Summary

I compared the new `Program.cs`, `DatabaseProvider`, `DatabaseBootstrapper` and `CryptoProvider` against the pre-phase `Program.cs` (1c59866). I found no blockers.

- **Release / SQL Server path:** it is behavior-equivalent. The vault source is still loaded when `KeyVaultName` is real, the connection-string precedence is unchanged (`ads-main` first, then `ConnectionStrings:ads-main`), and the legacy DDL runs only when `IsSqlServer()` is true.
- **Test auth handler:** it exists only in `Server.Tests`. Nothing in `Server` references it, and `SourceGuardTests` checks that.
- **Secrets:** no connection string or secret reaches any log line or exception message.

The findings below are:
- one production hardening regression (gRPC reflection, WR-02);
- one trap in the configuration design (WR-01);
- two test-harness gaps (WR-03, WR-04): the SQL Server DDL gate has no positive test, and the factory is not hermetic against environment variables.

No necessity findings. I did not load `necessity-review.md` because I had nothing to classify.

## Warnings

### WR-01: `Database:Provider` is resolved twice and the two reads can diverge

**File:** `src/AzureDesignStudio.Server/Program.cs:39` and `src/AzureDesignStudio.Server/Persistence/DatabaseProvider.cs:47`
**Issue:** `Program.cs` calls `DatabaseProvider.Resolve(builder.Configuration)` before `Build()` to decide on the vault. `DatabaseProvider.Configure` then calls `Resolve` again against the final `IConfiguration` when the first context is built.

Any source added after the top-level read can change the answer. Examples are `ConfigureAppConfiguration`, a later config provider, or the vault source itself. The vault decision and the EF provider are then taken from different values.

Example: top-level sees the default `SqlServer` with a real `KeyVaultName` and loads the vault. A later source then sets `Sqlite` or `InMemory`, and the host runs Sqlite or InMemory after paying for a vault round trip.

The reverse case is worse. The top level sees `InMemory` and skips the vault, then something flips it to `SqlServer`. The host then fails with "No database is configured" although a vault was named.

The AGENTS.md PITFALL (use `UseSetting`, not `ConfigureAppConfiguration`) documents the trap rather than removing it.

**Fix:** Resolve once and carry the value, so there is a single source of truth.
```csharp
var databaseKind = DatabaseProvider.Resolve(builder.Configuration);
...
builder.Services.AddDbContext<DesignDbContext>((sp, options) =>
    DatabaseProvider.Configure(options, databaseKind, sp.GetRequiredService<IConfiguration>()));
```
Change `Configure` to take the `DatabaseKind`. `Configure` would then re-resolve only for tests that call it directly.

### WR-02: gRPC reflection is no longer gated on a Debug build, only on the environment name

**File:** `src/AzureDesignStudio.Server/Program.cs:76-77` and `:125-128`
**Issue:** Before this phase, `AddGrpcReflection` and `MapGrpcReflectionService` were both inside `#if DEBUG` and `IsDevelopment()`. A Release image therefore could never expose reflection. Now `IsDevelopment()` is the only gate.

`ASPNETCORE_ENVIRONMENT=Development` on a deployed container or App Service slot is a common slip. It now publishes the full service surface to anonymous callers, because `MapGrpcReflectionService` carries no `[Authorize]`. The same environment value already turns on `UseWebAssemblyDebugging`, a larger disclosure surface, but that gate was not widened by this change. The reflection gate was.

**Fix:** Require an authenticated caller on the endpoint, or an explicit opt-in setting in addition to the environment.
```csharp
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Grpc:Reflection"))
    app.MapGrpcReflectionService();
```
Alternatively, add `.RequireAuthorization()` to the mapping. At minimum, record the trade-off in AGENTS.md, which currently presents the change only as a simplification.

### WR-03: The SQL Server legacy-DDL gate has no positive test

**File:** `src/AzureDesignStudio.Server.Tests/Hosting/DatabaseBootstrapperTests.cs:100-190`, `src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs:28`
**Issue:** The review focus said the T-SQL upgrade "must still run only on SQL Server". The tests prove only the negative: SQLite and InMemory do not run it. Nothing proves that the SQL Server provider does run `ExecuteSqlRaw(LegacySqlServerUpgrade)`.

If the gate regressed to something that is never true on the real provider, every test would stay green. Examples are a typo in the check, or a provider-name comparison like the one `DesignDbContext.OnModelCreating` uses. Production databases created by an earlier release would then silently stop receiving the missing-table and missing-column upgrades. `AdsPlanApprovals`, `AdsGuardrailPolicies` and `AdsDecom*` would be missing, with failures only at first use.

**Fix:** Make the gate observable without a live server. Extract the decision and the statement, for example `internal static bool NeedsLegacyUpgrade(DatabaseFacade db)` and `internal const string LegacySqlServerUpgrade`. Then assert the following:
- `NeedsLegacyUpgrade` is true for a context built with `UseSqlServer("Server=.;...")`. This needs no connection.
- It is false for Sqlite and InMemory.
- The constant still contains the guarded `OBJECT_ID` / `COL_LENGTH` statements.

### WR-04: `AdsServerFactory` is not hermetic against ambient environment variables

**File:** `src/AzureDesignStudio.Server.Tests/Hosting/AdsServerFactory.cs:28-62`
**Issue:** The factory relies on the checked-in placeholders (`KeyVaultName: "<kv name>"`, `ApplicationInsights:ConnectionString`) staying in effect, and overrides only the App Insights connection string. Environment variables are not neutralised:

- A developer or CI shell with `KeyVaultName` and `KeyVaultKeyName` exported makes `DeploySaveSubscriptionWithoutAKeyVaultAnswers503InBand` call the real vault through `DefaultAzureCredential`. It would hang or fail on credentials, or succeed against a real vault and return 200. User-secrets are protected by the `Testing` environment, but env vars are not.
- `APPLICATIONINSIGHTS_CONNECTION_STRING` set in the environment takes precedence in the Application Insights SDK. The tests would then send telemetry to a real resource, despite the `UseSetting(..., "")` above.
- `Database__Provider` exported in the shell may override the in-memory `UseSetting` value, depending on config ordering. `launchSettings.json` now exports it for `dotnet run`.

**Fix:** Pin every ambient input the tests assume.
```csharp
builder.UseSetting("KeyVaultName", "");
builder.UseSetting("KeyVaultKeyName", "");
builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", "");
```
Also add a `ConfigureAppConfiguration` in-memory layer, which wins over env vars, for the values that are read lazily.

## Info

### IN-01: Sqlite and InMemory are accepted in Production with only a log warning

**File:** `src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs:17-20`
**Issue:** `Database__Provider=Sqlite` on a Release image gives a container-local file, so designs, links and approvals are lost on redeploy. The only signal is a log warning. InMemory was already opt-in before this phase, but Sqlite is new. It also ships the native `e_sqlite3` library in the production image, because the package is referenced by the Server project.
**Fix:** Either refuse Sqlite and InMemory when `IsProduction()` unless `Database:AllowNonProduction=true`, or note that the Phase 5 production-refusal rule (CRED-02) is meant to cover the database too. CRED-02 currently covers only crypto.

### IN-02: Visual Studio "Docker" launch profile lost its InMemory default

**File:** `src/AzureDesignStudio.Server/Properties/launchSettings.json:27-33`
**Issue:** The `Project` and `IIS Express` profiles got `Database__Provider=InMemory`. The `Docker` profile did not. A Debug container previously ran InMemory through `#if DEBUG`. It now fails at startup with "No database is configured".
**Fix:** Add `"environmentVariables": { "Database__Provider": "InMemory" }` to the Docker profile, or document that it needs `ConnectionStrings__ads-main`.

### IN-03: Source guards cover only `Program.cs` for preprocessor conditionals

**File:** `src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs:34-46`
**Issue:** `ProgramHasNoPreprocessorConditionals` scans only `Program.cs`. Provider and diagnostic choices now live in `DatabaseProvider.cs`, `DatabaseBootstrapper.cs` and `CryptoProvider.cs`. A `#if DEBUG` there would silently reintroduce the Debug/Release split this phase removed. Separately, `EnsureCreatedIsCalledOnlyByTheDatabaseBootstrapper` matches raw text, so a comment containing `.EnsureCreated(` in any other file is a false positive.
**Fix:** Run the preprocessor scan over every `.cs` under `src/AzureDesignStudio.Server/Persistence` plus `Services/CryptoProvider.cs`, or over all of `ServerDir`. Strip `//` comments before the `EnsureCreated` match.

### IN-04: Tautological and vacuous assertions in `ServerHostTests`

**File:** `src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs:49-51`, `:67-69` and `:106-107`
**Issue:**
- `Assert.Equal(Path.GetTempPath().TrimEnd(...), Path.GetDirectoryName(factory.DbFile))` is true by construction, because `DbFile` is built from `Path.GetTempPath()`.
- `Assert.False(File.Exists(first.DbFile))` in the InMemory test is vacuous, because the InMemory host never touches `DbFile`.
- `BootsTestScopeMatchesTheConfiguredScope` compares a constant to a config value, which is fine, but nothing ties the handler's long-form claim types to what `RequiredScope` actually reads except the end-to-end tests.

These add noise without protecting anything.
**Fix:** Drop the first two assertions. Assert on the InMemory host's provider name instead (`Microsoft.EntityFrameworkCore.InMemory`).

### IN-05: `DeleteDatabaseFiles` swallows only `IOException`, and `ClearAllPools` is process-global

**File:** `src/AzureDesignStudio.Server.Tests/Hosting/AdsServerFactory.cs:76-98` (same pattern in `DatabaseProviderTests.Dispose` and `DatabaseBootstrapperTests.Dispose`)
**Issue:**
- On Windows, `File.Delete` on a file that is still locked or marked read-only can throw `UnauthorizedAccessException`. That is not caught, so fixture teardown fails the run, contradicting the comment "a leftover temp file must not fail the test run".
- `SqliteConnection.ClearAllPools()` clears the pools of every parallel host and test class in the process, not just this factory's. This is harmless today because idle connections reopen. It couples parallel test classes through a global.

**Fix:** Catch `(IOException or UnauthorizedAccessException)`. Better, use the `Pooling=False` connection-string option for test databases, so no process-wide pool clear is needed. Extract the duplicated cleanup into one helper, since it exists in three places.

### IN-06: AutoMapper audit suppression widened by two High advisories, outside the phase's scope

**File:** `Directory.Build.props:28-45`
**Issue:** The phase added suppressions for GHSA-gpwp-5797-mjff ("recursion that bypasses MaxDepth") and GHSA-6vhm-j9h7-f9gh, both High, under the original expiry (2027-01-31). The unchanged rationale is "mappings are shallow node DTOs". Designs can nest groups, and design JSON is user-supplied. "Bypasses MaxDepth" is the advisory most directly aimed at nested input, and the shallow-DTO claim was not re-checked against it.

The `NuGetAuditSuppress` is advisory-scoped, as the comment itself admits. This is unrelated to the server-foundation work, and the exception was likely added to unblock the build.
**Fix:** Give the two new ids their own dated entry and measured rationale, or a shorter expiry. Confirm that no AutoMapper map is reachable from server request data, or that nested groups are depth-bounded before mapping.

### Pre-existing, noted only

`ServiceTools.IsConfigured` treats any value containing `<` or `>` as an unset placeholder. A genuine SQL Server connection string whose password contains either character is reported as "No database is configured". This is not introduced by this phase, but the new `Sqlite`/`SqlServer` branches inherit it. Consider matching the specific `<...>` placeholder shape instead.

---

_Reviewed: 2026-11-14_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
