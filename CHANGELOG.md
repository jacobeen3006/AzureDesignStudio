# Changelog

All notable changes to Azure Design Studio are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the version scheme is SemVer via Nerdbank.GitVersioning.

## [1.0.0] - 2026-10-13

### Added
- **Resource catalog — 49 stencils** (43 deployable resources + 6 reference architectures) across Networking, Compute, Database, Storage/AI and Others: adds NAT Gateway, Traffic Manager, ExpressRoute, NSG, Container Instance, VM Scale Set, Cosmos DB, AI Search, Data Factory, Recovery Services Vault, Log Analytics, Logic App, Static Web App, Application Insights, App Configuration, SignalR, plus the earlier AI-era wave (Azure OpenAI/Foundry, Container Apps + Environment + Jobs, Service Bus, Event Hubs, Event Grid, Key Vault, Load Balancer, Private Endpoint, DNS Zone, Azure Redis, Container Registry, Fabric capacity, Availability Set).
- **Monthly cost estimation** per node and per design (Azure Retail Prices API with bundled snapshot fallback when offline).
- **Design validation** drawer: naming rules, placement rules (SQL DB, Container App, subnet, availability set), SKU-region availability, subnet delegation, required children, per-node checks.
- **Windows desktop app** (.NET MAUI): native window, native gRPC (HTTP/2) to the server, MSAL sign-in via the Windows broker (WAM) with device-code fallback, offline design cache (designs saved while the server is unreachable are stored locally and sync on reconnect), configurable server endpoint via `appsettings.json`.
- **Entra authentication** end to end: MSAL in the browser client, JWT + Microsoft.Identity.Web on the server, scoped server key.
- **CI**: solution build plus offline test gate on every push/PR (`Category!=LiveAzure`), CodeQL analysis, Docker image build; `publish.yml` produces release artifacts (WASM site zip, server Docker image, signed-optional Windows MSIX) on version tags.

### Changed
- Diagram engine moved from the vendored Blazor.Diagrams fork to the upstream **Z.Blazor.Diagrams 3.0.4.1** NuGet package; fork-only editor behavior (grouping drag, save-on-exit prompt, center-on-load) reimplemented in app code (`AdsDiagram`, `AzurePort`, `SnappingDragBehavior`).
- Editor UX: alignment guides + grid snapping, minimap navigator, Ctrl+Z/Y/Shift+Z undo-redo, Ctrl+C/V copy-paste, context-menu delete with confirmation.
- ARM generation pins exact `type@apiVersion` per resource; 73 offline contract/regression tests.

### Removed
- Blazor.Diagrams git submodule and its local net10 patch — no submodule step needed to build; a clean clone now builds with a single `dotnet build`.

## [0.x] - 2022–2024

- Original hackathon-era app: diagram editor on the forked Blazor.Diagrams, ARM/Bicep generation, Azure deploy, AAD B2C sign-in, 14-resource catalog.
