# CORE — Shared Domain Models + Node Components

Library of shared Blazor components, DTOs, and per-resource node models. Referenced by WASM client + Server.

## STRUCTURE

```
Core/
├── Components/         # Diagram node rendering
│   ├── AzureNodeComponent.razor           # Node render — bound to AzureNodeBase model
│   ├── AzureGroupComponent.razor          # Group render (VNet containers, etc.)
│   └── NameAndLocation.razor              # Reusable name + location form section
├── DTO/                # Data transfer + AutoMapper
│   ├── AzureNodeDto.cs                    # gRPC → client DTO
│   ├── AzureNodeProfile.cs                # AutoMapper profile Core↔DTO
│   ├── DiagramGraph.cs                    # Graph state (nodes + edges)
│   └── LinkModelDto.cs                    # Connection DTO
├── Models/              # Domain models
│   ├── AzureNodeBase.cs → NodeModel       # Base node: inherits Blazor.Diagrams NodeModel
│   ├── IAzureNode.cs                     # Node interface
│   ├── IAzureResource.cs                 # Resource interface
│   ├── AzureGroupBase.cs                 # Group node base
│   ├── ArmTemplate.cs                    # ARM template structure
│   └── AzureRegions.cs                   # Region constants
├── Network/ Compute/ Web/ Storage/ SQL/ APIM/   # Per-service node models + AntDesign forms
│   ├── *Form.razor                       # Properties form (AntDesign Drawer content)
│   └── *Model.cs                         # Node model (AzureNodeBase subclass)
├── Common/              # Shared utilities
└── Attributes/          # Custom attributes
```

## WHERE TO LOOK

| Task | Location | Notes |
|------|----------|-------|
| Add node model for new resource | Models/ + `<service>/` | Extend AzureNodeBase, implement IAzureResource |
| Add properties form | `<service>/*Form.razor` | AntDesign Form + Drawer |
| Add gRPC DTO mapping | DTO/AzureNodeProfile.cs | AutoMapper CreateMap |
| Add diagram behavior | Components/AzureNodeComponent.razor | Node rendering + interactions |
| Resource-to-ARM | Call AzureResources model via project ref | Core references AzureResources project |

## CONVENTIONS

- **Node model pattern**: extend `AzureNodeBase`, override `Initialized()` for defaults, implement `IAzureResource`
- **Form pattern**: AntDesign `<Form>` with `LabelCol`/`WrapperCol` prop objects, not LabelColSpan/WrapperColSpan
- **ArmTemplate generation**: `GenerateTemplateAsync()` returns `AzureResourceBase[]` from AzureResources models
- **Service models**: one folder per Azure service (Network/, Compute/, Web/, Storage/, SQL/, APIM/) — each contains `*Model.cs` + `*Form.razor`

## ANTI-PATTERNS

- Direct instantiation of Blazor.Diagrams types — use constructor injection via NodeModel
- Putting ARM model logic in Core service dirs — keep AzureResources files pure data models
