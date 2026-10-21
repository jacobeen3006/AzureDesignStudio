---
last_mapped_commit: a05ef8c00d2ff2dc2f0d9bea0a220051852405a8
last_mapped_at: 2026-10-21
---
# External Integrations

**Analysis Date:** 2026-10-21

<!-- refreshed: 2026-10-21 -->

## APIs & External Services

**Azure Resource Manager (ARM):**
- Deployment, resource-group listing, what-if, deployment stacks and guardrail policy — implemented twice:
  - Server side through the Azure SDK: `ArmClient`, `ArmDeploymentCollection`, `ArmDeploymentProperties` in `src/AzureDesignStudio.Server/Services/DeployService.cs`
  - Discovery/Desktop side through raw REST: `src/AzureDesignStudio.Discovery/Arm/ArmTransport.cs`
  - SDK/Client: `Azure.ResourceManager.Resources` 1.12.0 + `Azure.ResourceManager.Resources.Deployments` 1.0.0 (server), hand-rolled `HttpClient` (desktop/discovery)
  - Auth: server uses a per-linked-subscription `ClientSecretCredential` (`src/AzureDesignStudio.Server/Services/DeployService.cs`); desktop uses `Azure.Core.TokenCredential` (`AdsTokenBroker`)
  - Endpoints (`src/AzureDesignStudio.Discovery/Arm/ArmTransport.cs`): `https://management.azure.com`, `https://management.usgovcloudapi.net`, `https://management.chinacloudapi.cn`; default `api-version` `2022-12-01`
- Resource-group listing: `src/AzureDesignStudio.Server/Services/DeployService.cs` (`GetResourceGroups`, `subResource.GetResourceGroups()`) and ARG-based `TenantInventory.ListResourceGroupsAsync` in `src/AzureDesignStudio.Discovery/Arm/TenantInventory.cs`
- What-if preflight: `POST {scope}/providers/Microsoft.Resources/deployments/ads-deployment/whatIf?api-version=2025-04-01` in `src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfClient.cs`, LRO-polled with 60 max polls
- Deployment stacks: `Microsoft.Resources/deploymentStacks` PUT/GET with `DeploymentStack.ApiVersion`, list pinned at `2024-03-01` (`src/AzureDesignStudio.Discovery/Iac/DeploymentStackClient.cs`)
- Management locks: `Microsoft.Authorization/locks?api-version=2020-05-01` (`src/AzureDesignStudio.Discovery/Security/ResourceLockClient.cs`)
- Name availability: `checkNameAvailability` per provider for storage, SQL, Key Vault, container registry, AI Search, SignalR (location-scoped), Web/Function apps and APIM, with per-family api-versions (`src/AzureDesignStudio.Discovery/Naming/NameAvailabilityClient.cs`)
- Management groups: `providers/Microsoft.Management/managementGroups?api-version=2023-04-01`; subscriptions: `/subscriptions?api-version={ArmEndpointSet.ApiVersion}` (`src/AzureDesignStudio.Discovery/Arm/TenantInventory.cs`)

**Azure Resource Graph (ARG):**
- Every tenant scan, change feed and topology collector: `POST {endpoint}/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01` (`src/AzureDesignStudio.Discovery/ResourceGraph/ResourceGraphClient.cs`)
- Pagination via `$skipToken` (1 000 rows/page), quota backoff from `x-ms-user-quota-remaining`/`x-ms-user-quota-resets-after`, concurrency gate of 4 (`ResourceGraphOptions` in `src/AzureDesignStudio.Discovery/ResourceGraph/ResourceGraphModels.cs`)
- `resourcechanges` table for change history with caller attribution (`src/AzureDesignStudio.Discovery/ChangeFeed/ChangeFeedCollector.cs`)
- China cloud reports `ArgSupported=false` and must refuse graph-backed scans (`src/AzureDesignStudio.Discovery/Arm/CloudProfile.cs`)

**Azure Retail Prices API:**
- `https://prices.azure.com/api/retail/prices?$filter=...` — public OData, no auth, proxied server-side to avoid CORS (`src/AzureDesignStudio.Server/Services/RetailPriceService.cs`)
- 1-hour `IMemoryCache` per query URL, max 10 pages, automatic region-less retry for Global services
- Offline fallback for the clients: `src/AzureDesignStudio/wwwroot/price-snapshot.json` + `cost-map.json` consumed by `src/AzureDesignStudio/Services/CostService.cs`

**Azure Cost Management:**
- Actual spend: `POST {scope}/providers/Microsoft.CostManagement/query?api-version=2023-08-01`, async-job polling via `ArmTransport.PostAsyncPolled` (`src/AzureDesignStudio.Discovery/Cost/ActualSpendClient.cs`)
- Requires `Cost Management Reader`; probed in `src/AzureDesignStudio.Discovery/Preflight/PermissionPreflight.cs`

**Other ARM probes used by preflight** (`src/AzureDesignStudio.Discovery/Preflight/PermissionPreflight.cs`):
- `Microsoft.Insights/eventtypes/values?api-version=2015-04-01` (activity log)
- `Microsoft.Management/managementGroups?api-version=2023-04-01`
- `Microsoft.CostManagement/query` (7-day usage probe)

**Entra ID / MSAL:**
- Browser: `Microsoft.Authentication.WebAssembly.Msal` 10.0.12 with `AddMsalAuthentication`; authority `https://login.microsoftonline.com/common`, scope from `ServerScope` (`src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio/wwwroot/appsettings.json`)
- Desktop: native public client via `Microsoft.Identity.Client` 4.91.0 + `Microsoft.Identity.Client.Broker` 4.91.0 — Windows broker (WAM) with `msauth://{PackageFamilyName}/auth` redirect for MSIX and device-code fallback (`src/AzureDesignStudio.Desktop/MsalAccountService.cs`)
- Desktop ARM token chain, in order: MSAL silent → `AzureCliCredential` → `ManagedIdentityCredential` → `ClientSecretCredential` (secret in Windows Credential Manager) → MSAL interactive (`src/AzureDesignStudio.Desktop/AdsTokenBroker.cs`)
- Server: `AddMicrosoftIdentityWebApi` JWT bearer over the `AzureAd` config section; `[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]` on `DesignService` and `DeployService` (`src/AzureDesignStudio.Server/Program.cs`, `src/AzureDesignStudio.Server/Services/DesignService.cs`, `DeployService.cs`)
- User identity comes from the `http://schemas.microsoft.com/identity/claims/objectidentifier` claim (`src/AzureDesignStudio.Server/Services/ServiceTools.cs`)
- Microsoft Graph: not detected — no `graph.microsoft.com` endpoint, no `Microsoft.Graph` package anywhere under `src/`

**gRPC contracts (`src/AzureDesignStudio.SharedModels`):**
- `design_view_model.proto` → `Design` service, 4 unary RPCs: `Save`, `GetSaved`, `Load`, `Delete`
- `deploy_model.proto` → `Deploy` service, 15 RPCs incl. server-streaming `CreateDeployment`; also `WhatIf`, `ApprovePlan`, `PrepareRetry`, `CreateStack`, `GetStack`, `SetGuardrailPolicy`, `GetGuardrailPolicy`, `ApproveDecomWindow`, `ExecuteDecomWindow`, `ReleaseDecomWindow`
- `cost_model.proto` → `Cost` service, 1 RPC: `GetRetailPrices`
- Server implementations: `src/AzureDesignStudio.Server/Services/DesignService.cs`, `DeployService.cs`, `RetailPriceService.cs`; registered with `MapGrpcService<T>().EnableGrpcWeb()` in `src/AzureDesignStudio.Server/Program.cs`
- Transports: browsers use gRPC-Web over HTTP/1.1 with `BaseAddressAuthorizationMessageHandler` (`src/AzureDesignStudio/Program.cs`); desktop uses native HTTP/2 against the h2c listener `http://localhost:5204` with `MsalAuthTokenHandler` (`src/AzureDesignStudio.Desktop/MauiProgram.cs`)

**MCP (Model Context Protocol) stdio server:**
- Entry point `src/AzureDesignStudio.Mcp/Program.cs` — `AddMcpServer().WithStdioServerTransport().WithTools<AdsMcpTools>()`; stdout is the protocol channel, all logging rebound to stderr
- Database resolution order: `--db <path>` → `ADS_WORKSPACE_DB` env var → `%LOCALAPPDATA%\Azure Design Studio\AzureDesignStudio.Desktop\Data\workspace.db`
- 13 read-only tools in `src/AzureDesignStudio.Mcp/AdsMcpTools.cs`: `ads_scans_list`, `ads_scan_get`, `ads_scan_inventory`, `ads_scan_diff`, `ads_changes_since`, `ads_cost_summary`, `ads_designs_list`, `ads_design_get`, `ads_exposure`, `ads_reach`, `ads_unmanaged`, `ads_decom_plan`, `ads_decom_windows`
- Read-only by construction: `SqliteOpenMode.ReadOnly` + shared cache (`src/AzureDesignStudio.Mcp/AdsWorkspace.cs`); no tool calls ARM
- Tool logic is delegated to the pure engine in `src/AzureDesignStudio.Discovery/Mcp/AdsToolEngine.cs`

**Infrastructure as Code generation:**
- ARM JSON: POCO models in `src/AzureDesignStudio.AzureResources/` (`Base/DeploymentTemplate.cs`, `Base/ARMResourceBase.cs`, `[AzureResource(type, apiVersion)]`) serialized with `System.Text.Json`; api-version table derived by reflection in `src/AzureDesignStudio.Core/Common/AzureApiVersions.cs`
- Bicep import: `Azure.Bicep.Decompiler` 0.48.1 runs in-process on the client (`src/AzureDesignStudio/Services/AdsBicepDecompiler.cs`, DI in `src/AzureDesignStudio/Services/IServiceCollectionExtensions.cs`); artifact registry disabled via `EmptyArtifactRegistryProvider`
- Bicep parameters text: `src/AzureDesignStudio.Discovery/Iac/BicepParamsText.cs`; Terraform text export: `src/AzureDesignStudio.Discovery/Iac/TerraformText.cs`; ARM validation: `src/AzureDesignStudio.Discovery/Iac/ArmTemplateValidator.cs`

## Data Storage

**Databases:**
- Server designs/approvals/guardrails — SQL Server in Release, EF Core InMemory in Debug (`src/AzureDesignStudio.Server/Program.cs`)
  - Connection string key `ads-main`, resolved through Key Vault in production
  - Client: `DesignDbContext` in `src/AzureDesignStudio.Server/Models/DesignDbContext.cs` (`AdsDesigns`, `AzureSubscriptions`, `AdsPlanApprovals`, `AdsPlanApplyEvents`, `AdsGuardrailPolicies`, `AdsDecomApprovals`, `AdsDecomWindowEvents`)
  - Schema bootstrap is `EnsureCreated()` plus raw `CREATE TABLE/ALTER TABLE` guards in `src/AzureDesignStudio.Server/Program.cs`
- Desktop workspace — SQLite file `workspace.db` under `FileSystem.Current.AppDataDirectory` (`src/AzureDesignStudio.Desktop/MauiProgram.cs`, `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs`)
  - Tables: `Designs`, `Scans`, `ResourceSnapshots`, `CostCache`, `ExecutionPlans`, `AuditRecords`, `NodePositions`, `ChangeFeed`, `DecomWindows`
  - Bootstrap: `EnsureCreated()` + additive `ALTER TABLE`/`CREATE TABLE IF NOT EXISTS` in `WorkspaceDb` (`src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs`)
- Tests — SQLite provider (`src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj`)

**File Storage:**
- Local filesystem only for app data: WASM static assets under `src/AzureDesignStudio/wwwroot/`; desktop packaged `wwwroot` served through `PackageAssetHandler` (`src/AzureDesignStudio.Desktop/MauiProgram.cs`); crash log `%TEMP%\ads_desktop_crash.log` (`src/AzureDesignStudio.Desktop/MauiProgram.cs`)
- No blob storage integration found in code (blob URLs appear only in documentation/comments)

**Caching:**
- `IMemoryCache` for retail prices (`src/AzureDesignStudio.Server/Services/RetailPriceService.cs`, 1 hour)
- Persistent meter cache table `CostCache` keyed `itemId|region|skuName` (`src/AzureDesignStudio.Workspace/WorkspaceEntities.cs`)
- Browser asset caching via `src/AzureDesignStudio/wwwroot/service-worker.js` / `service-worker.published.js` (registered as `<ServiceWorker>` in `src/AzureDesignStudio/AzureDesignStudio.csproj`)

## Authentication & Identity

**Auth Provider:**
- Microsoft Entra ID (work + personal accounts), custom integration per host:
  - WASM: MSAL redirect flow served by the ASP.NET Core host (`src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio/Shared/RedirectToLogin.razor`)
  - Desktop: native MSAL/WAM broker + device-code fallback, with `DesktopAuthenticationStateProvider` (`src/AzureDesignStudio.Desktop/DesktopAuthenticationStateProvider.cs`)
  - Server: JWT bearer validation with `Microsoft.Identity.Web` 3.15.1 (`src/AzureDesignStudio.Server/Program.cs`)
- Shared UI obtains host sign-in through the `AdsHostAuth` override seam (`src/AzureDesignStudio/Services/AdsHostAuth.cs`); desktop wires it to `MsalAccountService`
- Subscription credentials: each linked subscription stores TenantId/ClientId plus an RSA-OAEP-encrypted ClientSecret (`AzureSubscriptionModel` in `src/AzureDesignStudio.Server/Models/DesignDbContext.cs`, encrypted by `src/AzureDesignStudio.Server/Services/CryptoService.cs`)

## Monitoring & Observability

**Error Tracking:**
- Azure Application Insights
  - Server: `AddApplicationInsightsTelemetry()` with `AdsTelemetryInitializer` setting cloud role `ads-server` (`src/AzureDesignStudio.Server/Utils/AdsTelemetryInitializer.cs`)
  - Clients: `BlazorApplicationInsights` 2.2.2 plus the JS SDK snippet in `src/AzureDesignStudio/wwwroot/index.html` (cloud role `ads-client`)

**Logs:**
- `Microsoft.Extensions.Logging` with configuration-bound levels (`Logging` section in each `appsettings.json`)
- Desktop adds a `FileLoggerProvider` writing Warning+ to `%TEMP%\ads_desktop_crash.log` and `AddDebug` in Debug (`src/AzureDesignStudio.Desktop/MauiProgram.cs`)
- MCP server forces every log to stderr (`src/AzureDesignStudio.Mcp/Program.cs`)

## CI/CD & Deployment

**Hosting:**
- Kubernetes (namespace `ads`): `src/Configurations/main/deployment.yaml`, `service.yaml`, `ingress.yaml` (nginx ingress, cert-manager `letsencrypt-issuer`, host `www.azuredesign.app`, `limit-rps: 30`)
- Container image built from `src/AzureDesignStudio.Server/Dockerfile`

**CI Pipeline:**
- `.github/workflows/build.yml` — .NET 10 build of server/WASM/MCP/tests, offline test run (`--filter "Category!=LiveAzure"`), Docker build via `src/DockerBuild.ps1`, plus a CodeQL C# job (`.github/codeql/codeql-config.yml` scopes analysis to `src`)
- `.github/workflows/publish.yml` — on `v*` tags: WASM zip, Docker push (needs `REGISTRY*` vars/secrets), Windows MSIX build/sign (needs `MSIX_PFX_BASE64`, `MSIX_PFX_PASSWORD`), GitHub release

## Environment Configuration

**Required env vars / config keys:**
- Server: `AzureAd:Instance`, `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:Audience`, `AzureAd:Scopes`, `KeyVaultName`, `KeyVaultKeyName`, `ApplicationInsights:ConnectionString`, `ads-main` (SQL connection string, non-DEBUG)
- Desktop: `ServerUrl`, `ResourceRoot`, `AzureAd:Authority`, `AzureAd:ClientId`, `ServerScope`, optional `ArmServicePrincipal:TenantId`, `ArmServicePrincipal:ClientId`
- WASM: `AzureAd:Authority`, `AzureAd:ClientId`, `AzureAd:ValidateAuthority`, `ServerScope`, `ResourceRoot`
- MCP: `ADS_WORKSPACE_DB` (optional; `--db` wins)

**Secrets location:**
- Azure Key Vault — secrets imported as configuration via `AddAzureKeyVault` with `DefaultAzureCredential` (non-DEBUG) and used as an RSA key for secret encryption (`src/AzureDesignStudio.Server/Program.cs`, `src/AzureDesignStudio.Server/Services/CryptoService.cs`)
- Server user secrets (`UserSecretsId` `a237ae84-fe59-44e2-8570-ed2f536ab4ed` in `src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj`)
- Windows Credential Manager for the desktop service-principal secret, namespaced `AzureDesignStudio/` (`src/AzureDesignStudio.Desktop/WindowsSecretStore.cs`)
- No `.env` files are committed or present

## Webhooks & Callbacks

**Incoming:**
- None. The server exposes only gRPC endpoints (`/Design`, `/Deploy`, `/Cost` via gRPC-Web), gRPC reflection in Debug, and the WASM fallback route (`MapFallbackToFile("index.html")`) — `src/AzureDesignStudio.Server/Program.cs`

**Outgoing:**
- None. All outbound traffic is pull-based REST/SDK calls to ARM, ARG, Cost Management and the Retail Prices API

---

*Integration audit: 2026-10-21*
