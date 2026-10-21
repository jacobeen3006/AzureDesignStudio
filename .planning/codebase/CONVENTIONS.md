---
last_mapped_commit: a05ef8c00d2ff2dc2f0d9bea0a220051852405a8
last_mapped_at: 2026-10-21
---
# Coding Conventions

**Analysis Date:** 2026-10-21

<!-- refreshed: 2026-10-21 -->

## Naming Patterns

**Files:**
- One public type per file, file name = type name: `src/AzureDesignStudio.Discovery/Iac/ArmTemplateValidator.cs` holds `ArmTemplateValidator`.
- Resource editors: `<Resource>Model.cs` + `<Resource>Form.razor` side by side in a per-service folder (`src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs`, `src/AzureDesignStudio.Core/Network/VirtualNetworkForm.razor`).
- ARM schema types keep the ARM schema name exactly (`VirtualNetworks`, `AddressSpace`, `InboundNatRulePropertiesFormat`) under `src/AzureDesignStudio.AzureResources/<Service>/`.
- DTOs end in `Dto` (`AzureNodeDto`, `LinkModelDto`), AutoMapper profiles in `Profile` (`src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs`), diagrams in `Graph` (`DiagramGraph.cs`).
- Engine/client/analyzer suffixes carry meaning: `*Engine`, `*Analyzer`, `*Planner`, `*Client`, `*Detector`, `*Collector` under `src/AzureDesignStudio.Discovery/<Area>/`.
- Tests: `<SubjectUnderTest>Tests.cs` in the flat test project, e.g. `src/AzureDesignStudio.Core.Tests/ReconcileEngineTests.cs`. The only file not matching is `src/AzureDesignStudio.Core.Tests/TestBase.cs`.

**Functions:**
- PascalCase for all methods (`SaveDesign`, `GetArmResources`, `ValidateTemplate`).
- Async methods do **not** get an `Async` suffix at the service/repository boundary: `DesignGrpcService.SaveDesign`, `IDesignOfflineStore.SaveAsync` — the repo is split roughly 147 `…Async` vs 159 `Task`-returning methods without it. Keep the name of whatever interface you implement; do not add `Async` to new public service methods, but do keep it on `IDesignOfflineStore`-style storage contracts where the contract already declares it (`TryLoadAsync`, `ClearPendingAsync`).
- Local helpers are PascalCase too, including small private ones (`DedicatedSubnetName`, `AddNodeToGroup`, `GetOverlappedGroup` in `src/AzureDesignStudio/Components/DiagramPanel.razor.cs`).

**Variables:**
- Locals and parameters: camelCase (`armTemplate`, `overlappedGroup`, `maxThrottleRetries`).
- Private instance fields: `_camelCase` (`_designClient`, `_clientFactory`, `_offlineStore` in `src/AzureDesignStudio/Services/DesignGrpcService.cs`; `_http`, `_credential`, `_gate` in `src/AzureDesignStudio.Discovery/ResourceGraph/ResourceGraphClient.cs`).
- `private static readonly` lookup tables are PascalCase and usually built with collection expressions: `InternetPrefixes`, `AdminPorts` in `src/AzureDesignStudio.Discovery/Exposure/ExposureAnalyzer.cs`, `OwnershipTagKeys` in `src/AzureDesignStudio.Discovery/Unmanaged/UnmanagedDetector.cs`.
- Legacy UI code-behind uses bare camelCase fields (`drawerRef`, `openedDrawer`, `diagram` in `src/AzureDesignStudio/Components/TopMenu.razor.cs` and `DiagramPanel.razor.cs`). Do not rename these; do use `_camelCase` in new C#.
- Razor-injected services: `@inject` field name `_camelCase` in newer components (`@inject MessageService _messageService`, `@inject AdsHostFeatures _features`), camelCase in older ones (`@inject AdsContext adsContext`, `src/AzureDesignStudio/Components/TopMenu.razor:3-12`). Prefer `_camelCase`.

**Types:**
- Interfaces: `I` prefix (`IAzureNode`, `IAzureResource`, `IDesignOfflineStore`, `IAdsBicepDecompiler`).
- Product-specific cross-cutting types take the `Ads` prefix: `AdsContext`, `AdsHostAuth`, `AdsHostFeatures`, `AdsDiagram`, `AdsConstants`, `AdsToolEngine`.
- Result/immutable payloads are `public sealed record` with positional parameters: `public sealed record ArmDiagnostic(ArmDiagnosticSeverity Severity, string Code, string Message);` (`src/AzureDesignStudio.Discovery/Iac/ArmTemplateValidator.cs`), `NameCheckResult`, `StagedRunResult`, `AnalysisFinding`.
- Outcome enums are closed sets with XML docs on members: `ArmDiagnosticSeverity { Error, Warning }` (`ArmTemplateValidator.cs`).
- Type-key constants live in `src/AzureDesignStudio.Core/Common/AdsConstants.cs` and are kebab-case strings (`"virtual-network"`, `"storage-account"`). Never inline the string literal.

## Code Style

**Formatting:**
- There is **no** `.editorconfig`, no StyleCop/Roslynator, and no `TreatWarningsAsErrors` anywhere. Style is enforced by review against `AGENTS.md`, not by tooling.
- Indentation is 4 spaces; braces on new lines (Allman) in C#; 2 spaces in `.razor` markup.
- File-scoped namespaces are the direction of travel: `src/AzureDesignStudio.Discovery/**` is 64/64 file-scoped (`namespace AzureDesignStudio.Discovery.Iac;`), tests are 51 file-scoped vs 11 block-scoped, legacy `Core`/UI code is mostly block-scoped. **Use file-scoped namespaces in new files.**
- No `global using` declarations in source (only compiler output). Add ordinary `using` lines per file.
- UTF-8 with BOM is common in existing files; match the file you edit.

**Nullability:**
- `<Nullable>enable</Nullable>` in every project except `src/AzureDesignStudio.AzureResources/AzureDesignStudio.AzureResources.csproj` (generated ARM models, nullable-oblivious — properties like `public IList<string> AddressPrefixes { get; set; }` in `src/AzureDesignStudio.AzureResources/Network/AddressSpace.cs` have no annotations).
- Empty collections/strings are preferred over `null` for model defaults: `public string Name { get; set; } = string.Empty;`, `public string ImagePath { get; set; } = string.Empty;` (`src/AzureDesignStudio.Core/Models/AzureNodeBase.cs`).
- Null-forgiving `!` is used at initialization boundaries (DI-assigned fields, deserialized data) and is intentionally frequent in UI code — over 400 occurrences in `src/AzureDesignStudio.Core`. Do not add `!` to silence a genuinely nullable value; make the type nullable (`string?`) instead.
- Prefer pattern matching `is null` / `is { } x` over `== null`: `if (stencil is null)`, `if (features.TenantScanLabel is { } label && features.OpenTenantScan is { } openScan)` (`src/AzureDesignStudio/Components/TopMenu.razor.cs`). Only 4 `== null` remain (legacy).

**Async:**
- All event handlers and service entry points return `Task`/`Task<T>`. There are **zero** `async void` methods in the repo; `async Task` appears 68 times in `.razor` files.
- Bind handlers directly: `@onclick="Close"`, `@onclick="CycleAsync"`, `@onclick="() => RunAsync(command)"`.
- When nothing is awaited, return `Task.CompletedTask` rather than using `async` for the sake of it: `TopMenu.CloseDrawer` (`src/AzureDesignStudio/Components/TopMenu.razor.cs`).
- `ConfigureAwait(false)` is used **only** in the library layer `src/AzureDesignStudio.Discovery/**` (10 files, e.g. `ArmTransport.cs`, `ResourceGraphClient.cs`). UI/Blazor code does not use it.
- Use exception filters for transport semantics instead of broad catches: `catch (Exception e) when (IsDeferredSaveFailure(e))` (`src/AzureDesignStudio/Services/DesignGrpcService.cs`).

**Modern C# idioms in use:**
- Target-typed `new` (221 uses): `private readonly SemaphoreSlim _gate = new(1, 1);`.
- Collection expressions (33 in `Core`+`Discovery`, pervasive in tests): `public static readonly string[] AdminPorts = ["22", "3389"];`, `return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];` (`src/AzureDesignStudio.Core.Tests/StencilTests.cs`).
- Primary constructors for dependency-only services: `public sealed class NameAvailabilityClient(ArmTransport transport)` (`src/AzureDesignStudio.Discovery/Naming/NameAvailabilityClient.cs`), `ArmTransport`, `WhatIfTokenGate`.
- Switch expressions for factories: `CreateNodeModelFromKey` in `src/AzureDesignStudio.Core/DataModelFactory.cs`, `GetAnchorParentId` in `AzureNodeProfile.cs`.
- `[GeneratedRegex]` + `static partial class` for regex-heavy validators (`ArmTemplateValidator.cs`).

## Import Organization

**Order:**
1. There is no enforced order. Existing files vary (newer `Discovery` files put `System.*` first).
2. For new files, follow `src/AzureDesignStudio.Discovery/Iac/ArmTemplateValidator.cs`: `System.*`, then `Microsoft.*`, then third-party (`Blazor.*`, `AutoMapper`, `Xunit`), then `AzureDesignStudio.*`.

**Path Aliases:**
- None. Full namespaces are used (`AzureDesignStudio.Services.AdsDiagram` is spelled out in `src/AzureDesignStudio/Components/DesignInspector.razor` to disambiguate from the Blazor.Diagrams type).
- The test project does **not** enable `ImplicitUsings` (`src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:4-7`); every test file must list `using System;`, `using System.Linq;`, `using Xunit;` explicitly.

## Error Handling

**Patterns:**
- Library engines do not throw for expected failures. They return the failure inside a result record: `IReadOnlyList<ArmDiagnostic> Validate(string templateJson)` (`src/AzureDesignStudio.Discovery/Iac/ArmTemplateValidator.cs`), `NameCheckResult(NameCheckOutcome Outcome, string? Detail)` (`src/AzureDesignStudio.Discovery/Naming/NameAvailabilityClient.cs`), `DeployScopePlan(Batches, Diagnostics)`, `StagedRunResult(..., string? Detail)`.
- Stable diagnostic codes are part of the contract; tests switch on them (`"json"`, `"content_version"`). Never reword an existing code.
- Throw only for programming/invariant/configuration errors: `throw new InvalidOperationException("KeyVaultName is not configured…")` in `src/AzureDesignStudio.Server/Program.cs`; `throw new Exception($"{Name} is not valid.")` in `AzureNodeBase.GetArmResources()`. A dedicated typed exception exists where callers must branch: `WhatIfException(string code, string message)` (`src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfException`).
- Catch the narrowest type and convert: `catch (JsonException e) { return [new ArmDiagnostic(..., "json", $"Template is not valid JSON: {e.Message}")]; }`; cleanup catches `catch (IOException) { }` in test `Dispose`.
- Fallbacks are explicit, never silent: unknown stencil keys resolve to `UnknownNodeModel` and the UI says so (`messageService.Info($"'{stencil.Name}' has no editor implementation in this version.")` in `DiagramPanel.razor.cs`).
- Offline degradation is a first-class path: `DesignGrpcService` treats `StatusCode.Unavailable`/`HttpRequestException` and `Unauthenticated` as deferred saves, stores them via `IDesignOfflineStore`, and reports `LastCallWasOffline`.

## Logging

**Framework:** `Microsoft.Extensions.Logging` (`ILogger<T>`) in the hosts; `Console.Error` only in the MCP stdio process; desktop crash log file.

**Patterns:**
- Inject `ILogger<T>` — `@inject ILogger<TopMenu> logger` (`src/AzureDesignStudio/Components/TopMenu.razor:5`), constructor injection in server services. 19 `ILogger<…>` usages repo-wide.
- **MCP server rule:** stdout is the protocol channel. Every diagnostic goes to stderr, and the console logger is rebound: `builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);` and `Console.Error.WriteLine(...)` (`src/AzureDesignStudio.Mcp/Program.cs:8-25`). Never `Console.WriteLine` in that project.
- Desktop host writes unhandled/unobserved exceptions to `%TEMP%\ads_desktop_crash.log` (`src/AzureDesignStudio.Desktop/MauiProgram.cs`). Do not add ad-hoc file logging elsewhere.
- User-facing messages go through AntDesign `MessageService`, not the logger (`await messageService.Error(...)`, 14 call sites).

## Comments

**When to Comment:**
- Explain *why*, especially workarounds, with the upstream reference: the BL0005 workaround in `TopMenu.razor.cs` names ant-design-blazor issue 2159 and wraps the assignment in `#pragma warning disable BL0005`.
- Comment non-obvious invariants at the point of enforcement: `// the node should be added to the group which has the smallest size.` (`DiagramPanel.razor.cs`), `// ARM child-resource nesting` (`StencilExportGoldenTests.cs`).
- Comment protocol/serialization quirks where they bite: `// Round-trips with AzureNodeDto.ArmId by AutoMapper name convention.` (`AzureNodeBase.cs`).

**XML docs:**
- `<summary>` on every public engine/client class and on most test classes. Test summaries name the product slice they defend, e.g. `/// <summary>v2 plan 10.4 online half: …</summary>` (`NameAvailabilityClientTests.cs`), `/// [93] local MCP server engine: read-only, deterministic, and honest …` (`AdsToolEngineTests.cs`).
- `<param>`/`<returns>` on non-obvious helpers (`DiagramPanel.AddNodeToGroup`).
- `#region` is used to group interface implementations (`AzureNodeBase.cs`) — 41 regions repo-wide; use sparingly.

## Function Design

**Size:** Small, single-purpose helpers; long orchestration methods are accepted where they map to one user action (`TopMenu.OpenDrawer<TDrawerTemplate>`), because drawer flows are linear UI sequences.

**Parameters:** Named arguments for boolean/callback knobs with defaults: `OpenDrawer<TDrawerTemplate>(string title, string options, bool bodyNoPadding = false, int width = 350)`; `NameAvailabilityClient` uses an injectable clock/delay delegate for testability (`WhatIfTokenGate(TimeSpan ttl, Func<DateTimeOffset>? clock = null)`).

**Return Values:** Prefer result records and tuples over out-params: `(bool result, string message) IsDrappable(...)`, `(IReadOnlyList<string> ids, IReadOnlyList<ArmDiagnostic> diags) PlannedManagedResourceIds(...)`.

**Statics:** Pure analysis lives in `static` methods (`ArmTemplateValidator.Validate`, `LinkTypeClassifier.Classify`, `AdsToolEngine.InventoryQuery`) so tests can call them without a container.

## Module Design

**Exports:** One primary public type per file; helper types may share the file when they are private to it (`DiagramHistory`'s nested `Command` types, `AzureNodeProfile`'s static anchor helpers). Extension-method classes are named `<Area>Extensions` (`src/AzureDesignStudio/Services/IServiceCollectionExtensions.cs`).

**Barrel Files:** None. Import concrete namespaces.

**Composition seams:**
- `DataModelFactory` (`src/AzureDesignStudio.Core/DataModelFactory.cs`) is the single registry mapping stencil key → model type and the diagram link factory. Add a resource by extending the `switch` in `CreateNodeModelFromKey` and (for group-rendered types) `RegisterAzureModels`.
- Host overrides are nullable delegates/properties registered as singletons with defaults, replaced by the desktop host: `AdsHostAuth { Func<Task>? SignIn; Func<Task>? SignOut; }` (`src/AzureDesignStudio/Services/AdsHostAuth.cs`), `AdsContext.StencilLoader`, `AdsHostFeatures`. WASM registers the default at `src/AzureDesignStudio/Program.cs:75,77`; MAUI rebinds them in `MauiProgram`.
- DI registration via `IServiceCollection` extension methods when a grouping is more than a single `Add*` call: `AddAdsBicepDecompiler()` (`IServiceCollectionExtensions.cs:16`).

## Project-Specific Conventions

### AntDesign 1.x (version 1.6.2)

Use enum values, never raw strings, and always pick the size explicitly:

```razor
<Form Model="@subnet" LabelCol="@(new ColLayoutParam { Span = 7 })"
      WrapperCol="@(new ColLayoutParam { Span = 17 })" Size="@FormSize.Small">
    <FormItem Label="Name">
        <Input Placeholder="subnet name" TValue="string" Size="@InputSize.Small" @bind-Value="@subnet.Name" />
    </FormItem>
    <FormItem WrapperCol="@(new ColLayoutParam { Span = 24 })">
        <Alert Type="@AlertType.Info" ShowIcon Message="…" />
    </FormItem>
</Form>
```

(`src/AzureDesignStudio.Core/Network/SubnetForm.razor`)

- `Size.Small` (478 uses), `ButtonType.Primary`, `DrawerPlacement.Bottom`, `Placement.TopRight`, `NotificationType.Info` — the enum member, never `"primary"`.
- `MessageService` methods are awaited: `await messageService.Error(...)`, `Info`, `Warn`, `Success`, `Loading`.
- Drawers are opened through `DrawerService` and returned as `DrawerRef<T>`; the content is a `FeedbackComponent<T1,T2>` (`src/AzureDesignStudio/Components/NodeDrawerTemplate.razor`, `src/AzureDesignStudio/Components/MenuDrawer/*.razor.cs`).

### AutoMapper + generated DTOs

Model → DTO mapping is generated, not hand-written:

```csharp
[MapToDto(TypeKey = AdsConstants.VirtualNetwork)]
public class VirtualNetworkModel : AzureGroupBase
{
    [MapToDto]
    public List<AddressSpace> IpSpace { get; set; } = new() { new AddressSpace { AddressPrefix = "10.0.0.0/16" } };
}
```

(`src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs`)

- `MapToDtoAttribute` is `internal` (`src/AzureDesignStudio.Core/Attributes/MapToDtoAttribute.cs`).
- `AzureNodeProfile` is `partial` and declares `private partial void CreateAzureNodeMaps();`; the implementation is emitted by the Scriban generator (`src/AzureDesignStudio.SourceGeneration/Templates/AzureNodeProfile.scriban`). Never write per-model `CreateMap` calls by hand — annotate the model.
- Register with `builder.Services.AddAutoMapper(typeof(AzureNodeProfile));` (`src/AzureDesignStudio/Program.cs:78`). Tests construct `new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper()` (`LinkTypeTests.cs`).
- Saving/loading goes through `DataModelFactory.SaveDiagramToDto` / `LoadDiagramFromDto` with an `IMapper` argument; `DiagramGraph` is the persisted root.

### gRPC client factory

Never construct a `*Client` over a manually built `GrpcChannel`. Register a named client and resolve it through `GrpcClientFactory`:

```csharp
builder.Services.AddGrpcClient<Design.DesignClient>("DesignClientWithAuth", o =>
{
    o.Address = new Uri(builder.HostEnvironment.BaseAddress);
}).ConfigurePrimaryHttpMessageHandler(() => new GrpcWebHandler(GrpcWebMode.GrpcWeb, baseAddressMessageHandler));
```

(`src/AzureDesignStudio/Program.cs:43-51`; desktop equivalents in `src/AzureDesignStudio.Desktop/MauiProgram.cs:63-70` with `MsalAuthTokenHandler`.)

Client names are the contract: `"DesignClientWithAuth"`, `"DeployClientWithAuth"`, `"CostClient"`. Wrapper services inject the factory and cache the client:

```csharp
public DesignGrpcService(GrpcClientFactory grpcClientFactory, IEnumerable<IDesignOfflineStore> offlineStores)
{
    _clientFactory = grpcClientFactory;
    _designClient = _clientFactory.CreateClient<Design.DesignClient>("DesignClientWithAuth");
    _offlineStore = offlineStores.FirstOrDefault();
}
```

(`src/AzureDesignStudio/Services/DesignGrpcService.cs`)

### Generated ARM models

- Every type under `src/AzureDesignStudio.AzureResources/**` carries `[GeneratedCode("ArmTypeGenerator", "0.2.1.52938")]` and `[JsonPropertyName("…")]`. Treat them as generated: do not hand-edit; fix the generator/model and regenerate.
- They are `public partial class` with settable properties and no nullable annotations; never "clean up" annotations there.

### Razor components

- Markup first, `@code` block last (79 of 82 `.razor` files use an inline `@code` block).
- Parameters at the top of `@code`, often one line each: `[Parameter] public bool Collapsed { get; set; }` (`src/AzureDesignStudio/Components/DesignInspector.razor:90`).
- Resource editor forms receive their node via cascading parameter and cast to the model — the form never owns state:

```razor
@code {
    [CascadingParameter]
    public NodeModel Node { get; set; } = null!;

    private SubnetModel subnet => (SubnetModel)Node;
}
```

(`src/AzureDesignStudio.Core/Network/SubnetForm.razor`)

- Split to a `.razor.cs` partial (`public partial class TopMenu`, `public partial class DiagramPanel : IDisposable`) when the component owns lifecycle, JS interop, or >100 lines of logic: `src/AzureDesignStudio/Components/TopMenu.razor.cs`, `DiagramPanel.razor.cs`, `src/AzureDesignStudio/Components/MenuDrawer/*.razor.cs` (8 such files).
- Per-component CSS isolation via `<Component>.razor.css` (`DiagramPanel.razor.css`, `Stencil.razor.css`).
- JS interop: `@inject IJSRuntime JS`, `DotNetObjectReference<T>` kept in a field, disposed in `Dispose`; JS-invoked entry points are `[JSInvokable]` methods that return `Task` and wrap work in `InvokeAsync` + `StateHasChanged` (`DiagramPanel.HandleDropAtAsync`).
- Do not call `StateHasChanged()` after ordinary Blazor event handlers; use it only around JS-invoked or external-event work.

### JSON

- `System.Text.Json` only (no Newtonsoft). Read with `JsonDocument` inside `using` and `TryGetProperty`; write through the ARM model types.
- ARM templates are generated as strings by `ArmTemplate.GenerateArmTemplate()` and parsed only for assertions.
- Tests embed expected JSON as raw string literals, using `__TOKEN__` placeholders + `.Replace(...)` for long resource ids (`src/AzureDesignStudio.Core.Tests/ChangeFeedTests.cs`, `AdsToolEngineTests.cs`).

---

*Convention analysis: 2026-10-21*
