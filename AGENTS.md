# PROJECT KNOWLEDGE BASE

**Generated:** 2026-06-27
**Commit:** `2bd69d9`
**Branch:** `main`

## OVERVIEW

Azure Design Studio — Blazor WASM app for visual Azure architecture design. Diagramming, validation, ARM/Bicep IaC generation, export. .NET 8, 7 projects in src/. Microsoft Global Hackathon 2022 winner.

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
│   │   └── Dockerfile                  # .NET 8.0 multi-stage build
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
│   ├── AzureDesignStudio.Core.Tests/   # xUnit tests
│   └── Blazor.Diagrams/               # Git submodule — diagramming library
├── .github/workflows/                  # CI: build + CodeQL
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
| Fix build/packages | root csproj files | No Directory.Build.props — version per-project |
| CI/CD | .github/workflows/build.yml + Server/Dockerfile | Docker build via DockerBuild.ps1 |

## CONVENTIONS

- **SDK projects**: `Microsoft.NET.Sdk.BlazorWebAssembly` (WASM), `Microsoft.NET.Sdk.Web` (Server), `Microsoft.NET.Sdk.Razor` (Core), `Microsoft.NET.Sdk` (others)
- **Nullable**: enabled in all projects except AzureResources
- **ImplicitUsings**: enabled in all SDK-style projects
- **TFM**: `net8.0` everywhere; SourceGeneration targets `netstandard2.0`
- **No Directory.Build.props** — each csproj declares its own packages/versions
- **gRPC**: protobuf in SharedModels, clients via `Grpc.Net.ClientFactory`, server via `Grpc.AspNetCore`
- **Auth**: AAD B2C — MSAL in WASM, JWT + Microsoft.Identity.Web in Server
- **Diagrams**: Blazor.Diagrams submodule (fork/vendor), not a NuGet ref
- **Source gen**: Roslyn incremental generator with Scriban templates
- **ARM models**: `ResourceBase` → `ARMResourceBase` hierarchy, JSON serialization, `[GeneratedCode("ArmTypeGenerator")]`
- **DI registration pattern**: `AdsBicepDecompiler` uses custom extension method pattern (`AddAdsBicepDecompiler()`)
- **Testing**: xUnit, no `Directory.Build.props` for test — each test project independent

## ANTI-PATTERNS

- `async void` in Blazor event handlers — use `Task` return type
- `await` calls on `void`-returning methods (e.g. `MessageService.Error` returns void)
- AntDesign 1.x: enum values not raw strings (e.g. `ButtonType.Primary`, not `"primary"`)
- gRPC clients: don't instantiate direct — use `GrpcClientFactory` with auth handler
- Blazor.Diagrams: don't modify submodule source unless upstream change tracked

## COMMANDS

```bash
dotnet build src/AzureDesignStudio.sln       # Full solution build
dotnet test src/AzureDesignStudio.Core.Tests  # Run unit tests
docker build -f src/AzureDesignStudio.Server/Dockerfile .  # Docker image
```

## NOTES

- `Blazor.Diagrams/` is a git submodule — `git clone --recursive` required
- `version.json` uses Nerdbank.GitVersioning for version stamping
- `PublishTrimmed=true` on WASM client — TrimmerRootAssembly for Msal + Collections.Immutable
- InMemory EF Core in DEBUG; SQL Server + Key Vault in production
- gRPC reflection service only registered in DEBUG
- Server behind ingress: `ForwardedHeaders` configured for XForwardedFor/XForwardedProto
