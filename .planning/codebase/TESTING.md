---
last_mapped_commit: a05ef8c00d2ff2dc2f0d9bea0a220051852405a8
last_mapped_at: 2026-10-21
---
# Testing Patterns

**Analysis Date:** 2026-10-21

<!-- refreshed: 2026-10-21 -->

## Test Framework

**Runner:**
- xUnit 2.9.2 (`src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:16`), `Microsoft.NET.Test.Sdk` 17.12.0, `xunit.runner.visualstudio` 2.8.2.
- Config: single csproj, no `xunit.runner.json`, no `.runsettings`. Default parallel collection behaviour applies (per test class).
- Coverage collector: `coverlet.collector` 6.0.2 (`csproj:21`) — referenced, but **no** CI step or documented command invokes it.

**Assertion Library:**
- Built-in xUnit `Assert` only. There is **no** FluentAssertions, Shouldly, Moq, or NSubstitute anywhere in the solution (verified across all `*.csproj`).
- Most-used asserts: `Assert.Equal` (876), `Assert.Contains` (349), `Assert.Single` (176), `Assert.True` (155), `Assert.False` (119), `Assert.Empty` (109).

**Run Commands:**

```bash

# Offline suite - the CI gate (no network, no credentials)

dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"

# Exact CI invocation

dotnet test src/AzureDesignStudio.Core.Tests -c Release --no-build --filter "Category!=LiveAzure"

# Live Azure tier (requires az login / DefaultAzureCredential + a subscription)

dotnet test src/AzureDesignStudio.Core.Tests --filter "Category=LiveAzure"

# One fixture

dotnet test src/AzureDesignStudio.Core.Tests --filter "FullyQualifiedName~ReconcileEngineTests"

# Coverage (collector is referenced; nothing automates this today)

dotnet test src/AzureDesignStudio.Core.Tests --collect:"XPlat Code Coverage"
```

(`README.md:241-257`, `AGENTS.md:90`, `.github/workflows/build.yml:37-38`)

## Test File Organization

**Location:**
- One project, flat directory: `src/AzureDesignStudio.Core.Tests/` — 68 `.cs` files, ~11.6k lines. Despite the name it tests **three** projects: `AzureDesignStudio.Core`, `AzureDesignStudio.Workspace`, `AzureDesignStudio.Discovery` (project references in `AzureDesignStudio.Core.Tests.csproj:28-30`).
- No subdirectories, no `Unit/`, `Integration/` split. Add new tests by subject, not by layer.

**Naming:**
- File = `<Subject>Tests.cs` matching the type under test: `ReconcileEngineTests.cs` → `ReconcileEngine`; `ArmTemplateValidatorTests.cs` → `ArmTemplateValidator`. 67 of 68 files follow this; the exception is the live base class `TestBase.cs`.
- Test methods: `Subject_Behavior_Expectation` with underscores — 544 of ~639 methods (`InventoryQuery_TypeContains_IsCaseInsensitive`, `Artifact_HalfBoundTargetIdentity_Throws`, `SaveLoadListDeleteRoundTrip`). Older UI/model tests use plain PascalCase sentences (`ConnectionLabelFollowsEndpointTypes`, `VnetLinkGetsBothArrows` in `LinkTypeTests.cs`). New tests: use the underscore form.
- Class-level `/// <summary>` on 62 of 68 files, naming the slice the tests defend (`/// <summary>v2 plan 10.4 online half: …</summary>`).

## Test Structure

**Suite Organization** (a real file, abridged from `src/AzureDesignStudio.Core.Tests/NameAvailabilityClientTests.cs`):

```csharp
namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 10.4 online half: the live availability client POSTs only the verified
/// checkNameAvailability shapes …, never invents a verdict.
/// </summary>
public class NameAvailabilityClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler { … }
    private sealed class FakeCredential : TokenCredential { … }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static NameAvailabilityClient Client(FakeHandler handler) =>
        new(new ArmTransport(new HttpClient(handler), new FakeCredential(), ArmEndpointSet.AzureCloud));

    [Fact]
    public void Supports_IsTheVerifiedTableExactly_NothingElse() { … }
}
```

Standard order inside a fixture: class doc → constants → nested fakes → static builders/helpers → tests grouped by theme with `// --- section ---` comments (see `AdsToolEngineTests.cs`).

**Patterns:**
- One behavior per test; arrange with direct object construction, no container unless the seam requires it (`DataModelFactory`, `WorkspaceDb`).
- Tests build the smallest real object graph, not mocks of the domain: `new VirtualNetworkModel { Name = "ads-vnet" }` then `vnet.AddSubnet(...)` (`src/AzureDesignStudio.Core.Tests/OfflineArmTemplateTests.cs`).
- Negative tests assert the *typed* failure, not just "throws": `var e = Assert.Throws<AdsDesignFormatException>(() => AdsDesignText.Read(text)); Assert.Contains("nodes", e.Message);` (`AdsDesignTextTests.cs:87`).
- Golden/contract tests iterate the whole catalog with `[Theory]` + `[MemberData]` so a new resource cannot silently skip coverage.
- Serialization round-trips are asserted both ways (`LinkTypeTests.LinkLabelsAndEndpointsSurviveSaveAndLoad`, `DeploymentStackTests`).

## Mocking

**Framework:** None. Hand-rolled nested fakes are the convention.

**Patterns:**

HTTP boundary — a `sealed` handler that records requests and plays scripted responses:

```csharp
private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
{
    public ConcurrentQueue<string> Uris { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uris.Enqueue(request.RequestUri!.ToString());
        return Task.FromResult(responder(request, Uris.Count - 1));
    }
}
```

(`src/AzureDesignStudio.Core.Tests/FailureModeSuiteTests.cs:28-36`; variants without the request index in `NameAvailabilityClientTests.cs`, `ResourceGraphTests.cs`, `ScanEngineTests.cs`.)

Identity — a `TokenCredential` that never contacts Entra:

```csharp
private sealed class FakeCredential : TokenCredential
{
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
    public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
        => new("fake-token", DateTimeOffset.MaxValue);
}
```

(Copy this into the fixture; it is duplicated per file by design — there is no shared `TestSupport` project.)

Time and retry — inject the clock/delay through the production seam instead of sleeping:

```csharp
var delays = new List<TimeSpan>();
var transport = new ArmTransport(new HttpClient(handler), new FakeCredential(), ArmEndpointSet.AzureCloud,
    (ts, _) => { delays.Add(ts); return Task.CompletedTask; }, maxThrottleRetries);
```

(`FailureModeSuiteTests.CreateTransport`; same trick for `WhatIfTokenGate(TimeSpan ttl, Func<DateTimeOffset>? clock)`.)

**What to Mock:**
- The network edge (`HttpMessageHandler`) and the credential. Everything else in `Discovery` takes an `ArmTransport`/`ResourceGraphClient`, so one fake handler covers the whole engine.
- Gateways for interactive flows (`AdsContext.StencilLoader`, `AdsHostAuth`, `AdsHostFeatures`) are nullable delegates meant to be replaced — use that, don't subclass the component.

**What NOT to Mock:**
- Domain models, AutoMapper (`new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper()`), `BlazorDiagram`, or the SQLite workspace. Tests use the real thing because the contract *is* the real serialization/database behavior.
- Do not introduce a mocking library — it would be the second convention and no test needs it.

## Fixtures and Factories

**Test Data:**
- Collection-expression data sources:

```csharp
public static IEnumerable<object[]> NewTypes =>
[
    [AdsConstants.EventGrid, "Microsoft.EventGrid/topics", "2024-06-01-preview", "ads-eg"],
    [AdsConstants.ContainerApp, "Microsoft.App/containerApps", "2024-02-02", "ads-ca"],
    …
];

[Theory]
[MemberData(nameof(NewTypes))]
public void StencilKeyProducesArmResourceWithContractedApiVersion(string key, string armType, string apiVersion, string name)
```

(`src/AzureDesignStudio.Core.Tests/NewResourceTypesArmTests.cs`; identical shape with 48 catalog keys in `StencilExportGoldenTests.CatalogKeys`.)
- Expected JSON is raw string literals, with `__TOKEN__` placeholders + `.Replace(...)` for real resource ids (`ChangeFeedTests.cs`, `AdsToolEngineTests.cs`).
- Live-Azure tests are built from the same model types and validated server-side, so they double as documentation of a valid topology (`ApiMTests.APIMWithVnetTest`).

**Location:**
- No fixtures folder. Everything lives in the fixture file. The one shared runtime artifact is the stencil catalog, copied from the app by the csproj:

```xml
<Content Include="..\AzureDesignStudio\wwwroot\ads-stencils.json" Link="ads-stencils.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

(`AzureDesignStudio.Core.Tests.csproj:34`) and read as `Path.Combine(AppContext.BaseDirectory, "ads-stencils.json")` (`StencilTests.cs`).

**Temporary storage pattern** (use this for anything touching `WorkspaceDb`):

```csharp
public class WorkspaceTests : IDisposable
{
    private readonly string _directory;
    private readonly WorkspaceDb _workspace;

    public WorkspaceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ads-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _workspace = new WorkspaceDb(Path.Combine(_directory, "workspace.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // Windows file lock
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
```

(`src/AzureDesignStudio.Core.Tests/WorkspaceTests.cs:18-38`; `ChangeFeedTests.cs` uses the same shape without the pool clear.)

**Live fixture:**

```csharp
[Trait("Category", "LiveAzure")]
public class TestBase
{
    public TestBase() { var armClient = new ArmClient(new AzureCliCredential()); … }

    protected async Task ValidateTemplate(ArmTemplate armTemplate, string? parameters = null) { … }
}
```

(`src/AzureDesignStudio.Core.Tests/TestBase.cs:12-49`.) The trait on the base class is inherited by all derived classes (`ApiMTests`, `NetworkTests`, `SqlDatabaseTests`, `WebTests`), which is what makes `--filter "Category!=LiveAzure"` sufficient.

## Coverage

**Requirements:** None enforced. No coverage threshold, no ratchet, no coverage artifact in CI, no `coverlet` invocation anywhere despite the package reference.

**View Coverage:**

```bash
dotnet test src/AzureDesignStudio.Core.Tests --collect:"XPlat Code Coverage"
```

Produces Cobertura XML under `TestResults/<guid>/coverage.cobertura.xml`; nothing consumes it yet.

**Known Coverage Gaps:**
- **UI/Blazor layer untested.** No bUnit (not referenced) and zero tests touch Razor components: `src/AzureDesignStudio/Components/DiagramPanel.razor.cs`, `TopMenu.razor.cs`, `Components/MenuDrawer/*`, `Pages/Index.razor`. `AdsDiagram`, `DiagramHistory`, `AdsCommandService`, `CostService` have no test file.
- **gRPC wrapper services untested.** `src/AzureDesignStudio/Services/DesignGrpcService.cs`, `DeployGrpcService.cs`, `CostGrpcService.cs` — the offline-fallback/deferred-save logic (`IsDeferredSaveFailure`, `FlushPendingAsync`) is only exercised indirectly through `IDesignOfflineStore` tests.
- **Server untested.** `src/AzureDesignStudio.Server/Services/*` (Design/Deploy/RetailPrice/Crypto) and `DesignDbContext` have no tests.
- **Hosts untested.** `src/AzureDesignStudio.Mcp/Program.cs` (stdio guard, read-only DB open, tool wiring `AdsMcpTools`) and `src/AzureDesignStudio.Desktop/MauiProgram.cs` are covered only at the engine level via `AdsToolEngine` (`AdsToolEngineTests.cs`).
- **Generator internals untested.** `src/AzureDesignStudio.SourceGeneration/AzureNodeDtoGenerator.cs` is verified only by reflection contract tests (`DtoGeneratorContractTests.cs`), not by snapshotting emitted code.
- **Live tier is manual-only** (9 facts) and depends on the `ads-arm-test-rg` resource group existing in the caller's subscription; it can fail on subscription quotas, not code.

## Test Types

**Unit Tests:**
- The bulk of the suite: pure engine/analyzer/planner logic in `src/AzureDesignStudio.Discovery/**` and model/ARM generation in `src/AzureDesignStudio.Core/**`, constructed directly with no container (`ReconcileEngineTests.cs`, `DeployScopePlannerTests.cs`, `ArmTemplateValidatorTests.cs`, `LinkTypeTests.cs`).

**Contract / Golden Tests:**
- `DtoGeneratorContractTests.cs` — every `[MapToDto]` model has a generated DTO with a unique `TypeKey`.
- `StencilExportGoldenTests.cs` — every catalog stencil key resolves to a real model and exports ARM without `NotImplementedException`; unresolvable keys must degrade to `UnknownNodeModel`.
- `StencilTests.cs` — `ads-stencils.json` schema, unique keys, icon-path convention, category coverage.
- `NewResourceTypesArmTests.cs` — key → ARM type + api-version table.

**Failure-Mode / Resilience Tests:**
- `FailureModeSuiteTests.cs` replays throttle (429 + `Retry-After`), partial permissions, offline and malformed payloads against scripted handlers and asserts each lands in the right bucket (caveat vs not-available vs typed throw) — never a hang or silent empty result.
- `ResourceGraphTests.cs`, `WhatIfTests.cs`, `DeploymentStackTests.cs` use the same handler pattern for 202/Accepted polling and error bodies.

**Persistence Tests:**
- `WorkspaceTests.cs`, `ChangeFeedTests.cs`, `TypedGraphReloadTests.cs`, `ExpiryWatchTests.cs` against a real temp-file SQLite database.

**Integration Tests (live):**
- `ApiMTests`, `NetworkTests`, `SqlDatabaseTests`, `WebTests` — generate ARM from model graphs and call `ArmDeploymentResource.ValidateAsync` against `ads-arm-test-rg`. Deployment-preflight only; nothing is left behind. Requires `az login`.

**E2E Tests:** Not used.

## Common Patterns

**Async Testing:**

```csharp
[Fact]
public async Task LinkLabelsAndEndpointsSurviveSaveAndLoad()
{
    var mapper = new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper();
    var graph = await DataModelFactory.SaveDiagramToDto(diagram, mapper);
    …
}
```

(`LinkTypeTests.cs`.) Tests are `async Task`, never `async void`; no `ConfigureAwait` in tests.

**Error Testing:**

```csharp
var e = Assert.Throws<AdsDesignFormatException>(() => AdsDesignText.Read(text));
Assert.Contains("nodes", e.Message);

var ex = await Assert.ThrowsAsync<ResourceGraphException>(() =>
    client.QueryResourcesAsync(…));
```

(`AdsDesignTextTests.cs:87`, `DeploymentStackTests.cs:306`.) Prefer asserting the message/code that the UI or caller branches on, not just the exception type.

**Diagnostic-result testing** (for engines that return findings instead of throwing):

```csharp
var (ids, diags) = DeploymentStack.PlannedManagedResourceIds(template, "sub-1", "rg1");
Assert.Empty(diags);
Assert.Equal(new[] { … }, ids);
```

(`DeploymentStackTests.cs:394`.) Assert on the stable diagnostic `Code` values, never on prose.

**Timing-sensitive tests** are rare and must be bounded: `await Task.Delay(150)` for background work (`ScanEngineTests.cs:184`) or `await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10)))` as a hang guard (`TypedGraphReloadTests.cs:32`). Do not add new sleeps where a seam (delay delegate, `Func<DateTimeOffset>` clock) exists.

## CI

`.github/workflows/build.yml`:

```yaml
    - name: Build
      run: |
        dotnet build src/AzureDesignStudio.Server -c Release
        dotnet build src/AzureDesignStudio -c Release
        dotnet build src/AzureDesignStudio.Mcp -c Release
        dotnet build src/AzureDesignStudio.Core.Tests -c Release

    - name: Test (offline)
      run: dotnet test src/AzureDesignStudio.Core.Tests -c Release --no-build --filter "Category!=LiveAzure"
```

- Ubuntu runner; the full `.sln` is never built (MAUI desktop is Windows-only) — CI builds an explicit project set (`build.yml:26-35`).
- The test project carries a `DropForeignThreadingAnalyzers` target (`AzureDesignStudio.Core.Tests.csproj:40-44`) because it inherits analyzers through the WASM app's `Azure.Bicep.Decompiler`; keep it when touching that csproj.
- CodeQL runs in a separate job (`.github/workflows/codeql-analysis.yml`, `.github/workflows/build.yml` `codeql` job). No coverage upload, no test-result publishing, no scheduled live-Azure run.

---

*Testing analysis: 2026-10-21*
