---
last_mapped_commit: a05ef8c00d2ff2dc2f0d9bea0a220051852405a8
last_mapped_at: 2026-10-21
---
# Architecture

**Analysis Date:** 2026-10-21

## System Overview

Azure Design Studio is a layered .NET 10 solution: one shared Blazor UI head (`AzureDesignStudio`) hosted by two shells (browser WASM and MAUI desktop), one gRPC server (`AzureDesignStudio.Server`), and a set of pure engine/persistence libraries consumed by all of them. The diagram canvas, domain models and ARM generation live in `AzureDesignStudio.Core`; host-specific capabilities are injected through explicit override seams rather than compiled-in branches.

```text
┌────────────────────────────── HOSTS ───────────────────────────────┐
│  Browser (WASM)            Desktop (MAUI/WebView2)     MCP (stdio) │
│  `src/AzureDesignStudio`   `src/AzureDesignStudio.     `src/Azure-  │
│    Program.cs                Desktop/MauiProgram.cs`    DesignStudio│
│                              `MainPage.xaml` → ads:App  .Mcp/       │
│                                                          Program.cs`│
└───────────┬────────────────────────┬────────────────────────┬──────┘
            │ gRPC-Web / HTTP1.1     │ native gRPC / HTTP2    │ read-only
            │                        │                        │ SQLite
┌───────────▼────────────────────────▼───────────┐   ┌────────▼──────┐
│              SHARED UI + DOMAIN                │   │  Workspace    │
│ `src/AzureDesignStudio.Core` (Razor + models)  │   │  SQLite DB    │
│ `src/AzureDesignStudio/Components` (canvas)    │   │ `src/Azure-   │
│ `src/AzureDesignStudio/Services` (context,     │   │  DesignStudio │
│  gRPC wrappers, host seams)                    │   │  .Workspace`  │
└───────────┬──────────────────────┬─────────────┘   └────────▲──────┘
            │ protobuf contracts   │ ARM JSON                 │ scans
            │ `SharedModels/*.proto`│                          │
┌───────────▼──────────────────────┴─────────────┐   ┌────────┴──────┐
│         `AzureDesignStudio.Server`             │   │ Discovery     │
│  DesignService · DeployService · RetailPrice   │──►│ `src/Azure-   │
│  EF Core (InMemory dev / SQL Server prod)      │   │  DesignStudio │
└───────────┬────────────────────────────────────┘   │  .Discovery`  │
            │ Azure.ResourceManager / HttpClient     │ (pure engines)│
            ▼                                        └───────────────┘
   ARM · Resource Graph · Retail Prices API · Entra ID
```

## Component Responsibilities

| Component | Responsibility | File |
|-----------|----------------|------|
| WASM shell | DI composition, MSAL auth, gRPC-Web clients, static asset host | `src/AzureDesignStudio/Program.cs` |
| Desktop shell | MAUI BlazorWebView host, native MSAL/WAM, native gRPC, SQLite workspace, tenant scan | `src/AzureDesignStudio.Desktop/MauiProgram.cs` |
| Server | gRPC endpoints, JWT validation, EF Core persistence, ARM deployment | `src/AzureDesignStudio.Server/Program.cs` |
| Shared diagram state | Singleton canvas state, stencil catalog, DTO serialization | `src/AzureDesignStudio/Services/AdsContext.cs`, `src/AzureDesignStudio/Services/AdsDiagram.cs` |
| Shared canvas UI | Drag/drop, grouping, history, selection, drawers | `src/AzureDesignStudio/Components/DiagramPanel.razor.cs` |
| Domain models | Diagram nodes + ARM projection contract | `src/AzureDesignStudio.Core/Models/AzureNodeBase.cs`, `src/AzureDesignStudio.Core/Models/AzureGroupBase.cs` |
| ARM generation | Template assembly, JSON serialization, api-version table | `src/AzureDesignStudio.Core/Models/ArmTemplate.cs`, `src/AzureDesignStudio.Core/Common/AzureApiVersions.cs` |
| ARM type library | Raw ARM resource/DTO shapes (generated style) | `src/AzureDesignStudio.AzureResources/` |
| DTO code generation | Emits `*Dto`, `AzureNodeProfile`, `DataModelFactory` partials | `src/AzureDesignStudio.SourceGeneration/AzureNodeDtoGenerator.cs` |
| Contracts | Protobuf service + message definitions | `src/AzureDesignStudio.SharedModels/design_view_model.proto`, `deploy_model.proto`, `cost_model.proto` |
| Discovery engines | Read-only analysis over inventory snapshots (drift, exposure, cost, decommission, IaC) | `src/AzureDesignStudio.Discovery/` |
| Workspace persistence | SQLite EF Core store for designs/scans/snapshots/plans | `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs` |
| MCP server | 13 read-only tools over the workspace, stdio transport | `src/AzureDesignStudio.Mcp/Program.cs`, `src/AzureDesignStudio.Mcp/AdsMcpTools.cs` |

## Pattern Overview

**Overall:** Layered architecture with a shared presentation/domain core, a thin host layer per execution environment, and ports-and-adapters at the edges (gRPC server, SQLite workspace, ARM/ARG clients, MCP transport).

**Key Characteristics:**
- One UI assembly (`src/AzureDesignStudio`) is reused verbatim by the browser and by MAUI — `src/AzureDesignStudio.Desktop/MainPage.xaml` mounts `{x:Type ads:App}` from the WASM assembly inside a `BlazorWebView`.
- Every host difference is expressed as a registered override, never a conditional: `AdsContext.StencilLoader`, `AdsHostAuth`, `AdsHostFeatures`, `IDesignOfflineStore`, `IScanSink`.
- Domain libraries (`Core`, `Discovery`, `Workspace`, `AzureResources`) hold no host or transport code; `Discovery` is pure and deterministic, which is what lets both the desktop drawer and the MCP server consume it.
- Compile-time code generation replaces DTO/mapper boilerplate: attributes on models drive `DataModelFactory` partials.
- Server-side responsibilities are deliberately narrow: persist designs, talk to ARM, proxy retail prices. All analysis that can be offline lives in `Discovery`/`Core`.

## Layers

**Host layer:**
- Purpose: process entry, DI composition, platform auth, transport choice.
- Location: `src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio.Desktop/MauiProgram.cs`, `src/AzureDesignStudio.Server/Program.cs`, `src/AzureDesignStudio.Mcp/Program.cs`.
- Contains: `WebAssemblyHostBuilder`/`MauiApp`/`WebApplication`/`HostApplicationBuilder` setup, `launchSettings`, `appsettings.json`.
- Depends on: contracts + domain + (for Desktop) `Workspace`, (for Server) `Discovery`.
- Used by: end users / agents; nothing references the hosts except the solution.

**Shared presentation layer:**
- Purpose: render the workspace shell, canvas, drawers, palette; own UI state and command surface.
- Location: `src/AzureDesignStudio/Components/`, `src/AzureDesignStudio/Services/`, `src/AzureDesignStudio/Shared/`, `src/AzureDesignStudio/Pages/Index.razor`.
- Contains: `DiagramPanel.razor(.cs)`, `TopMenu.razor(.cs)`, `StencilPanel.razor`, `DesignInspector.razor`, `Components/MenuDrawer/*`, `AdsCommandService`.
- Depends on: `AzureDesignStudio.Core` (models/components/factory), `SharedModels` (gRPC clients).
- Used by: WASM host directly; Desktop host indirectly through the reused `App` component.

**Domain layer:**
- Purpose: resource models, diagram behavior, validation, export text formats, DTO graph round-trip.
- Location: `src/AzureDesignStudio.Core/Models/`, `src/AzureDesignStudio.Core/<Service>/`, `src/AzureDesignStudio.Core/Common/`, `src/AzureDesignStudio.Core/Validation/`, `src/AzureDesignStudio.Core/DTO/`.
- Contains: `AzureNodeBase`, `AzureGroupBase`, per-service `*Model.cs` + `*Form.razor`, `DataModelFactory` (partial), `DesignValidator`, `AdsDesignText`, `NamingEngine`, diff/merge engines.
- Depends on: `AzureResources` (ARM shapes), `SourceGeneration` (analyzer only), `Z.Blazor.Diagrams`, `AutoMapper`.
- Used by: WASM UI, `Workspace` (offline store contract), `Mcp`, tests.

**ARM generation layer:**
- Purpose: turn a diagram into a deployable ARM template and API-version-correct resource payloads.
- Location: `src/AzureDesignStudio.AzureResources/Base/`, `src/AzureDesignStudio.AzureResources/<Service>/`, `src/AzureDesignStudio.Core/Models/ArmTemplate.cs`.
- Contains: `ARMResourceBase` → `ResourceBase` hierarchy, per-service resource classes (e.g. `Storage/StorageAccounts.cs`), `DeploymentTemplate`, `Parameter`, `AzureResourceAttribute`.
- Depends on: nothing (leaf library).
- Used by: every `IAzureResource.GetArmResources()` implementation; `AzureApiVersions` reflects over the `[AzureResource]` attributes.

**Contracts layer:**
- Purpose: versioned protobuf surface between clients and server.
- Location: `src/AzureDesignStudio.SharedModels/*.proto`.
- Contains: `Design` (4 RPCs), `Deploy` (deploy/what-if/stack/decommission), `Cost` (retail prices).
- Depends on: `Grpc.Tools` codegen only.
- Used by: `DesignGrpcService`/`DeployGrpcService`/`CostGrpcService` (client), `DesignService`/`DeployService`/`RetailPriceService` (server).

**Persistence layer:**
- Purpose: durable designs (server) and offline workspace (client).
- Location: `src/AzureDesignStudio.Server/Models/DesignDbContext.cs`, `src/AzureDesignStudio.Workspace/`.
- Contains: `AdsDesigns`, `AzureSubscriptions`, approval/audit/guardrail tables; `Designs`, `Scans`, `ResourceSnapshots`, `CostCache`, `ExecutionPlans`, `AuditRecords`, `NodePositions`, `ChangeFeed`, `DecomWindows`.
- Depends on: EF Core InMemory/SqlServer (server), EF Core SQLite (workspace), `Core` (workspace).
- Used by: `DesignService`/`DeployService`; Desktop host and `Mcp`.

**Discovery layer:**
- Purpose: deterministic, network-optional analysis engines over inventory snapshots.
- Location: `src/AzureDesignStudio.Discovery/`.
- Contains: `Arm/` (transport + tenant inventory), `ResourceGraph/`, `Scan/ScanEngine.cs`, `Drift/`, `Exposure/`, `Cost/`, `Decommission/`, `Iac/`, `Security/`, `Unmanaged/`, `Naming/`, `Preflight/`, `Report/`, `Analysis/`, `Mcp/AdsToolEngine.cs`.
- Depends on: `Azure.Identity` package only — no project references.
- Used by: Desktop (`MauiProgram.cs` registrations), Server (`DeployService`), MCP (`AdsMcpTools`), tests.

## Data Flow

### Primary Request Path — creating a design on the canvas

1. Palette renders stencils loaded into `AdsContext.AllStencils` (`src/AzureDesignStudio/Components/StencilPanel.razor`; source is `ads-stencils.json`).
2. Drop lands on the canvas; `DiagramPanel` instantiates the node via `DataModelFactory.CreateNodeModelFromKey` (`src/AzureDesignStudio/Components/DiagramPanel.razor.cs:147`, `src/AzureDesignStudio.Core/DataModelFactory.cs:52`).
3. Dragging a node over a group runs `IAzureNode.IsDrappable` and records membership through `DiagramHistory` (`src/AzureDesignStudio/Components/DiagramPanel.razor.cs:74`).
4. Selection opens the type's `DataFormType` form inside `NodeDrawerTemplate`; property edits mutate the model's backing `ResourceBase` instance.

### Save (server) — AdsContext → DTO → gRPC → EF Core

1. `TopMenu` serializes the canvas: `DataModelFactory.SaveDiagramToDto(adsContext.Diagram, mapper)` (`src/AzureDesignStudio.Core/DataModelFactory.cs`; call site `src/AzureDesignStudio/Components/TopMenu.razor.cs:344`).
2. Each node's `GetNodeDto(mapper)` returns its generated `*Dto : AzureNodeDto` (`src/AzureDesignStudio.Core/Storage/StorageAccountModel.cs`); `DiagramGraph` holds groups, nodes, links (`src/AzureDesignStudio.Core/DTO/DiagramGraph.cs`).
3. `DesignGrpcService.SaveDesign(name, data)` calls `Design.Save` through `GrpcClientFactory` (`src/AzureDesignStudio/Services/DesignGrpcService.cs`).
4. WASM transport is gRPC-Web over `BaseAddressAuthorizationMessageHandler`; Desktop uses native HTTP/2 with `MsalAuthTokenHandler` (`src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio.Desktop/MauiProgram.cs`).
5. `DesignService.Save` validates name/scope, enforces the 10-designs-per-user quota, upserts `AdsDesigns` (`src/AzureDesignStudio.Server/Services/DesignService.cs`; schema `src/AzureDesignStudio.Server/Models/DesignDbContext.cs`).

Offline branch: if no server is reachable (or the caller is unauthenticated), `DesignGrpcService` writes to `IDesignOfflineStore` and reports `LastCallWasOffline`; pending rows are flushed on the next successful call. WASM registers no store; Desktop registers `SqliteDesignOfflineStore` (`src/AzureDesignStudio.Workspace/SqliteDesignOfflineStore.cs`).

### Load

1. `DesignGrpcService.LoadDesign` returns the stored JSON.
2. `TopMenu` parses it into `DiagramGraph` and replays it into the live diagram with history suspended: `DataModelFactory.LoadDiagramFromDto(...)` (`src/AzureDesignStudio/Components/TopMenu.razor.cs:506`).
3. `GetNodeModelFromDto` (generated partial, see `src/AzureDesignStudio.SourceGeneration/Templates/DataModelFactory.scriban`) dispatches on `TypeKey` and maps the DTO back to the concrete model; unknown keys become `UnknownNodeModel`.

### Export — ARM / Bicep / .ads.json

1. `TopMenu.GetArmJson` walks canvas `IAzureResource` nodes, calls `GetArmResources()` / `GetArmParameters()`, assembles `ArmTemplate` (`src/AzureDesignStudio.Core/Models/ArmTemplate.cs`).
2. `GenerateArmTemplate()` serializes with `ResourceBaseJsonConverter` and web defaults.
3. Bicep is produced by decompiling that ARM JSON (`src/AzureDesignStudio/Services/AdsBicepDecompiler.cs`, registered via `AddAdsBicepDecompiler()` in `src/AzureDesignStudio/Services/IServiceCollectionExtensions.cs`).
4. `.ads.json` uses the canonical, byte-stable writer `AdsDesignText.Write` (`src/AzureDesignStudio.Core/Common/AdsDesignText.cs`).

### Deploy

1. `DeployGrpcService` sends template + parameters (+ `DeployTarget`) to `Deploy.DeployClient` (`src/AzureDesignStudio/Services/DeployGrpcService.cs`).
2. `DeployService` runs what-if through the gated `WhatIfTokenGate`, requires a matching plan approval, then deploys with `Azure.ResourceManager` (`src/AzureDesignStudio.Server/Services/DeployService.cs`).
3. Subscription credentials are stored encrypted by `CryptoService` (`src/AzureDesignStudio.Server/Services/CryptoService.cs`).

### Tenant discovery → workspace → MCP

1. Desktop `ScanEngine` collects via `ArmTransport`/`ResourceGraphClient` using `AdsTokenBroker` as `TokenCredential` (`src/AzureDesignStudio.Discovery/Scan/ScanEngine.cs`, `src/AzureDesignStudio.Desktop/MauiProgram.cs:78-102`).
2. Progress/checkpoints are persisted through `WorkspaceScanSink` into `WorkspaceDbContext` (`src/AzureDesignStudio.Desktop/WorkspaceScanSink.cs`, `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs`).
3. `AdsMcpTools` reads those rows through `AdsWorkspace` (read-only SQLite) and hands snapshots to the pure `AdsToolEngine` (`src/AzureDesignStudio.Mcp/AdsWorkspace.cs`, `src/AzureDesignStudio.Discovery/Mcp/AdsToolEngine.cs`).

**State Management:**
- One `AdsContext` singleton per host process owns the diagram, stencil catalog and current design name (`src/AzureDesignStudio/Services/AdsContext.cs`).
- Undo/redo is an explicit command stack (`DiagramHistory`) attached to `AdsDiagram`, not Blazor component state (`src/AzureDesignStudio/Services/AdsDiagram.cs`, `src/AzureDesignStudio.Core/Common/DiagramHistory.cs`).
- Shell commands are registered by key in `AdsCommandService` so the palette, inspector and menu share one surface (`src/AzureDesignStudio/Services/AdsCommandService.cs`).

## Key Abstractions

**`AdsContext`:**
- Purpose: process-wide canvas state and stencil catalog.
- Examples: `src/AzureDesignStudio/Services/AdsContext.cs`
- Pattern: singleton service constructed once in DI; `InitializeAsync` awaited before first render in both hosts (`Program.cs`, `App.xaml.cs`).

**`AzureNodeBase` / `AzureGroupBase`:**
- Purpose: a diagram model that is simultaneously an `IAzureNode` (UI concerns) and an `IAzureResource` (ARM concerns).
- Examples: `src/AzureDesignStudio.Core/Models/AzureNodeBase.cs`, `src/AzureDesignStudio.Core/Models/AzureGroupBase.cs`, `src/AzureDesignStudio.Core/Storage/StorageAccountModel.cs`
- Pattern: template method — subclasses override `ArmResource`, `ServiceName`, `DataFormType`, and `PopulateArmAttributes`; base implements `ResourceId` and `GetArmResources()`.

**`ARMResourceBase` → `ResourceBase`:**
- Purpose: mirror the ARM schema so serialization is shape-correct.
- Examples: `src/AzureDesignStudio.AzureResources/Base/ARMResourceBase.cs` (name/type/apiVersion/dependsOn/condition), `src/AzureDesignStudio.AzureResources/Base/ResourceBase.cs` (location/tags/copy/scope/comments), `src/AzureDesignStudio.AzureResources/Storage/StorageAccounts.cs`
- Pattern: inheritance + `virtual` `Type`/`ApiVersion` overrides per resource class, `[JsonPropertyOrder]` for stable output.

**`DataModelFactory`:**
- Purpose: the single dispatch point between stencil keys, concrete models, and DTO types.
- Examples: `src/AzureDesignStudio.Core/DataModelFactory.cs` (hand-written switch + type map) and generated partials from `src/AzureDesignStudio.SourceGeneration/Templates/DataModelFactory.scriban`.
- Pattern: static partial class; hand-written half maps keys→instances, generated half maps keys→DTO types and DTOs→instances.

**Host override seams:**
- Purpose: let one shared UI run on two very different auth/transport stacks without `#if`.
- Examples: `src/AzureDesignStudio/Services/AdsHostAuth.cs`, `src/AzureDesignStudio/Services/AdsHostFeatures.cs`, `AdsContext.StencilLoader`.
- Pattern: plain POCO of optional delegates; null means "feature absent" and the shared UI hides the affordance (WASM registers the empty defaults in `Program.cs`).

**`IDesignOfflineStore`:**
- Purpose: pending-design contract behind `DesignGrpcService`.
- Examples: contract `src/AzureDesignStudio.Core/Common/IDesignOfflineStore.cs`; implementation `src/AzureDesignStudio.Workspace/SqliteDesignOfflineStore.cs`.
- Pattern: interface in `Core`, implementation in `Workspace`, registration only in the Desktop host.

**`AdsToolEngine` / `IScanSink`:**
- Purpose: keep analysis pure and host-agnostic.
- Examples: `src/AzureDesignStudio.Discovery/Mcp/AdsToolEngine.cs`, `src/AzureDesignStudio.Discovery/Scan/ScanEngine.cs`.
- Pattern: engines take snapshots in and return records; the host supplies side effects through `IScanSink`.

## Entry Points

**WASM client:**
- Location: `src/AzureDesignStudio/Program.cs`
- Triggers: browser load of `wwwroot/index.html` (`#app` root component).
- Responsibilities: HTTP clients, MSAL (`AddMsalAuthentication`), gRPC-Web client registration, AntDesign, AutoMapper, `AdsContext` init before `RunAsync()`.

**MAUI desktop:**
- Location: `src/AzureDesignStudio.Desktop/MauiProgram.cs`
- Triggers: `App.CreateWindow` → `MainPage` → `BlazorWebView` root component `ads:App`.
- Responsibilities: native MSAL/WAM (`MsalAccountService`, `AdsTokenBroker`), native gRPC clients, SQLite workspace + legacy import, scan engine/sinks, `AdsHostAuth`/`AdsHostFeatures` overrides, file logging.

**Server:**
- Location: `src/AzureDesignStudio.Server/Program.cs`
- Triggers: `dotnet run`/container entrypoint; also serves the WASM static site in production.
- Responsibilities: JWT bearer (`Microsoft.Identity.Web`), EF Core selection (InMemory in Debug, Key Vault + SQL Server otherwise), gRPC-Web mapping, loopback h2c listener on 5204 for Desktop, schema bootstrap.

**MCP server:**
- Location: `src/AzureDesignStudio.Mcp/Program.cs`
- Triggers: stdio launch with `--db <path>` or `ADS_WORKSPACE_DB`.
- Responsibilities: host stdio transport, log to stderr only, expose `AdsMcpTools` over a read-only workspace.

## Architectural Constraints

- **Threading:** Blazor UI code runs on the circuit's single synchronization context; long work is offloaded (`Task.Run` inside `SaveDiagramToDto`) or made awaitable (`ScanControl.PauseAsync` awaits the gate rather than blocking). Diagram mutation must happen on the UI thread.
- **Global state:** `MauiProgram.Services` is a static `IServiceProvider` read by `App.CreateWindow`; `DataModelFactory` keeps static type/key tables; `AzureApiVersions.Map` is a thread-safe `Lazy` reflection cache. Treat all three as process-wide.
- **Import direction:** `AzureResources` and `SourceGeneration` are leaves; `Core` depends on both; `Workspace` depends on `Core`; `Discovery` depends on no project; hosts depend downward only. There are no circular project references in `src/AzureDesignStudio.sln`.
- **Transport split:** browser clients must use `GrpcWebHandler` over HTTP/1.1; Desktop uses native HTTP/2 against `http://localhost:5204`. The `GrpcClientFactory` client names (`"DesignClientWithAuth"`, `"DeployClientWithAuth"`, `"CostClient"`) are the contract between hosts and the shared wrappers.
- **Trimming/AOT:** the WASM project publishes trimmed with `RunAOTCompilation` in Release and explicit `TrimmerRootAssembly` entries (`src/AzureDesignStudio/AzureDesignStudio.csproj`). New reflection-based code paths must be rooted or avoided.
- **No `Directory.Build.props`:** every project declares its own TFM and packages; versioning is central only through `version.json` (Nerdbank.GitVersioning).
- **Nullable:** enabled everywhere except `src/AzureDesignStudio.AzureResources` (generated-style code predates it).

## Anti-Patterns

### Constructing gRPC clients directly

**What happens:** `new Design.DesignClient(channel)` bypasses the registered factory client.
**Why it's wrong:** it skips the auth message handler (`BaseAddressAuthorizationMessageHandler` in WASM, `MsalAuthTokenHandler` in Desktop) and the transport-specific primary handler, producing silent 401s or protocol failures.
**Do this instead:** inject `GrpcClientFactory` and call `CreateClient<T>("DesignClientWithAuth")`, exactly as `src/AzureDesignStudio/Services/DesignGrpcService.cs` does.

### Branching shared UI on `#if` or host type

**What happens:** a drawer checks whether it is running in desktop and calls desktop-only services.
**Why it's wrong:** the whole point of `AdsHostAuth`/`AdsHostFeatures` is that `AzureDesignStudio` compiles once for both hosts; host references would invert the dependency and break the browser build.
**Do this instead:** add a nullable delegate to `src/AzureDesignStudio/Services/AdsHostFeatures.cs` and register it in `src/AzureDesignStudio.Desktop/MauiProgram.cs`.

### Hand-maintained API version or mapping tables

**What happens:** a new api-version string or DTO mapping is typed into a switch/table by hand.
**Why it's wrong:** `AzureApiVersions` derives versions from `[AzureResource]` attributes (`src/AzureDesignStudio.Core/Common/AzureApiVersions.cs`) and the source generator derives DTO/mapping partials; a second source of truth drifts.
**Do this instead:** annotate the ARM model with `[AzureResource(...)]` and the diagram model with `[MapToDto(TypeKey = AdsConstants.X)]`, then rebuild.

### `async void` in Blazor event handlers

**What happens:** `async void` on `@onclick`/lifecycle code.
**Why it's wrong:** exceptions escape the renderer and crash the circuit; callers cannot await completion.
**Do this instead:** return `Task` from the handler (the pattern used throughout `src/AzureDesignStudio/Components/TopMenu.razor.cs`).

### Mutating diagram state outside the history stack

**What happens:** adding/removing nodes or groups without `DiagramHistory` recording (or inside a `Suspend` scope that is never matched).
**Why it's wrong:** undo/redo and dirty-state tracking silently diverge from the canvas.
**Do this instead:** follow `DiagramPanel`/`TopMenu`: `History.Suspend(...)` only for whole-document loads, `RecordChildMembership`/gesture scopes for edits.

## Error Handling

**Strategy:** transport errors become explicit status codes or typed exceptions at the layer that can judge them; analysis layers refuse to invent certainty and surface "unknown" instead.

**Patterns:**
- gRPC responses carry `int32 status_code` rather than throwing; clients check it (e.g. `DesignGrpcService.SaveDesign`).
- Offline degradation is deliberate: `DesignGrpcService.IsDeferredSaveFailure` treats `Unavailable`/`Unauthenticated` as "keep it locally", never as data loss.
- Text formats throw localized exceptions: `AdsDesignFormatException` from `AdsDesignText.Read` (`src/AzureDesignStudio.Core/Common/AdsDesignText.cs`).
- Scans record per-group failures in `ScanCheckpoint.FailedQueries` instead of aborting (`src/AzureDesignStudio.Discovery/Scan/ScanEngine.cs`).
- MCP tools publish caveat strings (`AdsToolEngine.ExposureCaveats`, `NotEvaluatedCaveats`) so a missing check reads as "not evaluated", never as "clean".
- Startup schema drift is repaired idempotently in `src/AzureDesignStudio.Server/Program.cs` (EnsureCreated + guarded DDL) rather than assumed.

## Cross-Cutting Concerns

**Logging:** `Microsoft.Extensions.Logging` with host-specific sinks — Application Insights on WASM (`BlazorApplicationInsights`), file logger to `%TEMP%/ads_desktop_crash.log` on Desktop (`FileLoggerProvider` in `src/AzureDesignStudio.Desktop/MauiProgram.cs`), `ILogger` + Application Insights on Server, stderr-only console logging in MCP.

**Validation:** two levels — data annotations on model properties (`[Required]`, `[DisplayName]`) feed forms and ARM parameter generation; cross-node rules live in `DesignValidator` (`src/AzureDesignStudio.Core/Validation/DesignValidator.cs`) and return `ValidationIssue` records to the validation drawer.

**Authentication:** WASM uses MSAL redirect (`src/AzureDesignStudio/Shared/RedirectToLogin.razor`, `/authentication/{action}` route in `src/AzureDesignStudio/Pages/Index.razor`); Desktop uses native MSAL/WAM with device-code fallback (`src/AzureDesignStudio.Desktop/MsalAccountService.cs`) behind `AdsHostAuth`; Server validates JWT and enforces `[RequiredScope]` on `DesignService`/`DeployService`. Azure SDK calls in Desktop go through the single `AdsTokenBroker` chain (`src/AzureDesignStudio.Desktop/AdsTokenBroker.cs`).

---

*Architecture analysis: 2026-10-21*
