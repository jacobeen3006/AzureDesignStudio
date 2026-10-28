# PROJECT KNOWLEDGE BASE

**Generated:** 2026-10-28
**Commit:** `578949c`
**Branch:** `main`

## OVERVIEW

Azure Design Studio — Blazor app for visual Azure architecture design, hosted as WASM (Entra ID work+personal accounts) and MAUI Blazor Hybrid desktop (Windows). Diagramming, validation, ARM/Bicep IaC generation, export. .NET 10, 11 projects in src/. Microsoft Global Hackathon 2022 winner.

## STRUCTURE

```
AzureDesignStudio/
├── src/
│   ├── AzureDesignStudio/              # Blazor WASM UI client
│   │   ├── Components/                 # DiagramPanel, Stencil, TopMenu, MenuDrawer
│   │   ├── Pages/                      # Index.razor (single page)
│   │   ├── Services/AdsContext.cs      # Diagram state + stencil loading
│   │   ├── Models/                     # Client-side models (StencilModel, etc.)
│   │   └── Shared/                     # MainLayout, RedirectToLogin
│   ├── AzureDesignStudio.Server/       # ASP.NET Core gRPC host
│   │   ├── Services/                   # DesignService, DeployService, CryptoService
│   │   ├── Models/                     # EF Core: DesignDbContext, DesignModel
│   │   ├── Utils/                      # AdsTelemetryInitializer
│   │   └── Dockerfile                  # .NET 10.0 multi-stage build
│   ├── AzureDesignStudio.Core/         # Shared components + domain models
│   │   ├── Components/                 # AzureNodeComponent, AzureGroupComponent
│   │   ├── DTO/                        # AzureNodeDto, AzureNodeProfile (AutoMapper)
│   │   ├── Models/                     # AzureNodeBase, IAzureNode, IAzureResource
│   │   ├── Network|Compute|Web|...     # Per-resource logic (node models + forms)
│   │   └── Common/                     # Shared utilities
│   ├── AzureDesignStudio.AzureResources/  # ARM resource model definitions
│   │   ├── Base/                       # ResourceBase → ARMResourceBase hierarchy
│   │   └── Network|Compute|Web|...     # Generated ARM type models per service
│   ├── AzureDesignStudio.SharedModels/ # Protobuf definitions (design_view_model, deploy_model)
│   ├── AzureDesignStudio.SourceGeneration/ # Roslyn source generator (Scriban templates)
│   ├── AzureDesignStudio.Core.Tests/   # xUnit tests (offline + live-Azure categories)
│   ├── AzureDesignStudio.Workspace/    # SQLite workspace (EF Core): designs/scans/snapshots/cost cache/plans/audit
│   ├── AzureDesignStudio.Discovery/    # Tenant analysis engines: scans/drift, exposure, guardrails, decommission planner/runbook, MCP tool engine
│   ├── AzureDesignStudio.Mcp/          # Stdio MCP server: 13 read-only tools over the workspace (plan-only decom; never mutates)
│   └── AzureDesignStudio.Desktop/      # MAUI BlazorWebView host (reuses App.razor; EnableTelemetry=false)
├── .github/workflows/                  # build.yml (build+test+CodeQL), publish.yml (tag v*: WASM zip, Docker, MSIX), codeql-analysis.yml
└── assets/ docs/                       # Screenshots, documentation
```

## WHERE TO LOOK

| Task | Location | Notes |
|------|----------|-------|
| Add new resource type | Core/`<service>`/ + AzureResources/`<service>`/ | Add ARM model in AzureResources, node + form in Core |
| Add page/route | AzureDesignStudio/Pages/Index.razor | Single-page app; new content = new component drawer |
| Add gRPC service | Server/Services/ + SharedModels/*.proto | Define proto, implement service, register in Server/Program.cs |
| Add UI drawer/modal | AzureDesignStudio/Components/MenuDrawer/ | Follow existing drawer template pattern |
| Modify diagram behavior | Core/Components/AzureNodeComponent.razor | Core renders diagram; WASM client hosts it |
| Local persistence (offline) | AzureDesignStudio.Workspace/ | EF Core SQLite `workspace.db`; `SqliteDesignOfflineStore` = pending-design contract |
| Fix build/packages | `Directory.Packages.props` + `Directory.Build.props` | Versions central; csproj declares `Include=` only |
| CI/CD | .github/workflows/build.yml + Server/Dockerfile | Docker build via DockerBuild.ps1 |
| MCP tool surface | AzureDesignStudio.Mcp/ + Discovery/Mcp/AdsToolEngine.cs | Read-only workspace truth for agents; engines pure/tested, host thin + ReadOnly SQLite |

## CONVENTIONS

- **SDK projects**: `Microsoft.NET.Sdk.BlazorWebAssembly` (WASM), `Microsoft.NET.Sdk.Web` (Server), `Microsoft.NET.Sdk.Razor` (Core), `Microsoft.NET.Sdk` (others)
- **Nullable**: enabled in all projects except AzureResources
- **ImplicitUsings**: enabled in all SDK-style projects
- **TFM**: `net10.0` (Desktop: `net10.0-windows10.0.19041.0`); SourceGeneration targets `netstandard2.0`
- **Build authority**: `Directory.Build.props` owns repo-wide properties (MauiVersion pin, lock files, warnings, audit level); `Directory.Packages.props` owns every package version; each csproj declares only `Include=` on references; locked restore is a CI flag, not a repo property
- **gRPC**: protobuf in SharedModels, clients via `Grpc.Net.ClientFactory`, server via `Grpc.AspNetCore`
- **Auth**: Entra ID work+personal - MSAL redirect in WASM, native MSAL (WAM broker + device-code fallback) in Desktop, JWT + Microsoft.Identity.Web in Server; shared drawers use the `AdsHostAuth` host-override seam
- **Diagrams**: `Z.Blazor.Diagrams` 3.0.4.1 NuGet; fork-only APIs reimplemented in `AdsDiagram`/`AzurePort`/`VirtualNetworkPort`
- **Source gen**: Roslyn incremental generator with Scriban templates
- **ARM models**: `ResourceBase` → `ARMResourceBase` hierarchy, JSON serialization, `[GeneratedCode("ArmTypeGenerator")]`
- **DI registration pattern**: `AdsBicepDecompiler` uses custom extension method pattern (`AddAdsBicepDecompiler()`)
- **Testing**: xUnit — each test project independent

## ANTI-PATTERNS

- `async void` in Blazor event handlers — use `Task` return type
- `await` calls on `void`-returning methods (e.g. `MessageService.Error` returns void)
- AntDesign 1.x: enum values not raw strings (e.g. `ButtonType.Primary`, not `"primary"`)
- gRPC clients: don't instantiate direct — use `GrpcClientFactory` with auth handler
- Z.Blazor.Diagrams 3.x: behavior deltas vs old fork live in `AdsDiagram` (delete/print/shortcuts) and port `CanAttachTo` rules

## COMMANDS

```bash
dotnet build src/AzureDesignStudio.sln       # Full solution build (Windows)
dotnet build src/AzureDesignStudio.Server -c Release && dotnet build src/AzureDesignStudio -c Release && dotnet build src/AzureDesignStudio.Core.Tests -c Release  # CI project set (Linux-safe; skips MAUI Desktop - ubuntu slnf parsing is broken in SDK 10.0.4xx and net10.0-windows cannot build there)
dotnet run --project src/AzureDesignStudio.Server  # dev gRPC server; Desktop native gRPC hits its loopback h2c listener http://localhost:5204 (Desktop `ServerUrl`) - URL endpoints stay HTTP/1.1 for browser gRPC-Web
dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"  # Offline tests
docker build -f src/AzureDesignStudio.Server/Dockerfile .  # Docker image
dotnet publish src/AzureDesignStudio.Desktop -c Release -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=MSIX -p:RuntimeIdentifier=win-x64 -p:WindowsAppSDKSelfContained=true  # MSIX package
```

## NOTES

- Diagramming via `Z.Blazor.Diagrams` NuGet — no submodule; assets served from `_content/Z.Blazor.Diagrams`
- `version.json` uses Nerdbank.GitVersioning for version stamping
- `PublishTrimmed=true` on WASM client — TrimmerRootAssembly for Msal + Collections.Immutable
- InMemory EF Core in DEBUG; SQL Server + Key Vault in production
- PITFALL: checked-in `Server/appsettings.json` has `KeyVaultName: "<kv name>"` placeholder → `CryptoService` ctor builds an invalid URI and throws, failing every `Deploy.*` RPC. Supply a real vault via user-secrets (`UserSecretsId` is set in the Server csproj)
- PITFALL: stale second copy `src/Configurations/ads-stencils.json` (2.4 KB) has drifted from the canonical `src/AzureDesignStudio/wwwroot/ads-stencils.json` (~8 KB) — Desktop and Core.Tests link the wwwroot copy; never edit the Configurations copy
- GSD planning artifacts live in `.planning/` (roadmap, phases, codebase maps); commit them with `docs(<phase>):` messages, don't hand-edit `STATE.md` counters
- gRPC reflection service only registered in DEBUG
- Server behind ingress: `ForwardedHeaders` configured for XForwardedFor/XForwardedProto
