# Azure Design Studio — Architecture

**Repo**: `github.com/chunliu/AzureDesignStudio`  
**Award**: 3rd Place Winner, Microsoft Global Hackathon 2022  
**License**: GPL v3

---

## Overview

Azure Design Studio is a **Blazor WebAssembly** SPA for visually designing Azure solution architectures. Users drag Azure resources onto a diagram canvas, configure properties, and export designs as images or **ARM/Bicep templates** ready for deployment.

---

## Solution Structure (8 projects)

```
AzureDesignStudio.sln
├── AzureDesignStudio                  — Blazor WASM client (UI)
├── AzureDesignStudio.Server           — ASP.NET Core gRPC backend
├── AzureDesignStudio.Core             — Shared domain logic, diagram models, ARM generation
├── AzureDesignStudio.SharedModels     — Protobuf contracts (gRPC service definitions)
├── AzureDesignStudio.AzureResources   — CLS-compliant Azure resource type definitions
├── AzureDesignStudio.SourceGeneration — Roslyn source generator for DTO/mapping profiles
├── AzureDesignStudio.Core.Tests       — xUnit unit tests
└── Blazor.Diagrams/                   — Git submodule (forked Blazor.Diagrams library)
```

**Target**: .NET 7.0, Blazor WebAssembly

---

## Tech Stack

| Layer | Technology |
|---|---|
| **UI framework** | Blazor WebAssembly + .NET 7 |
| **UI components** | Ant Design Blazor 0.14.4 |
| **Diagramming** | Blazor.Diagrams (custom fork — submodule) |
| **Auth** | Azure AD B2C (MSAL via `Microsoft.Authentication.WebAssembly.Msal`) |
| **Backend API** | ASP.NET Core gRPC with gRPC-Web (browser-compatible) |
| **ORM** | Entity Framework Core (InMemory dev / SQL Server prod) |
| **Mapping** | AutoMapper 12 + custom Roslyn source generator |
| **Secrets** | Azure Key Vault (production) |
| **Telemetry** | Azure Application Insights |
| **IaC generation** | Azure.ResourceManager SDK, Azure.Deployments.Core |
| **Bicep** | Azure.Bicep.Decompiler (import existing .bicep files) |
| **CI** | GitHub Actions, CodeQL, Nerdbank.GitVersioning |
| **Deployment** | Docker, Kubernetes (deployment.yaml, ingress.yaml) |

### Key NuGet Packages

| Package | Purpose |
|---|---|
| `AntDesign` 0.14.4 | UI component library |
| `AutoMapper` 12.0.1 | Object mapping |
| `Google.Protobuf` + `Grpc.Net.Client` | gRPC communication |
| `Microsoft.AspNetCore.Components.WebAssembly` 7.0.4 | WASM host |
| `Azure.Bicep.Decompiler` 0.15.31 | Bicep → ARM conversion |
| `Azure.ResourceManager` | Azure SDK for ARM deployment |
| `BlazorApplicationInsights` 2.2.0 | Client-side telemetry |
| `Nerdbank.GitVersioning` 3.5.119 | Automatic versioning |

---

## Architecture & Data Flow

```
┌─────────────────────────────────────────────────────┐
│                   Browser (Blazor WASM)              │
│                                                      │
│  Pages/Index.razor                                   │
│  ├── TopMenu.razor         (save/load/export/code)  │
│  ├── StencilPanel.razor    (resource palette)        │
│  └── DiagramPanel.razor    (Blazor.Diagrams canvas)  │
│                                                      │
│  Components/                                         │
│  ├── Stencil.razor         (individual drag icons)   │
│  ├── NodeDrawerTemplate    (resource config forms)   │
│  └── MenuDrawer/*          (save/code/export/sub)    │
│                                                      │
│  Services/                                           │
│  ├── AdsContext.cs         (singleton state)          │
│  ├── DesignGrpcService.cs  (gRPC client)             │
│  ├── DeployGrpcService.cs  (gRPC client)             │
│  ├── AdsBicepDecompiler.cs (Bicep parsing)           │
│                                                      │
│  Auth: MSAL → Azure AD B2C                           │
└──────────────┬──────────────────────────────────────┘
               │ gRPC-Web (protobuf)
               ▼
┌─────────────────────────────────────────────────────┐
│           AzureDesignStudio.Server (ASP.NET Core)    │
│                                                      │
│  Services/                                           │
│  ├── DesignService.cs   ─── EF Core ───► SQL Server  │
│  │   (CRUD designs, quota: 10/user)                  │
│  ├── DeployService.cs   ─── Azure SDK ──► ARM API    │
│  │   (deploy ARM templates, stream status)           │
│  └── CryptoService.cs   (encrypt stored credentials) │
│                                                      │
│  Auth: JWT Bearer + AzureAdB2C                       │
│  Telemetry: Application Insights                     │
└─────────────────────────────────────────────────────┘
```

---

## Domain Model

### Core Interfaces

```
IAzureNode                  IAzureResource              IArmTemplate
  │                              │                          │
  │  Diagram-level concerns      │  ARM generation           │  Template wrapper
  │  - ServiceName               │  - GetArmResources()      │  - Template
  │  - DataFormType              │  - GetArmParameters()     │
  │  - IsDrappable()             │  - Name, Location         │
  │  - GetNodeDto()              │  - ResourceId             │
  │                              │                          │
  └──────────┬───────────────────┘                          │
             │                                               │
    ┌────────▼────────┐                                      │
    │  AzureNodeBase  │  (NodeModel + IAzureNode + IAzureRsrc)│
    │  (leaf nodes)   │                                      │
    └─────────────────┘                                      │
                                                              │
    ┌────────▼────────┐                                      │
    │ AzureGroupBase  │  (GroupModel + IAzureNode + IAzureRsrc)
    │  (containers)   │
    └─────────────────┘
```

### Key Design Decisions

- **`AzureNodeBase`** extends Blazor.Diagrams `NodeModel` and implements both `IAzureNode` and `IAzureResource` — a single class handles diagram appearance AND ARM template generation
- **`AzureGroupBase`** extends `GroupModel` for container resources (VNet, SQL Server, App Service Plan). Children are visually nested and logically scoped
- **`GetArmResources()`** on each model produces the ARM resource JSON. The model hierarchy mirrors the ARM resource nesting (e.g., SQL Database is a child resource of SQL Server)
- **`IsDrappable()`** enforces placement rules — prevents nesting resources where Azure doesn't allow it

### Supported Azure Resources (14 types)

| Category | Resource | Type | Base Class |
|---|---|---|---|
| **Network** | Virtual Network | Group | `AzureGroupBase` |
| | Subnet | Group | `AzureGroupBase` |
| | Azure Firewall | Leaf | `AzureNodeBase` |
| | Azure Bastion | Leaf | `AzureNodeBase` |
| | Public IP | Leaf | `AzureNodeBase` |
| | Application Gateway | Leaf | `AzureNodeBase` |
| **Compute** | Virtual Machine | Leaf | `AzureNodeBase` |
| | App Service Plan | Group | `AzureGroupBase` |
| | Function App | Leaf | `AzureNodeBase` |
| | Web App | Leaf | `AzureNodeBase` |
| | AKS | Leaf | `AzureNodeBase` |
| **Data** | SQL Server | Group | `AzureGroupBase` |
| | SQL Database | Leaf | `AzureNodeBase` |
| **Integration** | API Management | Leaf | `AzureNodeBase` |
| **Storage** | Storage Account | Leaf | `AzureNodeBase` |

### Stencil Definitions

Stencils are configured in `src/Configurations/ads-stencils.json` — each entry defines a `key`, `name`, `iconPath`, `label`, and `category` (grouping for the palette). A `DataModelFactory.CreateNodeModelFromKey()` switch maps keys to model instances.

---

## Design Flow (End-to-End)

1. **Palette** → `StencilPanel.razor` reads `ads-stencils.json`, renders `Stencil.razor` for each
2. **Drag** → User drags a stencil onto `DiagramPanel.razor` canvas (Blazor.Diagrams)
3. **Instantiation** → `DataModelFactory.CreateNodeModelFromKey()` creates the correct `AzureNodeBase`/`AzureGroupBase` subclass
4. **Configuration** → Clicking a node opens `NodeDrawerTemplate.razor` with the resource's property form (e.g., `SqlServerForm.razor`)
5. **Persistence** → Design serialized via `SaveDiagramToDto()` → `AzureNodeDto` graph → JSON → gRPC → Server → EF Core → SQL
6. **Code Generation** → `GetArmResources()` + `GetArmParameters()` produce ARM template JSON (or Bicep via decompiler)
7. **Deployment** → ARM template sent to `DeployService` → Azure SDK → Resource Manager API — deployment status streamed back to client via gRPC server-streaming

---

## gRPC Contracts

Defined in `src/AzureDesignStudio.SharedModels/` as `.proto` files:

### Design Service
```protobuf
service Design {
  rpc Save(SaveDesignRequest)    returns(SaveDesignResponse);
  rpc GetSaved(Empty)            returns(GetSavedDesignResponse);
  rpc Load(LoadDesignRequest)    returns(LoadDesignResponse);
  rpc Delete(DeleteDesignRequest) returns(DeleteDesignResponse);
}
```

### Deploy Service
```protobuf
service Deploy {
  rpc SaveSubscriptionInfo(SubscriptionInfo)  returns(SaveSubInfoResponse);
  rpc LoadSubscriptionInfo(Empty)             returns(LoadSubInfoResponse);
  rpc GetResourceGroups(GetRgsRequest)        returns(GetRgsResponse);
  rpc CreateDeployment(DeploymentRequest)      returns(stream DeploymentResponse);
}
```

---

## Source Generation (Roslyn)

`AzureDesignStudio.SourceGeneration` is a Roslyn analyzer that at compile time:

1. Scans resource model classes in `AzureDesignStudio.AzureResources`
2. Generates DTO subclasses of `AzureNodeDto` for each resource
3. Generates AutoMapper profiles (`AzureNodeProfile`) for model → DTO → model mapping
4. Generates `GetDtoTypeFromKey()` and `GetNodeModelFromDto()` partial methods in `DataModelFactory`

This eliminates manual DTO boilerplate when adding new Azure resource types.

---

## Backend Server Details

### Authentication
- **Azure AD B2C** with `Microsoft.Identity.Web`
- JWT Bearer tokens, `name` claim type
- All gRPC endpoints decorated with `[Authorize]` + `[RequiredScope]`
- Client uses MSAL (`AddMsalAuthentication`) with scoped HTTP message handler (`BaseAddressAuthorizationMessageHandler`)

### DesignService
- Per-user isolation (userId extracted from JWT)
- Maximum 10 designs per user quota
- Designs stored as JSON blobs (`DesignData` column)
- Entity Framework Core with InMemory DB (dev) or SQL Server (prod)

### DeployService
- Stores encrypted Azure subscription credentials (`ICryptoService`)
- Uses `Azure.ResourceManager` SDK to authenticate via `ClientSecretCredential`
- Deploys ARM templates with `ArmDeploymentCollection.CreateOrUpdateAsync()` (incremental mode)
- Server-streaming gRPC for real-time deployment status updates (polls every 2s)

### CryptoService
- Abstraction for encrypting Azure subscription client secrets at rest
- Registered as singleton

---

## Client Architecture

### Page Structure
```
Pages/
  Index.razor              — Main workspace (TopMenu + StencilPanel + DiagramPanel)

Components/
  TopMenu.razor            — Save/Load/Export/Code gen/User menu
  StencilPanel.razor       — Azure resource palette (draggable)
  DiagramPanel.razor       — Blazor.Diagrams canvas host
  Stencil.razor            — Individual stencil icon + drag source
  IconComponent.razor      — SVG icon wrapper (renders Azure resource SVG)
  NodeDrawerTemplate.razor — Property editor sliding drawer
  MenuDrawer/
    SaveDrawerTemplate.razor
    CodeDrawerTemplate.razor
    ExportDrawerTemplate.razor
    AzSubscriptionDrawerTemplate.razor
    UserDrawerTemplate.razor
```

### State Management
- `AdsContext` — Singleton service holding design state, diagram instance, auth state
- Initialized on app start (`await adsContext.InitializeAsync()`)
- Diagram state serialized/deserialized to/from `DiagramGraph` DTO

### Services
- `DesignGrpcService` — gRPC client wrapping `Design.DesignClient`
- `DeployGrpcService` — gRPC client wrapping `Deploy.DeployClient`
- `AdsBicepDecompiler` — wraps `Azure.Bicep.Decompiler` for Bicep → ARM conversion

---

## DevOps & CI/CD

### Docker
- `DockerBuild.ps1` — build script
- `Dockerfile` — containerizes the Server project

### Kubernetes
- `Configurations/main/deployment.yaml` — K8s deployment
- `Configurations/main/service.yaml` — K8s service
- `Configurations/main/ingress.yaml` — K8s ingress

### CI (GitHub Actions)
- `build.yml` — build, test, publish
- `codeql-analysis.yml` — CodeQL security scanning (manual trigger)

### Versioning
- Nerdbank.GitVersioning via `version.json`
- Version embedded at build time

---

## Project Conventions

- **Nullable reference types** enabled throughout
- **Implicit usings** enabled
- **Source generators** for repetitive DTO/mapping code
- **Interfaces over inheritance** — `IAzureNode`, `IAzureResource` define contracts
- **Virtual methods** in base classes for customization per resource type
- **xUnit** for testing with shared `TestBase` class
- **No frontend JS framework** — pure Blazor WASM with Ant Design
- **gRPC-Web** for browser-server communication (no REST)

---

## Adding a New Azure Resource

1. Add resource type models in `AzureDesignStudio.AzureResources/<Category>/`
2. Create model class in `AzureDesignStudio.Core/<Category>/` extending `AzureNodeBase` or `AzureGroupBase`
3. Add property form Razor component (e.g., `MyResourceForm.razor`)
4. Register in `AdsConstants.cs` (type key constant)
5. Add stencil entry in `Configurations/ads-stencils.json`
6. Add case in `DataModelFactory.CreateNodeModelFromKey()`
7. (Source generator handles DTO and mapper automatically)
