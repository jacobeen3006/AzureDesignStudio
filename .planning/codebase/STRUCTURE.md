---
last_mapped_commit: a05ef8c00d2ff2dc2f0d9bea0a220051852405a8
last_mapped_at: 2026-10-21
---
# Codebase Structure

**Analysis Date:** 2026-10-21

## Directory Layout

```text
AzureDesignStudio/                      # repo root
├── .github/
│   ├── codeql/codeql-config.yml        # CodeQL path filters
│   └── workflows/                      # build.yml, codeql-analysis.yml, publish.yml
├── assets/                             # README screenshots/GIFs (not shipped)
├── docs/                               # architecture.md, entra-setup.md, v1/v2/v3 plans
├── src/
│   ├── AzureDesignStudio.sln           # 11-project solution
│   ├── version.json                    # Nerdbank.GitVersioning config
│   ├── DockerBuild.ps1                 # nbgv-stamped container build
│   ├── GenerateVersioningTargets.ps1   # version targets for non-NBGV projects
│   ├── Configurations/                 # deployment.yaml, ingress.yaml, service.yaml, ads-stencils.json
│   ├── AzureDesignStudio/              # Blazor WASM shell + shared canvas UI (also reused by Desktop)
│   │   ├── Components/                 # DiagramPanel, TopMenu, StencilPanel, Inspector, MenuDrawer/*
│   │   ├── Models/                     # client-only view models (StencilModel, *DrawerContent)
│   │   ├── Pages/Index.razor           # single workspace page + /authentication/{action}
│   │   ├── Services/                   # AdsContext, AdsDiagram, gRPC wrappers, host seams
│   │   ├── Shared/                     # MainLayout, RedirectToLogin, UpdateAvailableDetector
│   │   └── wwwroot/                    # index.html, assets/azure/*.svg, css/, js/, appsettings.json
│   ├── AzureDesignStudio.Core/         # shared domain + Razor components + per-service models
│   │   ├── Components/                 # AzureNodeComponent, AzureGroupComponent, NameAndLocation
│   │   ├── Common/                     # engines/utilities (ARM helpers, diff, history, naming, text format)
│   │   ├── Cost/                       # CostEstimator, CostModels, CostSkuResolver
│   │   ├── DTO/                        # AzureNodeDto, AzureNodeProfile, DiagramGraph, LinkModelDto
│   │   ├── Models/                     # AzureNodeBase, AzureGroupBase, ArmTemplate, IAzureNode, IAzureResource
│   │   ├── Validation/                 # DesignValidator + ValidationIssue
│   │   ├── Attributes/                 # MapToDtoAttribute
│   │   ├── <Service>/                  # AKS, APIM, App, AppConfiguration, Cache, CognitiveServices,
│   │   │                               # Compute, ContainerInstance, ContainerRegistry, DataFactory,
│   │   │                               # DocumentDB, Fabric, Insights, KeyVault, Logic, Messaging,
│   │   │                               # Network, OperationalInsights, RecoveryServices, SQL, Search,
│   │   │                               # SignalRService, Storage, Web  → *Model.cs + *Form.razor
│   │   └── DataModelFactory.cs         # hand-written key→model switch + type map (partial with codegen)
│   ├── AzureDesignStudio.AzureResources/  # raw ARM type shapes
│   │   ├── Base/                       # ARMResourceBase, ResourceBase, DeploymentTemplate, Parameter, ...
│   │   └── <Service>/                  # ApiManagement, App, AppConfiguration, Cache, CognitiveServices,
│   │                                   # Compute, ContainerInstance, ContainerRegistry, ContainerService,
│   │                                   # DataFactory, DocumentDB, Fabric, Insights, KeyVault, Logic,
│   │                                   # Messaging, Network, OperationalInsights, RecoveryServices,
│   │                                   # Search, SignalRService, Sql, Storage, Web
│   ├── AzureDesignStudio.SharedModels/ # design_view_model.proto, deploy_model.proto, cost_model.proto
│   ├── AzureDesignStudio.SourceGeneration/  # Roslyn incremental generator (netstandard2.0)
│   │   ├── Models/                     # DtoTypeModel, MapProfileModel
│   │   ├── Templates/                  # AzureNodeDto.scriban, AzureNodeProfile.scriban, DataModelFactory.scriban
│   │   ├── AzureNodeDtoGenerator.cs    # IIncrementalGenerator entry
│   │   ├── ResourceReader.cs           # loads embedded Scriban templates
│   │   └── RoslynExtensions.cs         # attribute/namespace helpers
│   ├── AzureDesignStudio.Server/       # ASP.NET Core gRPC + static WASM host
│   │   ├── Models/DesignDbContext.cs   # EF Core entities incl. design/plan/approval/audit tables
│   │   ├── Services/                   # DesignService, DeployService, RetailPriceService, CryptoService, ServiceTools
│   │   ├── Utils/AdsTelemetryInitializer.cs
│   │   ├── Pages/Error.cshtml.cs
│   │   └── Dockerfile                  # multi-stage .NET 10 image with wasm-tools
│   ├── AzureDesignStudio.Desktop/      # MAUI Blazor Hybrid shell (Windows)
│   │   ├── Components/                 # TenantScanDrawerTemplate, TopologyView
│   │   ├── Platforms/{Windows,Android,iOS,MacCatalyst}/
│   │   ├── Resources/                  # app icon, fonts, raw assets
│   │   ├── wwwroot/app.html            # host page for BlazorWebView
│   │   ├── MauiProgram.cs              # DI composition + host seams + workspace + scan
│   │   ├── MsalAccountService.cs       # native MSAL/WAM + device-code fallback
│   │   ├── AdsTokenBroker.cs           # single TokenCredential chain for Azure SDK
│   │   ├── MsalAuthTokenHandler.cs     # bearer token for gRPC clients
│   │   ├── DesktopAuthenticationStateProvider.cs
│   │   ├── WorkspaceScanSink.cs        # IScanSink → SQLite workspace
│   │   ├── TenantScanSession.cs, ChangeFeedService.cs
│   │   └── AppPackages/                # generated MSIX test package output (do not edit)
│   ├── AzureDesignStudio.Workspace/    # SQLite EF Core workspace (client-side persistence)
│   │   ├── WorkspaceDbContext.cs       # DbContext + WorkspaceDb bootstrap/migrations
│   │   ├── WorkspaceEntities.cs        # DesignRecord, ScanRecord, ResourceSnapshotRecord, ...
│   │   ├── SqliteDesignOfflineStore.cs # IDesignOfflineStore implementation
│   │   ├── LegacyDesignImporter.cs     # one-time v1 per-file pending import
│   │   ├── WorkspaceScans.cs / WorkspaceChanges.cs / WorkspaceDecomWindows.cs
│   │   └── TopologyPositions.cs        # identity-stable node placement
│   ├── AzureDesignStudio.Discovery/    # pure tenant-analysis engines (no project refs)
│   │   ├── Arm/                        # ArmTransport, TenantInventory, CloudProfile, deployment parsers
│   │   ├── ResourceGraph/              # ResourceGraphClient + models
│   │   ├── Scan/ScanEngine.cs          # checkpointed, pausable ARG scan
│   │   ├── Drift/                      # ScanDiffer, LiveDrift, DesignTemplateReader
│   │   ├── Exposure/                   # ExposureAnalyzer
│   │   ├── Cost/                       # ActualSpendClient, CostQueryParser, CostRollup, ChargebackDigest
│   │   ├── Decommission/               # planner, runbook, execution, idle ranking, protection windows
│   │   ├── Iac/                        # ARM validation, Bicep/Terraform text, stacks, staged plans, WhatIf/
│   │   ├── Security/                   # locks, data holdings, expiry watch, orphan detector
│   │   ├── Unmanaged/                  # detector + guardrail policy/enforcement
│   │   ├── Naming/, Preflight/, Report/, Analysis/
│   │   └── Mcp/                        # AdsToolEngine (pure), AdsToolEngineViews
│   ├── AzureDesignStudio.Mcp/          # stdio MCP server
│   │   ├── Program.cs                  # host + db path resolution
│   │   ├── AdsWorkspace.cs             # read-only SQLite window
│   │   └── AdsMcpTools.cs              # 13 [McpServerTool] methods
│   └── AzureDesignStudio.Core.Tests/   # flat xUnit project (one file per engine)
├── AGENTS.md                           # repo knowledge base (structure/conventions/anti-patterns)
├── README.md
├── CHANGELOG.md
└── LICENSE
```

## Directory Purposes

**`src/AzureDesignStudio/`:**
- Purpose: the single shared Blazor UI assembly. Compiles once; hosted by the browser OR mounted inside the MAUI `BlazorWebView`.
- Contains: shell components, one page, DI composition, gRPC wrappers, all `.razor` UI not tied to a resource form.
- Key files: `Program.cs`, `App.razor`, `Pages/Index.razor`, `Components/DiagramPanel.razor(.cs)`, `Components/TopMenu.razor(.cs)`, `Services/AdsContext.cs`, `Services/AdsDiagram.cs`.

**`src/AzureDesignStudio/Components/MenuDrawer/`:**
- Purpose: every top-level drawer/modal, one template per feature.
- Contains: `SaveDrawerTemplate`, `CodeDrawerTemplate`, `ExportDrawerTemplate`, `CompareDrawerTemplate`, `CostDrawerTemplate`, `ValidationDrawerTemplate`, `NamingDrawerTemplate`, `GuardrailsDrawerTemplate`, `AzSubscriptionDrawerTemplate`, `ImportAdsDrawerTemplate`, `UserDrawerTemplate`.
- Key files: `src/AzureDesignStudio/Components/MenuDrawer/SaveDrawerTemplate.razor.cs`, `src/AzureDesignStudio/Components/MenuDrawer/CodeDrawerTemplate.razor.cs`.

**`src/AzureDesignStudio.Core/<Service>/`:**
- Purpose: one folder per Azure service holding the diagram model and its property form.
- Contains: `<Name>Model.cs` (extends `AzureNodeBase` or `AzureGroupBase`, carries the ARM payload) and `<Name>Form.razor`.
- Key files: `src/AzureDesignStudio.Core/Storage/StorageAccountModel.cs`, `src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs`, `src/AzureDesignStudio.Core/Network/SubnetModel.cs`, `src/AzureDesignStudio.Core/Web/AppServicePlanModel.cs`.

**`src/AzureDesignStudio.Core/Common/`:**
- Purpose: cross-cutting engines and serialization utilities shared by UI, server and discovery.
- Contains: `ArmTemplate` helpers, `AzureApiVersions`, `AdsConstants`, `AdsDesignText`, `DataModelFactory` siblings (`DiagramHistory`, `DesignDiffEngine`, `AdoptEngine`, `ReconcileEngine`, `LivePlaneMerger`, `NamingEngine`, `LinkTypeClassifier`, `PlanApprovalGate`, `DeployPlanArtifact`), ports (`AzurePort`).
- Key files: `src/AzureDesignStudio.Core/Common/AdsConstants.cs` (stencil keys + type keys), `src/AzureDesignStudio.Core/Common/AzureApiVersions.cs`, `src/AzureDesignStudio.Core/Common/AdsDesignText.cs`.

**`src/AzureDesignStudio.AzureResources/Base/`:**
- Purpose: the ARM object model every service folder builds on.
- Contains: `ARMResourceBase`, `ResourceBase`, `DeploymentTemplate`, `Parameter`, `Output`, `OutputCopy`, `ResourceCopy`, `Function*`, `KeyVaultReference`, `AzureResourceAttribute`, `AzureResourceHelper`.
- Key files: `src/AzureDesignStudio.AzureResources/Base/ResourceBase.cs`, `src/AzureDesignStudio.AzureResources/Base/ARMResourceBase.cs`, `src/AzureDesignStudio.AzureResources/Base/DeploymentTemplate.cs`.

**`src/AzureDesignStudio.AzureResources/<Service>/`:**
- Purpose: generated-style ARM resource + nested property classes. Pure data, no behavior.
- Contains: type root (e.g. `StorageAccounts.cs`, `VirtualNetworks.cs`) plus every nested properties type (e.g. `Sku.cs`, `NetworkRuleSet.cs`).
- Key files: `src/AzureDesignStudio.AzureResources/Storage/StorageAccounts.cs`, `src/AzureDesignStudio.AzureResources/Network/VirtualNetworks.cs`.

**`src/AzureDesignStudio.SourceGeneration/Templates/`:**
- Purpose: Scriban templates rendered by the generator; the only place generated-code shape is defined.
- Contains: `AzureNodeDto.scriban` (per-model DTO), `AzureNodeProfile.scriban` (AutoMapper profile), `DataModelFactory.scriban` (`GetDtoTypeFromKey` / `GetNodeModelFromDto`).
- Key files: `src/AzureDesignStudio.SourceGeneration/Templates/DataModelFactory.scriban`.

**`src/AzureDesignStudio.Server/Services/`:**
- Purpose: server-side gRPC implementations and helpers.
- Contains: `DesignService` (CRUD + quota), `DeployService` (what-if, deploy, stacks, decommission windows), `RetailPriceService` (cached retail-price proxy, anonymous), `CryptoService` (subscription secret encryption), `ServiceTools` (JWT user id + input validation).
- Key files: `src/AzureDesignStudio.Server/Services/DeployService.cs`, `src/AzureDesignStudio.Server/Services/DesignService.cs`.

**`src/AzureDesignStudio.Desktop/`:**
- Purpose: Windows desktop shell around the shared UI.
- Contains: MAUI bootstrap, native auth, token broker, workspace/scan wiring, two desktop-only Blazor components.
- Key files: `src/AzureDesignStudio.Desktop/MauiProgram.cs`, `src/AzureDesignStudio.Desktop/MsalAccountService.cs`, `src/AzureDesignStudio.Desktop/AdsTokenBroker.cs`, `src/AzureDesignStudio.Desktop/Components/TenantScanDrawerTemplate.razor`.

**`src/AzureDesignStudio.Workspace/`:**
- Purpose: offline-first local persistence for the desktop host (and read-only truth for MCP).
- Contains: EF Core SQLite context, entity records, per-area query services, offline store, legacy import.
- Key files: `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs`, `src/AzureDesignStudio.Workspace/WorkspaceEntities.cs`, `src/AzureDesignStudio.Workspace/SqliteDesignOfflineStore.cs`.

**`src/AzureDesignStudio.Discovery/`:**
- Purpose: deterministic analysis libraries over inventory snapshots. No UI, no DI host, no project references.
- Contains: transports (`Arm/ArmTransport.cs`, `ResourceGraph/ResourceGraphClient.cs`), scan engine, and domain engines grouped by concern.
- Key files: `src/AzureDesignStudio.Discovery/Scan/ScanEngine.cs`, `src/AzureDesignStudio.Discovery/Mcp/AdsToolEngine.cs`, `src/AzureDesignStudio.Discovery/Decommission/DecommissionPlanner.cs`, `src/AzureDesignStudio.Discovery/Iac/WhatIf/`.

**`src/AzureDesignStudio.Mcp/`:**
- Purpose: process boundary that exposes workspace truth to agents.
- Contains: host bootstrap, read-only DB access, tool definitions. No analysis logic — that lives in `Discovery`.
- Key files: `src/AzureDesignStudio.Mcp/Program.cs`, `src/AzureDesignStudio.Mcp/AdsMcpTools.cs`, `src/AzureDesignStudio.Mcp/AdsWorkspace.cs`.

**`src/AzureDesignStudio.Core.Tests/`:**
- Purpose: single flat xUnit project covering domain, discovery, workspace and ARM generation.
- Contains: `<Subject>Tests.cs` per engine/model; `TestBase.cs` for live-Azure validation.
- Key files: `src/AzureDesignStudio.Core.Tests/StencilTests.cs`, `src/AzureDesignStudio.Core.Tests/DtoGeneratorContractTests.cs`, `src/AzureDesignStudio.Core.Tests/NewResourceTypesArmTests.cs`, `src/AzureDesignStudio.Core.Tests/WorkspaceTests.cs`.

## Key File Locations

**Entry Points:**
- `src/AzureDesignStudio/Program.cs`: WASM host — DI, MSAL, gRPC-Web, `AdsContext.InitializeAsync()`.
- `src/AzureDesignStudio.Desktop/MauiProgram.cs`: MAUI host — native MSAL, native gRPC, SQLite workspace, scan engine.
- `src/AzureDesignStudio.Server/Program.cs`: gRPC + static host, Kestrel h2c listener, EF bootstrap.
- `src/AzureDesignStudio.Mcp/Program.cs`: stdio MCP host.
- `src/AzureDesignStudio/App.razor`: router/auth root component reused by both GUI hosts.
- `src/AzureDesignStudio.Desktop/MainPage.xaml`: mounts `ads:App` inside `BlazorWebView`.

**Configuration:**
- `src/AzureDesignStudio/wwwroot/appsettings.json`: WASM `AzureAd`, `ServerScope`, `ResourceRoot`, logging.
- `src/AzureDesignStudio.Desktop/appsettings.json`: `ServerUrl`, `AzureAd`, `ResourceRoot`, `ArmServicePrincipal` ids, logging.
- `src/AzureDesignStudio.Server/appsettings.json`: server `AzureAd` (tenant/client/audience/scopes), `KeyVaultName`, `KeyVaultKeyName`, `ApplicationInsights`.
- `src/Configurations/ads-stencils.json` + `src/AzureDesignStudio/wwwroot/ads-stencils.json`: stencil catalog (key/name/iconPath/label/category).
- `src/Configurations/main/{deployment,service,ingress}.yaml`: Kubernetes manifests.
- `src/version.json`: Nerdbank.GitVersioning.

**Core Logic:**
- `src/AzureDesignStudio.Core/DataModelFactory.cs`: stencil key ↔ model ↔ DTO dispatch.
- `src/AzureDesignStudio.Core/Models/AzureNodeBase.cs`: node template method for ARM projection.
- `src/AzureDesignStudio.Core/Models/ArmTemplate.cs`: template assembly + JSON serialization.
- `src/AzureDesignStudio/Services/DesignGrpcService.cs`: online/offline save-load contract.
- `src/AzureDesignStudio.Server/Services/DeployService.cs`: what-if gate, approvals, ARM deployment.
- `src/AzureDesignStudio.Discovery/Mcp/AdsToolEngine.cs`: pure analysis surface shared by UI and MCP.

**Testing:**
- `src/AzureDesignStudio.Core.Tests/`: all tests; run with `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`.

## Naming Conventions

**Files:**
- Diagram models: `<Resource>Model.cs` — `src/AzureDesignStudio.Core/Storage/StorageAccountModel.cs`, `src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs`.
- Property forms: `<Resource>Form.razor` (`.razor.cs` only when code-behind is needed) — `src/AzureDesignStudio.Core/Web/WebAppForm.razor.cs`.
- Drawers/modals: `<Feature>DrawerTemplate.razor` under `src/AzureDesignStudio/Components/MenuDrawer/`.
- App services: `Ads*` prefix — `AdsContext`, `AdsDiagram`, `AdsCommandService`, `AdsHostAuth`, `AdsHostFeatures`, `AdsBicepDecompiler`, `AdsTokenBroker`.
- gRPC wrappers: `<Area>GrpcService.cs` — `DesignGrpcService`, `DeployGrpcService`, `CostGrpcService`.
- Discovery engines: `<Domain><Role>.cs` where role is `Engine`/`Analyzer`/`Planner`/`Client`/`Transport`/`Parser` — `ReachabilityEngine`, `ExposureAnalyzer`, `DecommissionPlanner`, `NameAvailabilityClient`, `ArmTransport`.
- Workspace: entities are `<Thing>Record` in `WorkspaceEntities.cs`; query services are `Workspace<Area>.cs` — `WorkspaceScans`, `WorkspaceChanges`, `WorkspaceDecomWindows`.
- Tests: `<Subject>Tests.cs`, one subject per file — `DriftTests.cs`, `StencilTests.cs`, `TopologyDeriverTests.cs`.
- Protobuf: snake_case `<domain>_model.proto` — `design_view_model.proto`, `deploy_model.proto`, `cost_model.proto`.

**Directories:**
- Project directories are PascalCase and prefixed `AzureDesignStudio`.
- Azure service folders mirror ARM namespaces and are PascalCase: `Network/`, `Compute/`, `Web/`, `Storage/`, `Messaging/`.
- Known inconsistency to preserve locally: Core uses `SQL/` while AzureResources uses `Sql/`; `AKS/`, `APIM/`, `SQL/` use acronym casing.
- Discovery folders name the concern, not the Azure service: `Drift/`, `Exposure/`, `Decommission/`, `Unmanaged/`, `Inventory/`, `ResourceGraph/`.

**Types and members:**
- Stencil/type keys are kebab-case string constants in `AdsConstants` (`"storage-account"`, `"virtual-network"`).
- ARM resource classes are pluralized ARM type names (`StorageAccounts`, `VirtualNetworks`); property bags are singular (`Sku`, `Encryption`).
- Private fields use `_camelCase` in newer code (`_deployClient`, `_designContext`) while older files use bare camelCase (`designContext`, `adsContext`); match the file you are editing.
- Razor parameters are PascalCase; injected services are camelCase (`adsContext`, `messageService`).

## Where to Add New Code

**New Azure resource type (end-to-end):**
1. ARM shape: add/extend the resource class under `src/AzureDesignStudio.AzureResources/<Service>/` (root type plus nested properties), following `src/AzureDesignStudio.AzureResources/Storage/StorageAccounts.cs` and annotated with `[AzureResource(ResourceType = ..., ApiVersion = ...)]`.
2. Stencil key: add a constant to `src/AzureDesignStudio.Core/Common/AdsConstants.cs`.
3. Diagram model: add `src/AzureDesignStudio.Core/<Service>/<Name>Model.cs` deriving from `AzureNodeBase` (or `AzureGroupBase` for containers), with `[MapToDto(TypeKey = AdsConstants.X)]` on the class, `[MapToDto]` on serialized properties, and overrides for `ServiceName`, `DataFormType`, `ArmResource`.
4. Form: add `src/AzureDesignStudio.Core/<Service>/<Name>Form.razor` (AntDesign form bound to the model).
5. Factory wiring — two places in `src/AzureDesignStudio.Core/DataModelFactory.cs`: the `CreateNodeModelFromKey` switch and the `StencilKeyByType` dictionary (plus `RegisterAzureModels` if it renders as a group).
6. Palette entry: add the object to `src/Configurations/ads-stencils.json`; icon goes under `src/AzureDesignStudio/wwwroot/assets/azure/`.
7. Tests: extend `src/AzureDesignStudio.Core.Tests/NewResourceTypesArmTests.cs` or add `<Name>Tests.cs`; verify `StencilTests.cs` still covers the palette contract.

**New gRPC RPC:**
- Contract: `src/AzureDesignStudio.SharedModels/<service>_model.proto`.
- Server: implement in `src/AzureDesignStudio.Server/Services/<Area>Service.cs`, then map it if new (`MapGrpcService<...>()` in `src/AzureDesignStudio.Server/Program.cs`).
- Client wrapper: `src/AzureDesignStudio/Services/<Area>GrpcService.cs`, resolved through `GrpcClientFactory` with the same client name registered in both `Program.cs` files.

**New shared drawer/modal:**
- Template: `src/AzureDesignStudio/Components/MenuDrawer/<Feature>DrawerTemplate.razor` (+ `.razor.cs` when stateful).
- Trigger: handler in `src/AzureDesignStudio/Components/TopMenu.razor.cs`, following the `ResetDrawerRef`/`OpenDrawer` pattern; register the command in `AdsCommandService` if it should appear in the palette.
- Payload models: `src/AzureDesignStudio/Models/*DrawerContent.cs`.

**New host-only capability (auth, scan, naming check):**
- Add a nullable delegate to `src/AzureDesignStudio/Services/AdsHostFeatures.cs` (or `AdsHostAuth.cs`) and consume it from shared UI; register the implementation in `src/AzureDesignStudio.Desktop/MauiProgram.cs`. Never reference Desktop types from Core/WASM.

**New discovery engine:**
- Pure logic: new file/folder under `src/AzureDesignStudio.Discovery/<Concern>/`; take snapshots/records in, return records out, inject time, sort deterministically.
- UI surface: drawer in `src/AzureDesignStudio/Components/MenuDrawer/` (Desktop-only ones live in `src/AzureDesignStudio.Desktop/Components/`).
- MCP surface: add an `[McpServerTool]` in `src/AzureDesignStudio.Mcp/AdsMcpTools.cs` delegating to `AdsToolEngine`.
- Tests: `src/AzureDesignStudio.Core.Tests/<Concern>Tests.cs`.

**New workspace table:**
- Entity: `src/AzureDesignStudio.Workspace/WorkspaceEntities.cs`; `DbSet` + constraints in `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs`; additive bootstrap in `WorkspaceDb.EnsureAdditiveTables/EnsureAddedColumns` (never edit an existing migration path).
- Read-only consumer: add a method to `src/AzureDesignStudio.Mcp/AdsWorkspace.cs` that tolerates a missing table.

**New utility/engine shared by UI and server:**
- `src/AzureDesignStudio.Core/Common/` for design-domain concerns; `src/AzureDesignStudio.Discovery/` for tenant/inventory concerns. Do not put ARM type shapes here — they belong in `AzureResources`.

## Special Directories

**`src/AzureDesignStudio/wwwroot/`:**
- Purpose: browser static assets — host page, CSS, JS (`ads.js` interop, `prism.js`, `dom-to-image-more`), service worker, stencil JSON, Azure SVG icons.
- Generated: service worker assets manifest at publish.
- Committed: yes (except generated manifests).

**`src/AzureDesignStudio.Desktop/AppPackages/`:**
- Purpose: MSIX test-package output from `dotnet publish`.
- Generated: yes.
- Committed: present in the working tree; treat as build output, never edit.

**`src/Configurations/`:**
- Purpose: deployment manifests and the canonical stencil catalog.
- Generated: no.
- Committed: yes.

**`src/*/bin/`, `src/*/obj/`:**
- Purpose: build output, including generated source under `obj/gen*/AzureDesignStudio.SourceGeneration/...` (inspect for debugging, never edit).
- Generated: yes.
- Committed: no.

**`.planning/`:**
- Purpose: GSD planning artifacts, including this codebase map.
- Generated: by workflows.
- Committed: per workflow policy (not by this mapper).

**Root `bd2.dll` / `bd_served.dll`:**
- Purpose: unreferenced binaries at the repository root (no project or workflow references them).
- Generated: unknown.
- Committed: currently present; do not add code that depends on them.

---

*Structure analysis: 2026-10-21*
