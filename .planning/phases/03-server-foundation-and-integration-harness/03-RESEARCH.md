# Phase 3: Server Foundation and Integration Harness - Research

**Researched:** 2026-11-14
**Domain:** ASP.NET Core 10 minimal-hosting server, `WebApplicationFactory` + gRPC over `TestServer`, EF Core provider selection (SQL Server / SQLite / InMemory), NuGet central versions + locked restore
**Confidence:** HIGH (every behavior the plan relies on was reproduced in a throwaway copy of the repo; see "Prototype evidence")

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
(none - CONTEXT.md has no `## Decisions` entries; the whole phase is Claude's discretion)

### Claude's Discretion
All implementation choices are at Claude's discretion - pure infrastructure phase. Follow the roadmap implementation note: `public partial class Program`, `Database:Provider`/`Crypto:Provider` read from configuration, `IsDevelopment()` gating diagnostics only (no `#if DEBUG` picking providers), direct Server->Core reference, design-time context factories beside `Persistence/DatabaseBootstrapper`, `Microsoft.AspNetCore.Mvc.Testing` with a test auth handler, SQLite temp files with `ClearAllPools()` by default, InMemory only for pure service logic.

### Deferred Ideas (OUT OF SCOPE)
None
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| TEST-01 | A server integration suite boots the real host and runs in CI without Azure credentials. | Prototype booted the real `Program` through `WebApplicationFactory<Program>` on a temp SQLite file with the checked-in placeholder `appsettings.json` (no Key Vault, no credentials), reached `Design`, `Deploy` and `Cost` over `TestServer`, in both Debug and Release (`-c Release -p:RunAOTCompilation=false`, the CI shape). See "Prototype evidence" and "Code Examples". |
</phase_requirements>

## Project Constraints (from AGENTS.md)

No `CLAUDE.md` exists (`./CLAUDE.md` and `./.claude/` absent); `AGENTS.md` is the project knowledge base. Directives that bind this phase:

- Package versions live only in `Directory.Packages.props`; csproj files declare `Include=` only. `Directory.Build.props` owns repo-wide properties. Locked restore is a CI flag; every project has a committed `packages.lock.json` (CI asserts tracked-lock count equals csproj count).
- `TreatWarningsAsErrors=true` in `Directory.Build.props`; the project set must still build with zero warnings.
- Server services read the caller from `context.UserState[ServiceTools.UserStateKey]`, set by `CallerInterceptor`; never `GetHttpContext()` inside `Services`.
- `AzureDesignStudio.Services` must not depend on ASP.NET Core (Mac Catalyst has none) - so anything needing `Microsoft.EntityFrameworkCore.SqlServer`/Sqlite registration, the design-time factory and the bootstrapper must live in `AzureDesignStudio.Server`, not `Services`.
- CI test shape for Server.Tests: `dotnet test src/AzureDesignStudio.Server.Tests -c Release --no-build -p:RunAOTCompilation=false` (Server hosts the WASM client; without the flag the test run rebuilds with AOT).
- `Database__Provider=InMemory` and `ConnectionStrings__ads-main` are documented operator-facing contracts (AGENTS.md NOTES, Docker/k8s usage) - keep both working.
- PITFALL already recorded: any Kestrel endpoint defined in code replaces URL-configured endpoints; `DesktopGrpcEndpoint` re-binds them. Do not touch it in this phase.
- Do not hand-edit `STATE.md` counters; commit planning artifacts as `docs(03):`.
- AGENTS.md lines that become false after this phase and must be updated: "InMemory EF Core in DEBUG..." (NOTES) and "gRPC reflection service only registered in DEBUG" (NOTES). Both sentences describe `#if DEBUG` behavior.

## Summary

The roadmap note is mostly accurate but four facts differ from the repo as it actually is, and one pre-existing break will stop the plan before it starts. **(1)** `AzureDesignStudio.Server.Tests` already exists (134 passing tests, direct service-class calls) and already has `InternalsVisibleTo` from Server and Services - this phase *extends* it, it does not create it. **(2)** `Persistence/DatabaseBootstrapper` does not exist; the bootstrap is inline in `Program.cs` lines 104-190 with the SQL Server DDL under `#if !DEBUG`. **(3)** No gRPC service calls `EnsureCreated`: the only call sites are `Program.cs:109` (once at startup), `Workspace/WorkspaceDbContext.cs:97`, and `Desktop/MauiProgram.cs:210` - success criterion 4's second half is already true and only needs a regression guard. **(4)** `Microsoft.AspNetCore.Mvc.Testing` is *not* at 10.0.13 in this repo's train: every other ASP.NET Core package in `Directory.Packages.props` is `10.0.15`, and `Mvc.Testing` 10.0.15 exists on nuget.org; use 10.0.15. **(5, blocker)** Two new AutoMapper High advisories (GHSA-gpwp-5797-mjff and GHSA-6vhm-j9h7-f9gh, published 2026-11-13) are not covered by the single `NuGetAuditSuppress` entry, so `dotnet restore` of any project that reaches AutoMapper currently fails with NU1903 under `TreatWarningsAsErrors`. The plan needs a first task for this or nothing builds.

The technical design is small and fully de-risked. Replace the two `#if DEBUG` provider blocks with a config-driven `Database:Provider` (`SqlServer` default | `Sqlite` | `InMemory`) read **inside the `AddDbContext((sp, options) => ...)` callback**, make `Program` `public partial`, move the startup schema work into `Server/Persistence/DatabaseBootstrapper` (SQL Server DDL gated by `db.Database.IsSqlServer()`, not `IsRelational()` - the T-SQL is a syntax error on SQLite), and build a `WebApplicationFactory<Program>` whose test auth handler replaces the default scheme. The one non-obvious trap, reproduced in the prototype: values added with `ConfigureAppConfiguration` are **invisible to `builder.Configuration` reads in top-level `Program.cs` code** (the prototype printed `SqlServer` at top level and `Sqlite` inside the deferred callback); `builder.UseSetting(...)` is visible at both points. Tests must set `Database:Provider` with `UseSetting`.

**Primary recommendation:** Extend `Server.Tests` with a `Hosting/` folder (`AdsServerFactory : WebApplicationFactory<Program>`, `TestAuthHandler`, per-host temp-SQLite file with `SqliteConnection.ClearAllPools()` cleanup), make the server pick its provider from configuration only, and ship it behind a Wave 0 that fixes the NU1903 restore break and adds `Mvc.Testing` 10.0.15 + `Microsoft.EntityFrameworkCore.Sqlite` to the Server with regenerated lock files.

## Prototype evidence

A copy of the repo (tree minus `bin/obj/.git/docs/assets`, re-`git init`ed so nbgv works) was modified and run on this machine (macOS, SDK 10.0.403). Everything tagged `[VERIFIED: prototype]` below was observed in that copy. The copy lives at `/private/tmp/claude-501/-Users-jacobeen-repositories-AzureDesignStudio/8633abe3-0609-4a89-85de-6691f1ac3674/scratchpad/proto` (throwaway; do not import from it).

| Observation | Result |
|-------------|--------|
| Real `Program` boots under `WebApplicationFactory<Program>` with checked-in placeholder `AzureAd`/`KeyVaultName`/`ApplicationInsights` values and `Database:Provider=Sqlite` | boots; Debug and Release (`-c Release -p:RunAOTCompilation=false`) |
| Design `Save` -> `GetSaved` over a `GrpcChannel` on the `TestServer` handler, test auth handler | 200, one design, temp `.db` file exists |
| No `Authorization`/test header | `StatusCode.Unauthenticated` |
| Authenticated but scope claim is not `Server.Access` | `StatusCode.PermissionDenied` (`[RequiredScope]` is enforced by the test host) |
| `Deploy.GetGuardrailPolicy` / `Deploy.SaveSubscriptionInfo` with the real `CryptoService` and no Key Vault | status_code 200 / **503** in-band (never reaches Azure, host survives) |
| `Cost.GetRetailPrices` with empty request, anonymous | status_code 400 in-band, no network |
| DB table dropped after boot, then `Design.Save` | `RpcException` `StatusCode.Unknown` ("Exception was thrown by handler."), next RPC on the same host answers 200 |
| `EndpointDataSource` + `GrpcMethodMetadata` enumeration | 21 distinct methods across exactly 3 services: `Design`, `Deploy`, `Cost` |
| Two hosts with `Database:Provider=InMemory` in one process | second host **sees the first host's data** (fixed name `adsdb-inmem`) |
| `ConfigureAppConfiguration` value vs top-level read | top-level `builder.Configuration["Database:Provider"]` = `SqlServer` (default) while the deferred callback saw `Sqlite` |
| `UseSetting("Database:Provider", ...)` | visible at top level *and* in the callback |
| `IDesignTimeDbContextFactory<DesignDbContext>` in Server with no new package | compiles |
| Direct `ProjectReference` Server -> Core | builds; `packages.lock.json` of Server unchanged |
| Whole Server.Tests suite, Release, with prototype edits | 139 passed (134 existing + 5 prototype tests) |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Choosing EF provider (SqlServer/Sqlite/InMemory) from configuration | API / Backend (`Server/Persistence`) | - | Needs the SqlServer and Sqlite packages; `Services` must stay ASP.NET-free and provider-free |
| Schema bootstrap (`EnsureCreated` + legacy SQL Server DDL) | API / Backend (`Server/Persistence/DatabaseBootstrapper`) | Database / Storage | Runs once at host start; never per RPC. Phase 4 replaces it with migrations |
| Design-time `DbContext` factory (`dotnet ef`) | API / Backend (`Server/Persistence`) | - | Only Server references SqlServer; the factory compiles with no `Design` package yet (Phase 4 adds it) |
| Key Vault configuration source | API / Backend (`Program.cs`) | - | Runs only when the provider needs a vault-supplied connection string |
| `ICryptoService` selection (`Crypto:Provider`) | API / Backend (`Server/Services`) | - | Phase 3 adds the seam with one value (`KeyVault`); Phase 5 adds the development provider |
| Test authentication | Test project only | - | A header-driven auth scheme must never exist in `AzureDesignStudio.Server` (it would be a production backdoor) |
| gRPC reachability / RPC status contract | API / Backend | Test project (consumer) | Verified through a `GrpcChannel` on `TestServer`, not by calling service classes |
| Temp-database lifecycle | Test project only | - | Per-host SQLite file under `Path.GetTempPath()`; no repo or developer path involved |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.AspNetCore.Mvc.Testing` | **10.0.15** | `WebApplicationFactory<TEntryPoint>`, in-memory `TestServer`, brings `Microsoft.AspNetCore.TestHost` + `Microsoft.Extensions.DependencyModel` | Microsoft's integration-test host; the gRPC docs describe the same TestServer approach. 10.0.15 matches the other ASP.NET Core rows in `Directory.Packages.props` (10.0.15). The roadmap's "10.0.13" is stale. [VERIFIED: nuget.org flat container lists 10.0.0..10.0.15] |
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.12 (already central) | `Database:Provider=Sqlite` in the Server | Already in `Directory.Packages.props`; Desktop and Workspace use it. Add `<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />` to the **Server** csproj (and let Server.Tests inherit it transitively - the prototype compiled `SqliteConnection.ClearAllPools()` with no direct reference) |
| `xunit` 2.9.2, `Microsoft.NET.Test.Sdk` 17.12.0, `coverlet.collector` 6.0.2 | existing | Test framework | Already referenced by Server.Tests; keep xunit v2 (`IClassFixture<T>`) |
| `Grpc.Net.Client` 2.66.0 | existing central | `GrpcChannel` over the `TestServer` client | Reached transitively today; an explicit `PackageReference` in Server.Tests is optional hygiene (adds one lock entry) |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Microsoft.EntityFrameworkCore.InMemory` | 10.0.12 (existing) | Pure service-logic tests (`TestData.NewDb()`) and `Database:Provider=InMemory` smoke runs | Keep in Server (documented operator switch) and in Server.Tests (existing 134 tests) |
| `Microsoft.EntityFrameworkCore.Design` | not in this phase | `dotnet ef migrations` | Phase 4 (DATA-02) adds it; `IDesignTimeDbContextFactory<T>` itself needs no extra package |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `UseSetting` for provider config in tests | `CreateHost` override + `ConfigureHostConfiguration` | Documented alternative (the docs say host configuration is enumerated before the entry point and `args` must reach `WebApplication.CreateBuilder` - it does here). More code; `UseSetting` was verified and is one line |
| Temp SQLite file per host | SQLite `:memory:` with a kept-open connection | Needs a shared singleton connection and breaks `ClearAllPools()` convention; DATA-0x phases will need a real file (WAL) anyway |
| Header-driven `TestAuthHandler` | Custom `ClaimsPrincipal` via `AuthenticateResult` from a static | A header keeps parallel tests and per-call identities independent |
| `Database:Provider` default `SqlServer` | Default `InMemory` | InMemory default makes a forgotten setting in production silently lose data. Keep the current fail-fast |

**Installation (central + csproj, then regenerate locks):**
```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.15" />

<!-- AzureDesignStudio.Server.csproj -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
<ProjectReference Include="..\AzureDesignStudio.Core\AzureDesignStudio.Core.csproj" />

<!-- AzureDesignStudio.Server.Tests.csproj -->
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
```
```bash
dotnet restore src/AzureDesignStudio.Server.Tests      # rewrites Server and Server.Tests packages.lock.json
```
Lock effect observed [VERIFIED: prototype]: Server.Tests lock gains `Microsoft.AspNetCore.Mvc.Testing` (Direct), `Microsoft.AspNetCore.TestHost` and `Microsoft.Extensions.DependencyModel` (Transitive, all 10.0.15); Server lock gains `Microsoft.EntityFrameworkCore.Sqlite` (Direct) plus `Microsoft.Data.Sqlite.Core`, `Microsoft.EntityFrameworkCore.Sqlite.Core`, `Microsoft.Extensions.DependencyModel`, `SQLitePCLRaw.*` 2.1.12 (Transitive). Adding the Server->Core `ProjectReference` changed no lock file.

**Version verification:** `Microsoft.AspNetCore.Mvc.Testing` 10.0.15 and `Microsoft.AspNetCore.TestHost` 10.0.15 listed in `https://api.nuget.org/v3-flatcontainer/.../index.json`; nuspec: authors `Microsoft`, repository `github.com/dotnet/dotnet`. The nuget.org search shows 449,837,244 total downloads, `verified: True`. A newer 11.0.0 exists - do **not** use it (TFM is `net10.0`).

## Package Legitimacy Audit

The `package-legitimacy` seam supports only `npm|pypi|crates` (it printed `Usage: ... --ecosystem <npm|pypi|crates>` for `nuget`), so the seam verdict is not available for NuGet. Registry metadata was checked directly.

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| Microsoft.AspNetCore.Mvc.Testing | NuGet | 7+ yrs (ships every ASP.NET Core release) | ~450M total | github.com/dotnet/dotnet | OK (Microsoft-authored, nuget.org "verified" owner) | Approved |
| Microsoft.EntityFrameworkCore.Sqlite | NuGet | 8+ yrs | ~314M total | Microsoft | OK; already a central row in this repo | Approved |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none. No `postinstall` concept applies to NuGet. Both packages come from Microsoft's own feed lineage and are tied to the .NET 10 train already in use, so no `checkpoint:human-verify` is needed. [CITED: https://learn.microsoft.com/en-us/aspnet/core/grpc/test-services?view=aspnetcore-10.0 names `Microsoft.AspNetCore.TestHost`; Mvc.Testing depends on it]

## Architecture Patterns

### System Architecture Diagram

```
 Test (xunit)                                   AzureDesignStudio.Server (real Program)
 ------------                                   ---------------------------------------
 AdsServerFactory : WebApplicationFactory<Program>
   UseEnvironment("Testing")                     WebApplication.CreateBuilder(args)
   UseSetting(Database:Provider = Sqlite)  ----> top-level reads (KeyVault decision, desktop port)
   UseSetting(ConnectionStrings:ads-main=tmp.db)
   ConfigureTestServices: auth scheme "Test"     AddAuthentication(JwtBearer) + AddMicrosoftIdentityWebApi  (placeholders ok)
                          [optional fake Crypto] AddDbContext<DesignDbContext>((sp,o) => DatabaseProvider.Configure(o, cfg))
        |                                        AddSingleton<ICryptoService>  (Crypto:Provider = KeyVault)
        | CreateDefaultClient(AuthHeaderHandler)AddGrpc(CallerInterceptor) / HttpClients / WhatIfTokenGate
        v                                              |
 GrpcChannel(HttpClient=TestServer client)             v
   Design.DesignClient / Deploy.DeployClient /   Build()
   Cost.CostClient                                     |
        |                                              v
        |                                        DatabaseBootstrapper.Initialize(app.Services)   <-- ONCE, startup only
        |                                          EnsureCreated(); if IsSqlServer(): legacy guarded DDL
        |                                              |
        |                                              v
        +---- HTTP/2 over TestServer ------------ UseAuthentication -> UseAuthorization -> MapGrpcService<Hosted*>
                                                       |  [Authorize] + [RequiredScope(AzureAd:Scopes)]
                                                       v
                                                 CallerInterceptor -> context.UserState["ads.user"] -> Services.* (DesignService, DeployService, RetailPriceService)
                                                       |
                                                       v
                                                 DesignDbContext -> temp SQLite file  (SqlServer/InMemory in other environments)
 Dispose: SqliteConnection.ClearAllPools(); delete *.db, -wal, -shm, -journal
```

### Recommended Project Structure
```
src/AzureDesignStudio.Server/
├── Program.cs                         # no #if; public partial class Program { }
├── Persistence/
│   ├── DatabaseProvider.cs            # Resolve(IConfiguration) + Configure(DbContextOptionsBuilder, IConfiguration)
│   ├── DatabaseBootstrapper.cs        # Initialize(IServiceProvider): EnsureCreated + IsSqlServer() legacy DDL
│   └── DesignDbContextFactory.cs      # IDesignTimeDbContextFactory<DesignDbContext> (SqlServer, no connection made)
├── Services/
│   ├── CryptoService.cs               # unchanged (already lazy)
│   └── CryptoProvider.cs              # Crypto:Provider switch (only "KeyVault" valid in this phase)
└── Properties/launchSettings.json     # + "Database__Provider": "InMemory" in the dev profile

src/AzureDesignStudio.Server.Tests/
├── Hosting/
│   ├── AdsServerFactory.cs            # WebApplicationFactory<Program>; temp sqlite; cleanup
│   ├── TestAuthHandler.cs             # scheme "Test"; header-driven claims incl. scope claim
│   ├── GrpcTestClients.cs             # channel helper: CreateChannel(Guid? user, ...)
│   ├── ServerHostTests.cs             # boot, reach 3 services, unauthenticated, scope, fault -> status
│   ├── DatabaseProviderTests.cs       # pure: Resolve/Configure/unknown value/missing connection string
│   └── DatabaseBootstrapperTests.cs   # sqlite temp file bootstrap; DesignDbContextFactory provider name
└── (existing 134 tests unchanged)
```

### Pattern 1: Provider chosen from configuration, read lazily
**What:** `Database:Provider` is parsed by one pure function and applied inside the `AddDbContext` options callback, so a value supplied by `ConfigureAppConfiguration` (or any late source) is honored. Top-level code only reads what must be known before `Build()` (Key Vault source, Desktop port).
**When to use:** Always, for anything a test needs to override.
**Example:**
```csharp
// Source: prototype, adapted from Program.cs lines 29-71 (verbatim current text quoted in Code Examples)
builder.Services.AddDbContext<DesignDbContext>((sp, options) =>
    DatabaseProvider.Configure(options, sp.GetRequiredService<IConfiguration>()));
```

### Pattern 2: `Program` reachable, auth replaced, no production test hooks
**What:** `public partial class Program { }` at the end of `Program.cs`; the test project replaces the default authenticate/challenge scheme in `ConfigureTestServices`. Production code gains no test-only branch.
**Why `public`:** `InternalsVisibleTo Include="AzureDesignStudio.Server.Tests"` already exists in the Server csproj, but a `public` factory fixture exposing an `internal` `Program` type in its signature fails with CS0060, so follow the docs' `public partial` form.

### Pattern 3: One schema-bootstrap call site, never per request
**What:** `DatabaseBootstrapper.Initialize(app.Services)` is called once after `Build()`. `EnsureCreated()` always; the guarded T-SQL upgrade only when `db.Database.IsSqlServer()`.
**Why not `IsRelational()`:** SQLite is relational; the DDL (`IF OBJECT_ID(...)`, `datetime2`, `IDENTITY`, `INDEXPROPERTY`) is T-SQL. [VERIFIED: `sqlite3 :memory: "IF OBJECT_ID(N'X', N'U') IS NULL CREATE TABLE X (a int);"` -> `Parse error ... near "IF": syntax error`]. Today the `IsRelational()` check was safe only because `#if !DEBUG` + SQL Server were the only relational combination.

### Pattern 4: Key Vault configuration only when the provider needs it
**What:** Add the Key Vault configuration source only when the provider is `SqlServer` *and* a real vault name is configured (`ServiceTools.IsConfigured`). Today the source exists only in Release builds; removing `#if` unconditionally would make a Debug developer who keeps `KeyVaultName` in user-secrets (for `CryptoService`) start doing a vault round trip at boot. `appsettings.Development.json` cannot carry the dev default because **`.gitignore` ignores it** (`/src/AzureDesignStudio.Server/appsettings.Development.json`, line 354 [VERIFIED: `git check-ignore -v`]); put `"Database__Provider": "InMemory"` in the committed `Properties/launchSettings.json` profile instead, so `dotnet run` keeps today's zero-setup dev experience as *configuration*, not compilation.

### Pattern 5: Crypto provider seam, minimal
**What:** `Crypto:Provider` (default `KeyVault`). Phase 3 implements exactly one value and fails fast with a message naming the accepted values on anything else; Phase 5 (CRED-01/02) adds the development provider and the "production refuses dev keys" rule. Tests that need a successful `SaveSubscriptionInfo` replace `ICryptoService` with the existing `FakeCryptoService` (in `TestSupport.cs`, internal, same assembly) via `ConfigureTestServices`. The real `CryptoService` stays lazy, so booting without a vault is already safe [VERIFIED: prototype - boots, and `SaveSubscriptionInfo` answers 503 in-band].

### Anti-Patterns to Avoid
- **`ConfigureAppConfiguration` for anything read at the top of `Program.cs`:** not visible there (see Pitfall 1).
- **`IsRelational()` as the guard for the T-SQL upgrade:** breaks SQLite (Pitfall 3).
- **Calling `EnsureCreated` from services or a request path:** the bootstrapper is the only caller in Server; add the grep guard.
- **A test auth scheme in `AzureDesignStudio.Server` (even behind `IsDevelopment()`):** it is a credential bypass; keep it in Server.Tests only.
- **Sharing one named InMemory store across hosts:** cross-test leakage (Pitfall 4).
- **A real `workspace.db` or `deploy.db` path in any test:** Server.Tests never needs either; all DB files must live under `Path.GetTempPath()`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| In-process host for integration tests | A custom `IHost`/Kestrel-on-random-port harness | `WebApplicationFactory<Program>` (Mvc.Testing) | Handles content root, `Program` entry-point resolution, `TestServer`, and disposal |
| gRPC transport to the test host | A custom handler stack | `factory.CreateDefaultClient(handler)` -> `GrpcChannel.ForAddress(client.BaseAddress, new GrpcChannelOptions { HttpClient = client })` | Verified working with HTTP/2 semantics over `TestServer`; the gRPC docs describe the TestServer approach |
| Enumerating every mapped gRPC method | Reflection over the proto-generated assemblies | `EndpointDataSource` + `GrpcMethodMetadata` (`Grpc.AspNetCore.Server`) | Reflects what is actually routed (21 methods / 3 services today); the same primitive Phase 5's IDA-03 will need |
| SQLite temp-file cleanup | `File.Delete` only | `SqliteConnection.ClearAllPools()` first, then delete `*.db`, `*.db-wal`, `*.db-shm`, `*.db-journal` | Pooled connections keep the file locked (Windows) and leave WAL side files |
| Design-time context creation | A runtime scan for providers | `IDesignTimeDbContextFactory<DesignDbContext>` in Server | `dotnet ef` finds it without running `Program`; compiles with no extra package |
| Placeholder detection | New regex | `ServiceTools.IsConfigured(...)` | Already shared by `CryptoService` and `Program` |

**Key insight:** every piece of this harness is a thin composition of framework primitives; the work is deleting `#if DEBUG` and moving three blocks (provider choice, bootstrap, crypto registration) behind configuration, not writing infrastructure.

## Common Pitfalls

### Pitfall 1: Test configuration invisible to top-level `Program.cs`
**What goes wrong:** `builder.Configuration["Database:Provider"]` returns the default even though the test set it with `ConfigureAppConfiguration`; the host picks SQL Server and fails.
**Why:** `ConfigureAppConfiguration` sources are applied when the `WebApplicationBuilder` builds. [VERIFIED: prototype printed `top-level ... SqlServer` and `deferred ... Sqlite` for the same host] [CITED: learn.microsoft.com integration-tests: "Configuration accessed before `WebApplicationBuilder.Build` is called won't include these values."]
**How to avoid:** Read the provider inside `AddDbContext((sp, options) => ...)`; in tests set the value with `builder.UseSetting(key, value)` (verified visible at top level) for everything top-level code reads (`Database:Provider`, `KeyVaultName`, `DesktopGrpc:Port`).
**Warning signs:** "No database is configured" or a SQL Server connection attempt during a test.

### Pitfall 2: NU1903 breaks restore today (pre-existing blocker)
**What goes wrong:** `dotnet restore src/AzureDesignStudio.Server.Tests` fails: `error NU1903: Warning As Error: Package 'AutoMapper' 12.0.1 has a known high severity vulnerability, https://github.com/advisories/GHSA-gpwp-5797-mjff` and `...GHSA-6vhm-j9h7-f9gh`.
**Why:** `Directory.Build.props` suppresses only `GHSA-rvv3-g6hj-g44x`. [VERIFIED: GitHub advisory API: GHSA-gpwp-5797-mjff "AutoMapper: Denial of Service via uncontrolled recursion that bypasses MaxDepth", published 2026-11-13T18:47:17Z, high; GHSA-6vhm-j9h7-f9gh "AutoMapper: Uncontrolled Recursion with Constructor Parameters", published 2026-11-13T19:21:02Z, high.] Reproduced on a clean copy of HEAD *without* any Phase 3 edit (10 NU1903 hits); with the two extra `NuGetAuditSuppress` lines restore succeeds. This also breaks CI on every PR (BUILD-04's gate), not just this phase.
**How to avoid:** Wave 0 task: add both IDs next to the existing one in `Directory.Build.props`, keep the single dated expiry (`AdsAuditSuppressionAutoMapperExpires` 2027-01-31) and update the `AdsCheckAuditSuppressionExpiry` message in `Directory.Build.targets` (it names only `GHSA-rvv3-g6hj-g44x`). This is a decision for the owner of Phase 2/BUILD-04 ("one dated AutoMapper allowlist entry"); flagged in Open Questions.

### Pitfall 3: T-SQL bootstrap on SQLite
**What goes wrong:** Moving the guarded DDL out of `#if !DEBUG` while keeping `db.Database.IsRelational()` makes every SQLite host throw `near "IF": syntax error` at startup.
**How to avoid:** Gate on `db.Database.IsSqlServer()` (namespace `Microsoft.EntityFrameworkCore`, SqlServer package is already referenced). InMemory is non-relational and skips it automatically.

### Pitfall 4: Fixed-name InMemory store leaks between hosts
**What goes wrong:** `UseInMemoryDatabase("adsdb-inmem")` is process-wide by name. [VERIFIED: prototype - two `InMemory` factories in one process, host B's `GetSaved` returned host A's design.]
**How to avoid:** Harness default is Sqlite with a unique temp file. If InMemory is used for a host, give it a unique name through a new optional `Database:InMemoryName` key (default stays `adsdb-inmem` for operators).

### Pitfall 5: `[RequiredScope]` needs a scope claim
**What goes wrong:** An authenticated test principal without the scope gets `PermissionDenied`. [VERIFIED: prototype]
**How to avoid:** The test handler always issues claim type `http://schemas.microsoft.com/identity/claims/scope` with the value of `AzureAd:Scopes` (`Server.Access` in the checked-in appsettings), unless a test deliberately omits it. Caller id must be claim `http://schemas.microsoft.com/identity/claims/objectidentifier` (what `ServiceTools.GetUserId` reads first), or `oid`.

### Pitfall 6: Local SDK drift rewrites lock files you must not commit
**What goes wrong:** On this machine (SDK 10.0.403; `global.json` pins 10.0.401 with `rollForward: latestPatch`), restore rewrote `src/AzureDesignStudio/packages.lock.json` (`Microsoft.AspNetCore.App.Internal.Assets` and `Microsoft.NET.ILLink.Tasks` from 10.0.12 to 10.0.14) before any Phase 3 change. [VERIFIED: prototype `git diff`] AGENTS.md already records this class of drift.
**How to avoid:** Commit only the lock files that Phase 3 intentionally changes (`Server` and `Server.Tests`); `git checkout -- src/AzureDesignStudio/packages.lock.json` (and any other unrelated lock) before committing. CI's "Lock files must be committed" step (`git diff --exit-code -- '*packages.lock.json'`) runs on 10.0.401 and is the arbiter; the Phase 3 locks should be generated under that SDK if one is available.

### Pitfall 7: Environment name drives dev-only wiring
**What goes wrong:** `WebApplicationFactory` defaults to `Development` [CITED: integration-tests doc]; then `DesktopGrpcEndpoint.ResolvePort` returns 5204 (Kestrel config is inert under TestServer but surprising), `UseWebAssemblyDebugging` and reflection are on.
**How to avoid:** `builder.UseEnvironment("Testing")` in the factory (verified to boot; non-Development takes the `UseExceptionHandler("/Error")` + HSTS branch). Add one separate Development-environment test only if reflection mapping must be proved.

### Pitfall 8: Benign log noise must not become failures
`HttpsRedirectionMiddleware: Failed to determine the https port for redirect.` and `StaticFileMiddleware: The WebRootPath was not found` appear under TestServer. They are warnings, not errors; do not assert "no warnings".

### Pitfall 9: Application Insights placeholder
The checked-in `ApplicationInsights:ConnectionString` is `"<connection string>"`. Set it to `""` in the test host (`UseSetting`/in-memory) so no telemetry channel is ever configured against a placeholder. [VERIFIED: prototype boots with `""`]

### Pitfall 10: Debug vs Release divergence is the thing being removed
CI runs Release; developers run Debug. Because `#if DEBUG` currently selects the provider, a green Release run proves nothing about Debug. After the change both must behave identically - run the new tests in both configurations (plan verification), and add the static guard `grep -nE '^#(if|else|elif)' src/AzureDesignStudio.Server/Program.cs` -> empty.

## Code Examples

Current text this phase replaces (read this session, `src/AzureDesignStudio.Server/Program.cs`):

```csharp
// lines 37-38 and 71 - the two #if blocks to delete
#if DEBUG
builder.Services.AddDbContext<DesignDbContext>(options => options.UseInMemoryDatabase("adsdb-inmem"));
#else
...
var connectionString = builder.Configuration["ads-main"] ?? builder.Configuration.GetConnectionString("ads-main");
if (string.Equals(builder.Configuration["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
...
#endif
```
Also `#if DEBUG builder.Services.AddGrpcReflection(); #endif` (lines 85-87), `#if !DEBUG ... InMemory warning ... #endif` (99-102), `#if !DEBUG ... ExecuteSqlRaw ... #endif` (110-189), and `#if DEBUG if (app.Environment.IsDevelopment()) { app.MapGrpcReflectionService(); } #endif` (224-229). Reflection becomes `if (builder.Environment.IsDevelopment()) builder.Services.AddGrpcReflection();` and `if (app.Environment.IsDevelopment()) app.MapGrpcReflectionService();`.

### Program.cs shape after the change
```csharp
// Source: prototype (compiles, runs); names are the recommendation
var provider = DatabaseProvider.Resolve(builder.Configuration);            // top-level: tests must use UseSetting
var keyVaultName = builder.Configuration["KeyVaultName"];
if (provider == DatabaseKind.SqlServer && ServiceTools.IsConfigured(keyVaultName))
{
    var secretClient = new SecretClient(new Uri($"https://{keyVaultName}.vault.azure.net/"), new DefaultAzureCredential());
    builder.Configuration.AddAzureKeyVault(secretClient, new KeyVaultSecretManager());
}

builder.Services.AddDbContext<DesignDbContext>((sp, options) =>
    DatabaseProvider.Configure(options, sp.GetRequiredService<IConfiguration>()));   // lazy: ConfigureAppConfiguration also works here

CryptoProvider.Register(builder.Services, builder.Configuration);                    // Crypto:Provider, only "KeyVault" today
...
var app = builder.Build();
DatabaseBootstrapper.Initialize(app.Services);                                        // once; warns for InMemory/Sqlite
...
app.Run();

public partial class Program { }
```

### DatabaseProvider (pure, unit-testable)
```csharp
// Source: recommendation; mirrors the existing error text and key names
internal enum DatabaseKind { SqlServer, Sqlite, InMemory }

internal static class DatabaseProvider
{
    public const string Key = "Database:Provider";

    public static DatabaseKind Resolve(IConfiguration configuration) =>
        configuration[Key]?.Trim().ToLowerInvariant() switch
        {
            null or "" or "sqlserver" => DatabaseKind.SqlServer,
            "sqlite" => DatabaseKind.Sqlite,
            "inmemory" => DatabaseKind.InMemory,
            var other => throw new InvalidOperationException(
                $"{Key} '{other}' is not one of SqlServer, Sqlite, InMemory."),
        };

    public static void Configure(DbContextOptionsBuilder options, IConfiguration configuration)
    {
        // "ads-main" is both the vault secret name and the ConnectionStrings key (existing contract).
        var connectionString = configuration["ads-main"] ?? configuration.GetConnectionString("ads-main");
        switch (Resolve(configuration))
        {
            case DatabaseKind.InMemory:
                options.UseInMemoryDatabase(configuration["Database:InMemoryName"] ?? "adsdb-inmem");
                break;
            case DatabaseKind.Sqlite when ServiceTools.IsConfigured(connectionString):
                options.UseSqlite(connectionString);
                break;
            case DatabaseKind.SqlServer when ServiceTools.IsConfigured(connectionString):
                options.UseSqlServer(connectionString);
                break;
            default:
                throw new InvalidOperationException(
                    "No database is configured. Name a Key Vault that holds an \"ads-main\" connection string (KeyVaultName), " +
                    "or set ConnectionStrings__ads-main. Database__Provider=InMemory starts the server without one, for testing only.");
        }
    }
}
```
Because the options callback runs when the first `DesignDbContext` is built - inside `DatabaseBootstrapper.Initialize` before any request - a missing connection string still stops the host before it serves, with the same message as today.

### DatabaseBootstrapper
```csharp
internal static class DatabaseBootstrapper
{
    public static void Initialize(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
        db.Database.EnsureCreated();
        if (db.Database.IsSqlServer())            // NOT IsRelational(): the DDL is T-SQL
            db.Database.ExecuteSqlRaw(LegacySqlServerUpgrade);   // move the existing raw string verbatim
    }
    private const string LegacySqlServerUpgrade = """ ...Program.cs lines 117-186 verbatim... """;
}
```
Use a `const string` (or `ExecuteSqlRaw` with the literal as today); do not reformat the DDL - Phase 4 deletes it.

### DesignDbContextFactory (design time)
```csharp
// Source: prototype (compiles in Server with no Design package)
using Microsoft.EntityFrameworkCore.Design;
public sealed class DesignDbContextFactory : IDesignTimeDbContextFactory<DesignDbContext>
{
    public DesignDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DesignDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ads-design-time").Options);   // never connected at design time for model snapshots
}
```
Test: `new DesignDbContextFactory().CreateDbContext([]).Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer"` (that exact string is what `DesignDbContext.OnModelCreating` compares).

### Test host
```csharp
// Source: prototype HarnessProto.cs, adapted (all of this ran green in Debug and Release)
public sealed class AdsServerFactory : WebApplicationFactory<Program>
{
    public string DbFile { get; } = Path.Combine(Path.GetTempPath(), $"ads-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");                         // top-level-visible
        builder.UseSetting("ApplicationInsights:ConnectionString", "");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ads-main"] = $"Data Source={DbFile}",             // read lazily
        }));
        builder.ConfigureTestServices(services =>
            services.AddAuthentication(o =>
            {
                o.DefaultScheme = o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(DbFile) + "*"))
            File.Delete(file);
    }
}
```
Do **not** name the constant `Scheme` inside the handler - it hides `AuthenticationHandler<T>.Scheme` and trips CS0108 (an error under `TreatWarningsAsErrors`). The prototype used `SchemeName`.

### Test auth handler
```csharp
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "x-test-user";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
            return Task.FromResult(AuthenticateResult.NoResult());          // -> challenge -> gRPC Unauthenticated
        var claims = new List<Claim>
        {
            new("http://schemas.microsoft.com/identity/claims/objectidentifier", user.ToString()),
            new("http://schemas.microsoft.com/identity/claims/scope", "Server.Access"),   // AzureAd:Scopes
            new("name", "Tester"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
```
Recommended extension for Phase 5/8 reuse (TEST-02 needs malformed `oid`, missing `tid`, roles): let the handler read extra claims from a repeated `x-test-claim: <type>|<value>` header and a `x-test-no-scope` switch, so tests state the exact token shape per call. Keep it in Server.Tests.

### Channel helper
```csharp
GrpcChannel Channel(AdsServerFactory f, Guid? user)
{
    var http = f.CreateDefaultClient(new HeaderHandler(user));              // DelegatingHandler adds x-test-user
    return GrpcChannel.ForAddress(http.BaseAddress!, new GrpcChannelOptions { HttpClient = http });
}
```

### "Every service" proof
```csharp
var source = factory.Services.GetRequiredService<EndpointDataSource>();
var services = source.Endpoints
    .Select(e => e.Metadata.GetMetadata<Grpc.AspNetCore.Server.GrpcMethodMetadata>())
    .Where(m => m is not null).Select(m => m!.Method.ServiceName).Distinct().Order().ToArray();
Assert.Equal(new[] { "Cost", "Deploy", "Design" }, services);               // observed: Design, Deploy, Cost; 21 methods
```
Then one unary call per service: `Design.GetSaved` (200 + names), `Deploy.GetGuardrailPolicy` (in-band 200) or `SaveSubscriptionInfo` (503 without a vault), `Cost.GetRetailPrices` with an invalid request (400, no network). Do not call any method that reaches `prices.azure.com` or ARM; if a later test needs those, replace the `"arm"`/`"RetailPrices"` `IHttpClientFactory` with the existing `RouteTableHttp` via `ConfigureTestServices`.

### "Failure surfaces as status, host survives" proof
```csharp
using var scope = factory.Services.CreateScope();
await scope.ServiceProvider.GetRequiredService<DesignDbContext>().Database.ExecuteSqlRawAsync("DROP TABLE AdsDesigns");
var ex = await Assert.ThrowsAsync<RpcException>(() => designClient.SaveAsync(new SaveDesignRequest { Name = "a", Data = "{}" }).ResponseAsync);
Assert.Equal(StatusCode.Unknown, ex.StatusCode);                            // observed
Assert.Equal(200, (await deployClient.GetGuardrailPolicyAsync(new Empty())).StatusCode);   // host still serving
```
(Needs `using Microsoft.EntityFrameworkCore;`.) Note this is a one-off fault-injection recipe, not proof that no request path calls `EnsureCreated` (EF's `EnsureCreated` is a no-op when other tables exist), so the EnsureCreated guard is a source grep (below).

### EnsureCreated hot-path guard
Acceptance grep: `grep -rn "EnsureCreated" src/AzureDesignStudio.Services src/AzureDesignStudio.Server --include='*.cs'` must match only `Server/Persistence/DatabaseBootstrapper.cs`. Optionally a test that walks up to `global.json`, reads `src/AzureDesignStudio.Services/**/*.cs` and asserts none contains `EnsureCreated`.

### Dev ergonomics (replaces `#if DEBUG` InMemory)
```json
// src/AzureDesignStudio.Server/Properties/launchSettings.json, profile "AzureDesignStudio.Server"
"environmentVariables": {
  "ASPNETCORE_ENVIRONMENT": "Development",
  "Database__Provider": "InMemory"
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `#if DEBUG` selects EF provider; Debug and Release are different apps | `Database:Provider` configuration; one code path | this phase | Debug tests exercise what Release ships; EF migrations (Phase 4) become possible |
| Schema bootstrap inline in `Program.cs` | `DatabaseBootstrapper`, SQL Server DDL gated by provider | this phase | Migrations in Phase 4 replace one call site |
| Service-class-only tests with a fake `ServerCallContext` | Add real-host tests through `TestServer`; keep the 134 direct tests | this phase | Covers auth, `[RequiredScope]`, interceptor and routing, which direct calls skip |
| `WebApplicationFactory` config via `ConfigureAppConfiguration` for pre-`Build` reads | `UseSetting` / `ConfigureHostConfiguration` (and `ConfigureWebApplicationBuilder` in .NET 11 Preview 7+) | documented | The doc's third option is .NET 11 only; not available on `net10.0`, do not use |

**Deprecated/outdated:**
- Roadmap's "Mvc.Testing 10.0.13": superseded by 10.0.15 (same train as the repo's other ASP.NET packages).
- Roadmap's "new `AzureDesignStudio.Server.Tests`": already exists; extend it.
- Roadmap's "RPC hot path no longer calls `EnsureCreated`": already true; add a guard, do not re-do work.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `Crypto:Provider` should exist in Phase 3 with exactly one accepted value (`KeyVault`) and fail fast otherwise; Phase 5 adds the development provider. This is my reading of the roadmap note ("`Crypto:Provider` read from configuration") against Phase 5's CRED-01/02 ownership. | Pattern 5 | If the owner wants a working dev provider now, Phase 3 grows; if they want no seam yet, drop `CryptoProvider.cs` and keep the `ConfigureTestServices` fake |
| A2 | Putting `Database__Provider=InMemory` in the `launchSettings.json` dev profile is an acceptable replacement for the `#if DEBUG` InMemory default (the alternative, a committed `appsettings.Development.json`, is blocked by `.gitignore`). Only `dotnet run`/VS honour launch profiles. | Pattern 4 | Developers who run the built DLL directly in Development must now set the variable, or get the "No database is configured" message |
| A3 | Key Vault configuration should load only for `SqlServer` + a configured vault name (rather than `!IsDevelopment()` as `.planning/research/ARCHITECTURE.md` suggests). | Pattern 4 | Slightly different rule from the earlier research doc; both satisfy "no `#if DEBUG`" |
| A4 | Server needs a hard dependency on `Microsoft.EntityFrameworkCore.Sqlite` (production-shipped, adds native `SQLitePCLRaw` assets to the image) rather than keeping Sqlite test-only and overriding `DbContextOptions` in `ConfigureTestServices`. The roadmap says `Database:Provider` is read from configuration, which needs the provider in Server. | Standard Stack | A reviewer may prefer a smaller image; fallback is a test-only override and no Sqlite value in config |
| A5 | Adding the AutoMapper suppressions (Pitfall 2) is within this phase's remit as a Wave 0 unblock; the BUILD-04 owner (Phase 2) may prefer to land it there. | Pitfall 2 | If Phase 2 fixes it first, drop the task; if neither does, nothing in Phase 3 can be verified |
| A6 | Test run time stays small (prototype: whole suite ~9 s incremental in Release). A cold CI build includes the WASM client in Release without AOT. | Validation | Wave timings only; no correctness risk |

## Open Questions

1. **Who lands the two new AutoMapper suppressions?**
   - What we know: reproduced at HEAD without Phase 3 edits; advisories published 2026-11-13; only `GHSA-rvv3-g6hj-g44x` is suppressed; the dated expiry (2027-01-31) and Phase 8 replacement plan exist.
   - What's unclear: whether the owner extends the "single dated exception" to three IDs or replaces AutoMapper sooner.
   - Recommendation: Wave 0 task in Phase 3 adding both IDs under the same expiry property, with the commit message citing the advisory dates; do not widen the expiry.
2. **Should the Server project reference Core directly?**
   - What we know: the roadmap and CONTEXT say yes; it builds and changes no lock file [VERIFIED: prototype]; Server.Tests already uses `AzureDesignStudio.Core.Common` types transitively; the Dockerfile already copies Core's csproj and lock.
   - What's unclear: nothing in Phase 3 requires it.
   - Recommendation: add it (one line, no risk), as the roadmap states.
3. **InMemory warning log for non-production providers.** Today a Release host logs a warning for InMemory. Recommend logging one warning for `InMemory` and `Sqlite` in `DatabaseBootstrapper`; the user may want it quieter in tests (it does not affect assertions).
4. **TEST-05/TEST-06 follow-ups.** CI already builds and runs Server.Tests (`build.yml` lines for build/test). Test-count assertion and coverage gates are Phase 8; nothing to add in CI for Phase 3 unless the owner wants a Debug-configuration run (recommended as a plan-level local check only).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | ✓ | 10.0.403 (global.json pins 10.0.401, `rollForward: latestPatch`) | CI uses 10.0.401 via `setup-dotnet` |
| ASP.NET Core shared runtime | test run | ✓ | 10.0.14 | - |
| nuget.org network | restore of Mvc.Testing + lock regeneration | ✓ | - | - |
| git | nbgv versioning during build | ✓ | - | Without a `.git` the copy needed `git init` (not an issue in the real repo) |
| SQL Server / Key Vault / Azure credentials | **not required** by any Phase 3 test | ✗ (intentionally) | - | InMemory/SQLite and the lazy `CryptoService` |
| SQLite native library | Sqlite provider | ✓ (bundled via `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12) | - | - |
| Docker | not needed for Phase 3 tests; image build step in CI is unchanged | not probed | - | - |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** none.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit 2.9.2 + Microsoft.NET.Test.Sdk 17.12.0 (existing `AzureDesignStudio.Server.Tests`, 134 passing tests at the start of this phase [VERIFIED: 139 total in the prototype run minus 5 prototype tests]) |
| Config file | none - see Wave 0 for the csproj package change |
| Quick run command | `dotnet test src/AzureDesignStudio.Server.Tests --filter "FullyQualifiedName~Hosting"` |
| Full suite command | `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` (CI shape) and the same without `-c Release` (Debug) |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| TEST-01 / SC1 | Real `Program` boots with placeholder appsettings, no Key Vault, no credentials, temp SQLite, in Debug and Release | integration | `dotnet test src/AzureDesignStudio.Server.Tests --filter "FullyQualifiedName~ServerHostTests.Boots"` | ❌ Wave 0 |
| TEST-01 / SC1 | No `#if` in `Program.cs` | static guard | `! grep -nE '^#(if|else|elif)' src/AzureDesignStudio.Server/Program.cs` | ✅ (command) |
| TEST-01 / SC2 | Design, Deploy, Cost each answer through the test auth handler; endpoint enumeration returns exactly those 3 services | integration | `... --filter "FullyQualifiedName~ServerHostTests.EveryServiceIsReachable"` | ❌ Wave 0 |
| TEST-01 / SC2 | No credentials -> `Unauthenticated`; wrong scope -> `PermissionDenied`; handler fault -> `Unknown` and the host keeps answering | integration | `... --filter "FullyQualifiedName~ServerHostTests.Failure"` | ❌ Wave 0 |
| TEST-01 / SC3 | Creating a design writes a `.db` under `Path.GetTempPath()`; file removed on dispose; no `workspace.db`/`deploy.db` created or referenced | integration | `... --filter "FullyQualifiedName~ServerHostTests.TempSqlite"` | ❌ Wave 0 |
| TEST-01 / SC4 | `Program` reachable (compiles as `WebApplicationFactory<Program>`) | compile | `dotnet build src/AzureDesignStudio.Server.Tests` | ❌ Wave 0 |
| TEST-01 / SC4 | No `EnsureCreated` outside `DatabaseBootstrapper` | static guard | `grep -rn "EnsureCreated" src/AzureDesignStudio.Services src/AzureDesignStudio.Server --include='*.cs'` (single hit expected) | ✅ (command) |
| TEST-01 / design | `DatabaseProvider.Resolve/Configure`: default SqlServer, case-insensitive values, unknown value throws naming the three values, missing connection string keeps the existing message, placeholder `<kv name>` counts as unset | unit | `... --filter "FullyQualifiedName~DatabaseProviderTests"` | ❌ Wave 0 |
| TEST-01 / design | `DatabaseBootstrapper` on SQLite temp file creates tables and never runs T-SQL; `DesignDbContextFactory` reports the SqlServer provider name without connecting | unit/integration | `... --filter "FullyQualifiedName~DatabaseBootstrapperTests"` | ❌ Wave 0 |
| TEST-01 / regression | The 134 existing direct-call tests keep passing | unit | full suite command | ✅ |

### Sampling Rate
- **Per task commit:** the quick run command (Hosting tests, a few seconds incremental).
- **Per wave merge:** full suite in Debug and in Release (`-p:RunAOTCompilation=false`).
- **Phase gate:** full suite green in both configurations, plus the two static greps, plus `dotnet restore src/AzureDesignStudio.Server.Tests --locked-mode` succeeding after the lock files are committed, before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] Restore unblock: add `GHSA-gpwp-5797-mjff` and `GHSA-6vhm-j9h7-f9gh` to `Directory.Build.props`; update the message in `Directory.Build.targets` - without it no command above can run (see Pitfall 2)
- [ ] `Directory.Packages.props`: `Microsoft.AspNetCore.Mvc.Testing` 10.0.15; `AzureDesignStudio.Server.csproj`: Sqlite package + Core project reference; `AzureDesignStudio.Server.Tests.csproj`: Mvc.Testing - then regenerate and commit only the Server and Server.Tests lock files (revert unrelated SDK-drift lock changes)
- [ ] `Hosting/AdsServerFactory.cs`, `Hosting/TestAuthHandler.cs`, `Hosting/GrpcTestClients.cs`
- [ ] `Hosting/ServerHostTests.cs`, `Hosting/DatabaseProviderTests.cs`, `Hosting/DatabaseBootstrapperTests.cs`
- [ ] Production edits: `Program.cs` (no `#if`, `public partial class Program`), `Persistence/*`, `CryptoProvider`, `launchSettings.json`; AGENTS.md NOTES updated (InMemory/DEBUG and reflection/DEBUG sentences)

## Security Domain

`security_enforcement` is enabled (config `workflow.security_enforcement: true`, ASVS level 1, block on high).

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes (test seam only) | The test scheme exists only in `Server.Tests`; production keeps `AddMicrosoftIdentityWebApi`. Verify `grep -rn "x-test-user\|TestAuthHandler" src/AzureDesignStudio.Server` is empty |
| V3 Session Management | no | stateless bearer tokens |
| V4 Access Control | yes | Tests prove deny-by-default today only for `[Authorize]`+`[RequiredScope]` (Design, Deploy) and the intentional anonymity of `Cost`; the full reflection-based deny-by-default test is IDA-03 (Phase 5) |
| V5 Input Validation | yes (config) | `Database:Provider` / `Crypto:Provider` values validated against an allow-list; unknown value stops startup |
| V6 Cryptography | yes | Do not add any plaintext/dev crypto in this phase; `Crypto:Provider` accepts only `KeyVault`. The development provider and the production-refusal rule belong to Phase 5 (CRED-01/02) |
| V14 Configuration | yes | No secret in `appsettings.json`/`launchSettings.json`; placeholders stay placeholders (`ServiceTools.IsConfigured`); test connection strings point at temp files only |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Test auth handler reachable in production | Spoofing / Elevation | Handler and header live only in the test assembly; never registered in `Program.cs` |
| `Database:Provider=InMemory`/`Sqlite` set in a deployed server, silently losing or exposing data | Tampering / Repudiation | Keep the startup warning; default remains SQL Server and fails fast without a connection string |
| Key Vault configuration loaded for a provider that does not need it (credential prompts, unexpected network at boot) | Denial of Service | Load the vault source only for `SqlServer` + a configured vault name |
| Legacy T-SQL run against the wrong provider | Tampering | `IsSqlServer()` guard |
| Test run touching a developer's data or a live tenant | Information Disclosure | All DB files in `Path.GetTempPath()`; no ARM/price HTTP in tests (Deploy/Cost calls limited to in-band, non-network paths; use `RouteTableHttp` when needed) |
| Real Application Insights channel started from a placeholder in tests | Information Disclosure | Empty `ApplicationInsights:ConnectionString` in the test host |

## Sources

### Primary (HIGH confidence)
- Repo files read this session: `src/AzureDesignStudio.Server/Program.cs`, `AzureDesignStudio.Server.csproj`, `Services/CryptoService.cs`, `Services/HostedServices.cs`, `Utils/DesktopGrpcEndpoint.cs`, `appsettings.json`, `Properties/launchSettings.json`, `Dockerfile` (grep), `src/AzureDesignStudio.Services/{DesignService,ServiceTools,RetailPriceService}.cs`, `Models/DesignDbContext.cs`, `AzureDesignStudio.Services.csproj`, `src/AzureDesignStudio.Server.Tests/{*.csproj,TestSupport.cs,ServerPlumbingTests.cs,InProcessTests.cs}`, `src/AzureDesignStudio.Desktop/MauiProgram.cs` (lines 78-79, 205-211), `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `global.json`, `.github/workflows/build.yml`, `.gitignore` (line 354), `.planning/{ROADMAP,REQUIREMENTS,STATE}.md`, `.planning/research/ARCHITECTURE.md`.
- Prototype runs described in "Prototype evidence" (scratch copy; commands and outputs observed).
- nuget.org flat container + search API (Mvc.Testing / TestHost 10.0.0-10.0.15; owner Microsoft; downloads).
- GitHub advisory API for GHSA-gpwp-5797-mjff and GHSA-6vhm-j9h7-f9gh (published 2026-11-13, severity high).

### Secondary (MEDIUM confidence)
- [CITED: https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0] - default environment `Development`; `ConfigureAppConfiguration` not visible before `Build`; `ConfigureHostConfiguration` via `CreateHost` needs `args` passed to `CreateBuilder`; `ConfigureWebApplicationBuilder` is .NET 11 Preview 7+. (The page was fetched for the first 100,000 of 268,315 characters; the `public partial class Program` section was not in the read portion - that part is [VERIFIED: prototype] instead.)
- [CITED: https://learn.microsoft.com/en-us/aspnet/core/grpc/test-services?view=aspnetcore-10.0] - gRPC integration tests host the app in `TestServer` (`Microsoft.AspNetCore.TestHost`) and use `ConfigureWebHost` to override dependencies.

### Tertiary (LOW confidence)
- None. Everything load-bearing was either read in the repo or reproduced.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - versions from the NuGet flat container; lock-file effects observed.
- Architecture: HIGH - the full host/auth/gRPC/SQLite path ran green in Debug and Release.
- Pitfalls: HIGH - 1, 2, 3, 4, 5, 6 reproduced; 7-10 observed incidentally.

**Research date:** 2026-11-14
**Valid until:** 2026-12-14 (30 days). Re-check the AutoMapper advisory list and the `10.0.x` patch train (a 10.0.16 will change central versions and lock files) before planning if later.
