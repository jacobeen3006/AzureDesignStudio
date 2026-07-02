# AZURERESOURCES — ARM Resource Model Definitions

Auto-generated ARM type models (~400 files). Pure data — no business logic. Zero NuGet dependencies.

## STRUCTURE

```
AzureResources/
├── Base/               # Hierarchy + ARM template infrastructure (14 files)
│   ├── ResourceBase.cs              # Name, Type, ApiVersion, DependsOn
│   ├── ARMResourceBase.cs           # Location, Tags, Copy, Scope, Comments
│   ├── AzureResourceAttribute.cs    # [AzureResource("...")] decorator
│   ├── DeploymentTemplate.cs        # ARM template wrapper ($schema, contentVersion)
│   └── FunctionMember/KeyVaultReference/Output/Parameter/ResourceCopy
├── Network/            # 206 files — VNet, NSG, LB, VPN GW, Firewall, etc.
├── Compute/            # 77 files — VM, VMSS, Disk, Gallery, etc.
├── Web/                # 44 files — WebApp, Plan, Cert, etc.
├── Storage/            # 36 files — StorageAccount, Blob, Table, etc.
├── ApiManagement/      # 17 files — APIM, API, Policy, etc.
└── Sql/                # 14 files — Server, DB, Firewall, etc.
```

## HIERARCHY

```
ResourceBase                    # ARM JSON fields: Name, Type, ApiVersion, DependsOn
└── ARMResourceBase              # Azure-specific: Location, Tags, Properties, Copy, Scope
    ├── Network/<type>           # VNet, NSG, Subnet, etc.
    ├── Compute/<type>           # VirtualMachine, Disk, etc.
    ├── Web/<type>               # Site, ServerFarm, etc.
    └── ...                      # One file per ARM resource type
```

All classes are `partial`, annotated `[GeneratedCode("ArmTypeGenerator")]`.

## WHERE TO LOOK

| Task | Location | Notes |
|------|----------|-------|
| Find ARM schema for a resource | `<service>/<Type>.cs` | File-per-type, e.g. Network/VirtualNetwork.cs |
| Add new resource type | Create file in `<service>/` | Extend ARMResourceBase, add [AzureResource("...")] |
| Add parameters to ARM template deploy | Base/Parameter.cs | Template parameter types |

## CONVENTIONS

- **One class file per ARM resource type** — class name matches resource type
- **`[GeneratedCode("ArmTypeGenerator")]`** — all classes auto-generated, do not hand-edit
- **Property pattern**: `[JsonPropertyName("properties")]` + typed `Properties` class per resource
- **JSON serialization**: Models map directly to ARM template JSON shapes
- **Nullability**: disabled in this project (consistent with ARM model null semantics)

## ANTI-PATTERNS

- Avoid adding logic/behavior here — keep this project pure data models
- Don't add NuGet dependencies — project intentionally has none
- Don't use nullable reference types in new model files — matches project convention (`<Nullable>disable</Nullable>`)
