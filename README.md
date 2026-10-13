# Azure Design Studio

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET Build](https://github.com/jacobeen3006/AzureDesignStudio/actions/workflows/build.yml/badge.svg)](https://github.com/jacobeen3006/AzureDesignStudio/actions/workflows/build.yml)

**Azure Design Studio** is a web application designed to simplify and streamline the process of creating solution architectures for Azure. With a focus on ease of use, efficiency, and consistency, it offers several key features:

- **🎨 Visual Design**: Create solution architecture for Azure using a visually appealing and consistent styling.
- **✅ Validation**: Ensure your design adheres to the rules and constraints of Azure resources to reduce errors.
- **📤 Export**: Export your design as images for easy integration into your documents and presentations.
- **☁️ Cloud Storage**: Save your design in the cloud for convenient access from any location.
- **💻 Infrastructure as Code (IaC) generation**: Automatically generate ARM/Bicep templates for your design.

## ✨ Microsoft Global Hackathon 2022 Winner

Azure Design Studio won the **3rd Place Winner award** of Microsoft Global Hackathon 2022.

![screenshot](assets/AzureDesignStudio.gif)

---

## 🚀 Features

### Visual Architecture Design
- Drag-and-drop Azure resource palette
- Support for 14+ Azure resource types across Network, Compute, Data, Integration, and Storage categories
- Real-time validation against Azure resource constraints

### Export Options
- **Image Export**: Export diagrams as high-quality PNG images
- **ARM Template Export**: Generate Azure Resource Manager (ARM) JSON templates
- **Bicep Code Export**: Generate Bicep IaC code from your visual design
- **Code Import**: Import existing Bicep files to reuse in your designs

### Cloud Storage & Collaboration
- Save designs to Azure Cloud Storage
- Retrieve previously saved designs for future use
- Single sign-on authentication via Microsoft Entra ID (work and personal accounts)

### Infrastructure as Code Generation
- Automatically generates ARM templates from your visual designs
- Converts visual layouts to production-ready ARM JSON
- Import existing Bicep files and generate corresponding ARM templates
- Deploy ARM templates directly through the platform

---

## 🏗️ Solution Structure (7 projects)

```
AzureDesignStudio.sln
├── AzureDesignStudio                  — Blazor WASM client (UI)
├── AzureDesignStudio.Server           — ASP.NET Core gRPC backend
├── AzureDesignStudio.Core             — Shared domain logic, diagram models, ARM generation
├── AzureDesignStudio.SharedModels     — Protobuf contracts (gRPC service definitions)
├── AzureDesignStudio.AzureResources   — CLS-compliant Azure resource type definitions
├── AzureDesignStudio.SourceGeneration — Roslyn source generator for DTO/mapping profiles
└── AzureDesignStudio.Core.Tests       — xUnit unit tests
```

**Target**: .NET 8.0, Blazor WebAssembly

---

## 🛠️ Tech Stack

| Layer | Technology |
|---|---|
| **UI framework** | Blazor WebAssembly + .NET 10 |
| **UI components** | Ant Design Blazor 1.6.2 |
| **Diagramming** | Z.Blazor.Diagrams 3.0.4.1 (NuGet) |
| **Auth** | Microsoft Entra ID (MSAL via `Microsoft.Authentication.WebAssembly.Msal`) |
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
| `Microsoft.AspNetCore.Components.WebAssembly` 8.0.1 | WASM host |
| `Azure.Bicep.Decompiler` 0.15.31 | Bicep → ARM conversion |
| `Azure.ResourceManager` | Azure SDK for ARM deployment |
| `BlazorApplicationInsights` 2.2.0 | Client-side telemetry |
| `Nerdbank.GitVersioning` 3.5.119 | Automatic versioning |

---

## 📊 Supported Azure Resources

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

---

## 🏃 Architecture

### End-to-End Flow

```
┌─────────────────────────────────────────────────────┐
│                   Browser (Blazor WASM)              │
│                                                      │
│  Pages/Index.razor                                  │
│  ├── TopMenu.razor      (save/load/export/code)    │
│  ├── StencilPanel.razor  (resource palette)         │
│  └── DiagramPanel.razor  (Blazor.Diagrams canvas)  │
│                                                      │
│  Components/                                        │
│  ├── Stencil.razor       (individual drag icons)    │
│  ├── NodeDrawerTemplate   (resource config forms)    │
│  └── MenuDrawer/*        (save/code/export/sub)     │
│                                                      │
│  Services/                                          │
│  ├── AdsContext.cs        (singleton state)          │
│  ├── DesignGrpcService.cs  (gRPC client)            │
│  ├── DeployGrpcService.cs  (gRPC client)            │
│  └── AdsBicepDecompiler.cs (Bicep parsing)          │
│                                                      │
│  Auth: MSAL → Microsoft Entra ID                   │
└──────────────┬──────────────────────────────────────┘
              │ gRPC-Web (protobuf)
              ▼
┌─────────────────────────────────────────────────────┐
│           AzureDesignStudio.Server (ASP.NET Core)    │
│                                                      │
│  Services/                                          │
│  ├── DesignService.cs   ─── EF Core ───► SQL Server │
│  │   (CRUD designs, quota: 10/user)                │
│  ├── DeployService.cs   ─── Azure SDK ──► ARM API    │
│  │   (deploy ARM templates, stream status)          │
│  └── CryptoService.cs   (encrypt stored credentials) │
│                                                      │
│  Auth: JWT Bearer + AzureAd                        │
│  Telemetry: Application Insights                 │
└─────────────────────────────────────────────────────┘
```

### Key Design Decisions

- **`AzureNodeBase`** extends Blazor.Diagrams `NodeModel` and implements both `IAzureNode` and `IAzureResource` — a single class handles diagram appearance AND ARM template generation
- **`AzureGroupBase`** extends `GroupModel` for container resources (VNet, SQL Server, App Service Plan). Children are visually nested and logically scoped
- **`GetArmResources()`** on each model produces the ARM resource JSON. The model hierarchy mirrors the ARM resource nesting
- **`IsDrappable()`** enforces placement rules — prevents nesting resources where Azure doesn't allow it

### Source Generation (Roslyn)

`AzureDesignStudio.SourceGeneration` is a Roslyn analyzer that at compile time:

1. Scans resource model classes in `AzureDesignStudio.AzureResources`
2. Generates DTO subclasses of `AzureNodeDto` for each resource
3. Generates AutoMapper profiles (`AzureNodeProfile`) for model → DTO → model mapping
4. Generates `GetDtoTypeFromKey()` and `GetNodeModelFromDto()` partial methods in `DataModelFactory`

This eliminates manual DTO boilerplate when adding new Azure resource types.

---

## 📖 Design Flow

1. **Palette** → `StencilPanel.razor` reads `ads-stencils.json`, renders `Stencil.razor` for each resource
2. **Drag** → User drags a stencil onto `DiagramPanel.razor` canvas (Blazor.Diagrams)
3. **Instantiation** → `DataModelFactory.CreateNodeModelFromKey()` creates the correct `AzureNodeBase`/`AzureGroupBase` subclass
4. **Configuration** → Clicking a node opens `NodeDrawerTemplate.razor` with the resource's property form (e.g., `SqlServerForm.razor`)
5. **Persistence** → Design serialized via `SaveDiagramToDto()` → `AzureNodeDto` graph → JSON → gRPC → Server → EF Core → SQL
6. **Code Generation** → `GetArmResources()` + `GetArmParameters()` produce ARM template JSON (or Bicep via decompiler)
7. **Deployment** → ARM template sent to `DeployService` → Azure SDK → Resource Manager API — deployment status streamed back to client via gRPC server-streaming

---

## 🚀 Deployment

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

## 🔐 Authentication

### Client-Side (WASM)
- **Microsoft Entra ID** (work + personal accounts) with MSAL (`Microsoft.Authentication.WebAssembly.Msal`)
- Login, logout, and token management
- Single Sign-On support

### Server-Side
- **JWT Bearer** + AzureAd with `Microsoft.Identity.Web`
- All gRPC endpoints decorated with `[Authorize]` + `[RequiredScope]`
- Client uses MSAL (`AddMsalAuthentication`) with scoped HTTP message handler (`BaseAddressAuthorizationMessageHandler`)

---

## 🧪 Development & Testing

### Build Locally
```bash
# Full solution build
dotnet build src/AzureDesignStudio.sln

# Run unit tests
dotnet test src/AzureDesignStudio.Core.Tests

# Build Docker image
docker build -f src/AzureDesignStudio.Server/Dockerfile .
```

### Clone the Repository
```bash
git clone https://github.com/jacobeen3006/AzureDesignStudio.git
cd AzureDesignStudio
```

### Development Environment
- **Visual Studio 2022** (recommended for debugging)
- **Azure CLI** (optional, for testing Azure resource deployment)
- **Docker Desktop** (optional, for building Docker images)

---

## 📚 Documentation

- [Architecture Overview](docs/architecture.md) — Detailed system architecture and design decisions
- [Development Guidelines](AGENTS.md) — Coding standards, conventions, and development workflow

---

## 🤝 Contributing

All feedback and suggestions are welcome! Please feel free to:

- 🐛 Report bugs via GitHub Issues
- 🚀 Submit feature requests
- 📝 Create pull requests

All contributions are welcome. Make sure to follow the existing patterns and conventions in the codebase.

---

## ⚠️ Disclaimer

Azure Design Studio is a personal project without any warranty. It is neither an official product from Microsoft nor supported by Microsoft. Use it at your own risk.

---

## 📄 License

This project is licensed under the **GPL v3** License - see the [LICENSE](LICENSE) file for details.
