# PROJECT KNOWLEDGE BASE

**Generated:** 2026-10-28
**Commit:** `578949c`
**Branch:** `main`

## OVERVIEW

Azure Design Studio — Blazor app for visual Azure architecture design. The primary target is the standalone MAUI Blazor Hybrid desktop app (Windows, and macOS via Mac Catalyst shipped as a .dmg): it runs every service in-process and needs no server. The WASM client + gRPC server keep building and tested but get no new work. Diagramming, validation, ARM/Bicep IaC generation, What-If, deploy, export. .NET 10, 13 projects in src/. Microsoft Global Hackathon 2022 winner.

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
│   ├── AzureDesignStudio.Services/     # Host-neutral service implementations (no ASP.NET Core - Mac Catalyst has none)
│   │   ├── DesignService, DeployService, RetailPriceService, LocalDesignService
│   │   ├── Models/                     # EF Core: DesignDbContext, DesignModel
│   │   └── InProcess/                  # InProcessCallInvoker: generated gRPC clients call services in-process
│   ├── AzureDesignStudio.Server/       # ASP.NET Core gRPC host: thin [Authorize] Hosted* subclasses + CallerInterceptor
│   │   ├── Services/                   # HostedServices.cs, CryptoService (Key Vault)
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
│   ├── AzureDesignStudio.Server.Tests/ # xUnit tests of the gRPC services called directly (InMemory EF, fake ServerCallContext; no Azure, no network)
│   ├── AzureDesignStudio.Workspace/    # SQLite workspace (EF Core): designs/scans/snapshots/cost cache/plans/audit
│   ├── AzureDesignStudio.Discovery/    # Tenant analysis engines: scans/drift, exposure, guardrails, decommission planner/runbook, MCP tool engine
│   ├── AzureDesignStudio.Mcp/          # Stdio MCP server: 14 read-only tools over the workspace (plan-only decom; never mutates)
│   ├── AzureDesignStudio.Desktop/      # MAUI BlazorWebView host (reuses App.razor); runs Services in-process, no server
│   │   └── Platforms/Windows|MacCatalyst/ # OS-only code: Credential Manager vs Keychain secret store, Mac paths/token cache
│   └── BuildMacDmg.sh                  # macOS: universal Release build + ad-hoc/Developer ID signing + .dmg (artifacts/)
├── .github/workflows/                  # build.yml (build+test+CodeQL), publish.yml (tag v*: WASM zip, Docker, MSIX, macOS .dmg on a macos-26/Xcode 27 runner, `MACOS_RUNNER` var overrides), codeql-analysis.yml
└── assets/ docs/                       # Screenshots, documentation
```

## WHERE TO LOOK

| Task | Location | Notes |
|------|----------|-------|
| Add new resource type | Core/`<service>`/ + AzureResources/`<service>`/ | Add ARM model in AzureResources, node + form in Core |
| Add page/route | AzureDesignStudio/Pages/Index.razor | Single-page app; new content = new component drawer |
| Add gRPC service | SharedModels/*.proto + Services/ + Server/Services/HostedServices.cs + Desktop/MauiProgram.cs | Implement in Services (caller via `ServiceTools.GetUser(context)`, never `GetHttpContext()`); add an `[Authorize]` Hosted* subclass + `MapGrpcService` in Server; `InProcessCallInvoker.Add<T>` in Desktop |
| Add UI drawer/modal | AzureDesignStudio/Components/MenuDrawer/ | Follow existing drawer template pattern |
| Modify diagram behavior | Core/Components/AzureNodeComponent.razor | Core renders diagram; WASM client hosts it |
| Local persistence (offline) | AzureDesignStudio.Workspace/ | EF Core SQLite `workspace.db`; `SqliteDesignOfflineStore` = pending-design contract |
| Fix build/packages | `Directory.Packages.props` + `Directory.Build.props` | Versions central; csproj declares `Include=` only |
| CI/CD | .github/workflows/build.yml + Server/Dockerfile | Docker build via DockerBuild.ps1 (repo-root context, root `.dockerignore`) |
| IPv4 ranges / subnet allocation | Core/Network/Ipv4Cidr.cs + VirtualNetworkModel.NextFreeSubnetPrefix/NewSubnet | Strict CIDR parsing; reserved subnet names via `DedicatedSubnets` |
| Design linter (Well-Architected) | Core/Validation/DesignLinter.cs (rule table) + DesignCheck.cs (stored payload → report) | Rules use only data the models hold; Warning/Info, never Error; same report in the validation drawer and `ads_design_lint` |
| MCP tool surface | AzureDesignStudio.Mcp/ + Discovery/Mcp/AdsToolEngine.cs | Read-only workspace truth for agents; engines pure/tested, host thin + ReadOnly SQLite |

## CONVENTIONS

- **SDK projects**: `Microsoft.NET.Sdk.BlazorWebAssembly` (WASM), `Microsoft.NET.Sdk.Web` (Server), `Microsoft.NET.Sdk.Razor` (Core), `Microsoft.NET.Sdk` (others)
- **Nullable**: enabled in all projects except AzureResources
- **ImplicitUsings**: enabled in all SDK-style projects
- **TFM**: `net10.0` (Desktop: `net10.0-windows10.0.19041.0` on Windows, `net10.0-maccatalyst` on macOS - chosen by host OS, one each); SourceGeneration targets `netstandard2.0`
- **Build authority**: `Directory.Build.props` owns repo-wide properties (MauiVersion pin, lock files, warnings, audit level); `Directory.Packages.props` owns every package version; each csproj declares only `Include=` on references; locked restore is a CI flag, not a repo property
- **gRPC**: protobuf in SharedModels, clients via `Grpc.Net.ClientFactory`, server via `Grpc.AspNetCore`
- **Auth**: Entra ID work+personal - MSAL redirect in WASM, native MSAL in Desktop (Windows: WAM broker; macOS: system browser on the `http://localhost` loopback, cache in the Keychain; device-code fallback on both), JWT + Microsoft.Identity.Web in Server; shared drawers use the `AdsHostAuth` host-override seam. Desktop identity (`DesktopIdentity`) = the MSAL account, else the Azure CLI token's claims (sign-out turns that fallback off)
- **Caller identity**: services read the caller from `context.UserState[ServiceTools.UserStateKey]`; Server's `CallerInterceptor` and Desktop's `InProcessCallInvoker` put it there
- **Azure credentials (deploy/what-if)**: a linked service principal for that subscription wins (secret in Key Vault on Server, `SecretStore` on Desktop = Windows Credential Manager / macOS login Keychain); otherwise the host's `IAmbientAzureCredential` (Desktop: the signed-in user). A broken link answers 404 - it never silently falls back to the user
- **Diagrams**: `Z.Blazor.Diagrams` 3.0.4.1 NuGet; fork-only APIs reimplemented in `AdsDiagram`/`AzurePort`/`VirtualNetworkPort`
- **Source gen**: Roslyn incremental generator with Scriban templates
- **ARM models**: `ResourceBase` → `ARMResourceBase` hierarchy, JSON serialization, `[GeneratedCode("ArmTypeGenerator")]`
- **DI registration pattern**: `AdsBicepDecompiler` uses custom extension method pattern (`AddAdsBicepDecompiler()`)
- **Testing**: xUnit — each test project independent. `Core.Tests` covers Core/Discovery/Workspace; `Server.Tests` calls the gRPC service classes directly with a fake `ServerCallContext` (the caller is `UserState[ServiceTools.UserStateKey]`) and never reaches Azure, and covers the in-process host (`InProcessTests`); it also holds the tests of the client's gRPC wrappers (`DesignGrpcService`, `DeployGrpcService`), reached through the Server's reference to the client

## ANTI-PATTERNS

- `async void` in Blazor event handlers — use `Task` return type
- `await` calls on `void`-returning methods (e.g. `MessageService.Error` returns void)
- AntDesign 1.x: enum values not raw strings (e.g. `ButtonType.Primary`, not `"primary"`)
- gRPC clients: don't instantiate direct — use `GrpcClientFactory` with auth handler
- Z.Blazor.Diagrams 3.x: behavior deltas vs old fork live in `AdsDiagram` (delete/print/shortcuts) and port `CanAttachTo` rules

## COMMANDS

```bash
dotnet build src/AzureDesignStudio.sln       # Full solution build (Windows)
dotnet build src/AzureDesignStudio.Server -c Release -p:RunAOTCompilation=false && dotnet build src/AzureDesignStudio -c Release -p:RunAOTCompilation=false && dotnet build src/AzureDesignStudio.Mcp -c Release && dotnet build src/AzureDesignStudio.Core.Tests -c Release  # CI project set (Linux-safe; skips MAUI Desktop - ubuntu slnf parsing is broken in SDK 10.0.4xx and net10.0-windows cannot build there). Release enables WASM AOT, which needs the wasm-tools workload (NETSDK1147 without it) - hence the flag; the Docker build does the AOT build
dotnet run --project src/AzureDesignStudio.Server  # dev server: URL endpoints (https://localhost:7203, http://localhost:5203; HTTP/1.1 + gRPC-Web for browsers) plus a loopback h2c listener http://localhost:5204, left over from when Desktop called the server (it no longer does). The h2c listener is Development-only unless `DesktopGrpc:Port` is set (0 = off)
dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"  # Offline tests
dotnet test src/AzureDesignStudio.Server.Tests  # Server RPC tests (in Release add -p:RunAOTCompilation=false - it builds the Server, which hosts the WASM client)
node --test src/AzureDesignStudio/JsTests/*.test.cjs  # Stencil drag state machine (ads.js) tests; kept out of wwwroot so they are not published
docker build -f src/AzureDesignStudio.Server/Dockerfile .  # Docker image - context is the REPO ROOT (central props/global.json live there); src/DockerBuild.ps1 [-Tag] adds the nbgv version
dotnet publish src/AzureDesignStudio.Desktop -c Release -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=MSIX -p:RuntimeIdentifier=win-x64 -p:WindowsAppSDKSelfContained=true  # MSIX package
dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst  # macOS dev build -> bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Azure Design Studio.app
src/BuildMacDmg.sh [--version x.y.z] [--arch universal|arm64|x64] [--sign "Developer ID Application: ..."] [--notarize profile]  # macOS .dmg into artifacts/
```

## NOTES

- Diagramming via `Z.Blazor.Diagrams` NuGet — no submodule; assets served from `_content/Z.Blazor.Diagrams`
- `version.json` uses Nerdbank.GitVersioning for version stamping
- `PublishTrimmed=true` on WASM client — TrimmerRootAssembly for Msal + Collections.Immutable
- InMemory EF Core in DEBUG. Release (the image) needs a database: SQL Server from the Key Vault secret `ads-main` (`KeyVaultName`) or from `ConnectionStrings__ads-main`; without either it stops with a message saying so. `Database__Provider=InMemory` starts it with no database - smoke tests only, nothing persists. Placeholders such as `<kv name>` count as unset (`ServiceTools.IsConfigured`)
- PITFALL: checked-in `Server/appsettings.json` has `KeyVaultName: "<kv name>"` placeholder → `CryptoService` (loads the vault key on first use, not in its ctor) reports "Key Vault is not configured": `SaveSubscriptionInfo` answers 503 (400 is reserved for bad input) and every RPC that needs a linked subscription answers 404. Supply a real vault via user-secrets (`UserSecretsId` is set in the Server csproj)
- PITFALL: any Kestrel endpoint defined in code (`Listen*`) REPLACES every URL-configured endpoint (`ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, launchSettings). `Server/Utils/DesktopGrpcEndpoint` re-binds the hosting URLs next to the Desktop h2c listener - add listeners there, never with a bare `ConfigureKestrel(o => o.Listen...)`
- PITFALL: ARM/Resource Graph payload shapes are easy to get subtly wrong and offline fixtures will happily pin the wrong shape (lock name = last URL segment; `managedBy` is a top-level ARG column; deployment-operation fields sit under `properties`; ARG pages on `$skipToken`, not `resultTruncated`; `Azure-AsyncOperation` monitors answer 200 `InProgress`). Check the REST reference before writing a fixture
- Gallery designs (`wwwroot/gallery/*.json`) are generated, not hand-written: the builders are in `Core.Tests/GalleryDesignTests.cs`; regenerate with `ADS_WRITE_GALLERY=<path to wwwroot/gallery> dotnet test src/AzureDesignStudio.Core.Tests --filter GalleryDesignTests`. The same tests fail when a gallery stencil points at a missing file
- PITFALL: Z.Blazor.Diagrams 3.x wraps node/group components in its own `div.diagram-node` / `div.diagram-group`; our element is the child (`.ads-azure-node`, `.ads-group`). A selector that expects both on one element (`.group.ads-group`) matches nothing
- PITFALL: stale second copy `src/Configurations/ads-stencils.json` (2.4 KB) has drifted from the canonical `src/AzureDesignStudio/wwwroot/ads-stencils.json` (~8 KB) — Desktop and Core.Tests link the wwwroot copy; never edit the Configurations copy
- GSD planning artifacts live in `.planning/` (roadmap, phases, codebase maps); commit them with `docs(<phase>):` messages, don't hand-edit `STATE.md` counters
- Deployment stacks: `Discovery/Iac/DeploymentStack.cs` writes the api-version 2025-07-01 body - `properties.actionOnUnmanage.{resources,resourceGroups,managementGroups}` (`detach`/`delete`, delete refused until Phase 13) and the required `properties.denySettings.{mode,applyToChildScopes}`; no name/type/location in the body (the name is in the URL, a resource-group stack inherits its group's location). The plan hash binds the body plus the stack name
- Stack deployments are gated on the deployment-stacks what-if, not the plain one: the `StackWhatIf` RPC (`Discovery/Iac/StackWhatIf.cs` + `StackWhatIfClient`: PUT a temporary `deploymentStacksWhatIfResults/ads-<16 hex of the plan hash>`, read it, DELETE it; needs `deploymentStacksWhatIfResults/write`) issues a *stack* token (`WhatIfTokenGate.IssueForStack`) bound to `DeploymentStackBody.PlanHash` (template, parameters, policy, bypass flag, stack name), and `CreateStack` consumes it with `ValidateAndConsumeForStack`. A stack token and a deployment token refuse each other (`WHAT_IF_PAYLOAD_CHANGED`); changing the deny mode or bypass flag after the preview needs a new preview. `CreateStack` and `StackWhatIf` share `PrepareStackAsync` (validation, plan identity, stack body, guardrail policy, then the one resource-group lookup - so a refused request touches nothing in Azure). `StackWhatIf` also registers the scope+template+parameters plan hash so `ApprovePlan` finds a live preview. Rows are fail-closed (`StackWhatIfSummary.IndeterminacyReason`: unsupported rows, any `changeCertainty` other than `definite`, a delete/detach without an id): no token is issued for an indeterminate or guardrail-blocked stack preview, the reason comes back instead. Stack what-if rows carry no tags, so while the guardrail policy has tag rules every modify/detach/delete row is held back
- Exported templates are checked against Azure itself, not only offline: `ADS_WRITE_TEMPLATES=<dir> dotnet test src/AzureDesignStudio.Core.Tests --filter GalleryDesignTests.WriteExportedTemplatesWhenAsked` writes one template per palette entry plus the gallery designs and their stack bodies, for `az deployment group validate`/`what-if` (create nothing). `GalleryDesignTests.EveryPaletteEntryHasACompleteMinimalDesign` is the offline half
- Virtual machines and scale sets need a subnet (a VM may also sit in an availability set that is in one) and export their NIC alongside; the admin password is never stored - the template asks for the secure parameter `adminPassword`. Private endpoints and internal load balancers also need a subnet; a private endpoint takes its target from a link (or a typed id)
- Subnets export `defaultOutboundAccess` explicitly (`SubnetModel.PrivateSubnet`; omitted for delegated and reserved-name subnets; network api-version 2024-05-01 for vnets and subnets). A NAT gateway serves a subnet when it is linked to a node placed in that subnet, and needs a linked public IP. `DesignValidator` rules `outbound-access` (a machine subnet with no outbound path) and `retired-sku` (Basic and v1 network SKUs). Scans capture the top-level `sku` into `InventoryItem.Raw`, and `ScanDiffer` ignores a SKU that only the newer scan carries (no false drift on the first rescan)
- Scan status `Partial` = some query group failed or a listing was truncated. Partial scans are browsable but never the comparison baseline (`GetLatestCompletedAsync` and the Desktop rescan diff take `Completed` only) - a partial baseline makes what it missed look deleted
- PITFALL: the `wasm-tools` workload's `library-packs` copy of `Microsoft.NET.Sdk.WebAssembly.Pack` hashes differently on Windows, on Linux and on nuget.org, so a lock file written on one fails NU1403 on the others (Docker, Linux CI). The client sets `DisableImplicitLibraryPacksFolder=true` to always take nuget.org's. A machine whose NuGet cache already holds the workload copy must drop that one cache entry (`~/.nuget/packages/microsoft.net.sdk.webassembly.pack/<version>`) once
- No client-side telemetry: the web client and the Desktop app (which loads the same `wwwroot/index.html`) send nothing. Server telemetry is `AddApplicationInsightsTelemetry()`, active only when `ApplicationInsights:ConnectionString` is configured. Do not put a key or a tracking snippet in `index.html`
- gRPC reflection service only registered in DEBUG
- Desktop storage (all under `FileSystem.AppDataDirectory`): `workspace.db` (designs, scans, snapshots...) and `deploy.db` (approvals, guardrails, linked subscriptions, audit; `EnsureCreated`). Designs are local (`AdsHostFeatures.DesignsAreLocal`): no quota, no sign-in needed to save
- PITFALL: ARM rejects `location` on a resource-group-scoped deployment or what-if ("The 'location' property is not allowed"); send it only at subscription/management-group/tenant scope
- Every generated `dependsOn` is `[resourceId('type', 'name', ...)]`; `ArmTemplateValidator` (the pre-deploy gate) resolves those by type+name. Resource identity is type+name - two types may share a name
- macOS Desktop (Mac Catalyst) is not sandboxed (Entitlements.plist): it runs `az`, listens on a loopback port for the browser sign-in, and uses the file-based login Keychain (`KeychainSecretStore`, P/Invoke - the SecItem data-protection keychain needs a team-signed entitlement). Because it is unsandboxed, MAUI's `AppDataDirectory` is the user's real ~/Library: Mac app data goes to `MacHost.AppDataDirectory` (~/Library/Application Support/<bundle id>) instead
- PITFALL (macOS Desktop): a Finder-launched app gets launchd's PATH; `MacHost.ExtendToolPath` adds /opt/homebrew/bin and /usr/local/bin for `az`. Read-only content (wwwroot, appsettings.json, ads-stencils.json) is in Contents/Resources (`AppContent.Root`), not next to the executable. On Unix `Uri.TryCreate("/", Absolute)` succeeds (file:///) - never treat that as a remote root
- PITFALL (macOS Desktop build): Release Mac Catalyst needs `UseInterpreter=true` (else dynamic code is off and EF Core refuses to build its model at startup) and the WASM ProjectReference drops `AppendRuntimeIdentifierToOutputPath` (the universal build's per-RID inner builds otherwise look for the client's static web assets under obj/.../browser-wasm/). Grpc.Tools must stay >= 2.84 (first with a universal macOS protoc; older ones need Rosetta on Apple silicon) - Server/Server.Tests still resolve 2.66 transitively via Grpc.AspNetCore, harmless since only SharedModels compiles .proto files. The Mac SDK pins one exact Xcode; `ValidateXcodeVersion=false` accepts point releases
- Desktop lock files: `packages.lock.json` is the Windows restore (CI checks it in locked mode); `packages.maccatalyst.lock.json` is the macOS restore (`NuGetLockFilePath`). A Mac with a newer SDK patch than global.json rewrites the other projects' locks (implicit 10.0.x package versions) - do not commit that drift
- Server behind ingress: `ForwardedHeaders` configured for XForwardedFor/XForwardedProto
