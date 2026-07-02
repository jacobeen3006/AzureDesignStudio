# Azure Refresh Plan — Modernization Assessment

> **Date:** 2026-06-27
> **Scope:** Upgrade AzureDesignStudio from .NET 7 + deprecated Azure SDK to current Azure-compatible stack

---

## 1. Target Framework: .NET 8 LTS (Recommended)

| Current | Target | Rationale |
|---|---|---|
| .NET 7.0 (EOL May 2024) | .NET 8.0 (LTS, supported thru Nov 2026) | Most stable; MS still ships patches |

.NET 9 (STS) is also viable, but .NET 8 LTS gives longer runway. All 8 projects must retarget.

---

## 2. NuGet Package Matrix

### Client — `AzureDesignStudio.csproj`

| Package | Current | Latest | Upgrade Effort | Risk |
|---|---|---|---|---|
| **AntDesign** | 0.14.4 | **1.6.2** | **HIGH** — pre-1.0 → stable, breaking component API + theming |
| Azure.Bicep.Decompiler | 0.15.31 | **0.44.1** | **HIGH** — unsupported pkg, breaking changes at MS's discretion |
| AutoMapper | 12.0.1 | 12.x | LOW |
| Google.Protobuf | 3.22.1 | 3.28.x | LOW |
| Grpc.Net.Client | 2.52.0 | 2.66.x | LOW |
| Grpc.Net.Client.Web | 2.52.0 | 2.66.x | LOW |
| Grpc.Net.ClientFactory | 2.52.0 | 2.66.x | LOW |
| Grpc.Tools | 2.52.0 | 2.66.x | LOW |
| Microsoft.AspNetCore.Components.WebAssembly | 7.0.4 | **8.0.x** | MEDIUM — .NET upgrade required |
| Microsoft.AspNetCore.Components.WebAssembly.DevServer | 7.0.4 | **8.0.x** | MEDIUM |
| Microsoft.Authentication.WebAssembly.Msal | 7.0.4 | **8.0.x** | MEDIUM |
| Microsoft.Extensions.Http | 7.0.0 | **8.0.x** | LOW |
| Microsoft.Extensions.Logging.Configuration | 7.0.0 | **8.0.x** | LOW |
| Nerdbank.GitVersioning | 3.5.119 | **3.6.x** | LOW |
| System.IO.Abstractions.TestingHelpers | 19.2.4 | 21.x | LOW |
| System.Text.Encoding | 4.3.0 | Remove | Remove — ancient, not needed in modern .NET |
| BlazorApplicationInsights | 2.2.0 | **2.3.x** | LOW |

### Server — `AzureDesignStudio.Server.csproj`

| Package | Current | Latest | Upgrade Effort |
|---|---|---|---|
| **Azure.Identity** | **1.8.2** | **1.21.0** | **HIGH** — major API churn, new credential types, TypeForwardedTo in 1.21 |
| **Azure.ResourceManager.Resources** | **1.4.0** | **~1.14.0** | **HIGH** — ArmClient model changed, new resource locator pattern |
| Azure.Security.KeyVault.Keys | 4.5.0 | **4.7.x** | LOW |
| Azure.Extensions.AspNetCore.Configuration.Secrets | 1.2.2 | **1.3.x** | LOW |
| Grpc.AspNetCore | 2.52.0 | 2.66.x | LOW |
| Grpc.AspNetCore.Server.Reflection | 2.52.0 | 2.66.x | LOW |
| Grpc.AspNetCore.Web | 2.52.0 | 2.66.x | LOW |
| Microsoft.ApplicationInsights.AspNetCore | 2.21.0 | **2.22.x** | LOW |
| Microsoft.AspNetCore.Components.WebAssembly.Server | 7.0.4 | **8.0.x** | MEDIUM |
| **Microsoft.EntityFrameworkCore.InMemory** | 7.0.4 | **8.0.x** | MEDIUM — no IR(where) |
| **Microsoft.EntityFrameworkCore.SqlServer** | 7.0.4 | **8.0.x** | MEDIUM — no IR |
| **Microsoft.Identity.Web** | **2.5.0** | **3.x** | **HIGH** — major auth library rewrite, DI changes |
| Microsoft.VisualStudio.Azure.Containers.Tools.Targets | 1.18.1 | **1.21.x** | LOW |
| Nerdbank.GitVersioning | 3.5.119 | 3.6.x | LOW |

### Tests — `AzureDesignStudio.Core.Tests.csproj`

| Package | Current | Latest |
|---|---|---|
| Azure.Identity | 1.8.2 | 1.21.0 |
| Azure.ResourceManager.Resources | 1.4.0 | ~1.14.0 |
| Microsoft.NET.Test.Sdk | 17.5.0 | **17.12.x** |
| xunit | 2.4.2 | **2.9.x** |
| xunit.runner.visualstudio | 2.4.5 | **2.8.x** |
| coverlet.collector | 3.2.0 | **6.0.x** |

### Source Generation — `AzureDesignStudio.SourceGeneration.csproj`

| Package | Current | Latest |
|---|---|---|
| Microsoft.CodeAnalysis.Analyzers | 3.3.4 | 3.11.x |
| Microsoft.CodeAnalysis.CSharp | 4.5.0 | **4.11.x** |
| Scriban | 5.7.0 | 5.10.x |

(Targets `netstandard2.0` — stays as-is, no change needed.)

---

## 3. Key Breaking Changes & Mitigations

### 3.1 AntDesign 0.14.4 → 1.6.2

**Changes:**
- API surface changed significantly from pre-1.0 to 1.x
- CSS class names, parameters, and event callbacks migrated to align with Ant Design 4.x/5.x
- Component hierarchy renamed/restructured

**Mitigation:** Incremental migration. Update to AntDesign 1.x stepwise through intermediate versions. Test each component category.

### 3.2 Azure.Identity 1.8.2 → 1.21.0

**Critical changes:**
- 1.20.0: `AddAzureClient`, `AddKeyedAzureClient`, `WithAzureCredential` return type changed from `IHostApplicationBuilder` to `IClientBuilder`
- 1.21.0: All `Azure.Identity` types moved to `Azure.Core` via `TypeForwardedTo` — transparent but may cause compilation errors if you reference `Azure.Identity` types by full namespace path

**Mitigation:** Check all `using Azure.Identity;` imports. Update `Program.cs` credential registration. Test DefaultAzureCredential chaining behavior (changed in 1.10.1).

### 3.3 Azure.ResourceManager.Resources 1.4.0 → ~1.14.0

**Changes:**
- `ArmClient` API is largely stable but `GetDefaultSubscription()` deprecated — use `GetSubscriptionResource(subscriptionId)` or call `GetSubscriptions()` 
- Resource locator pattern: `subscription.GetResourceGroup()` → `subscription.GetResourceGroupResource()`
- Some ARM resource methods moved to extension methods in different namespaces

**Mitigation:** Check `DeployService.cs` — it uses `ArmClient`, `SubscriptionResource`, `ArmDeploymentCollection`, `ArmDeploymentResource`, `ArmDeploymentContent`. The `DeployService.cs` usage pattern (`GetSubscriptions().First()`, `GetResourceGroup()` on subscription) compiles against new SDK with minor adjustments.

### 3.4 Microsoft.Identity.Web 2.5.0 → 3.x

**Changes:**
- 3.x simplified configuration for Azure AD B2C
- `Microsoft.Identity.Web.UI` components merged/renamed
- App settings schema changed — `AzureAdB2C` section may need `Instance`, `Domain`, `ClientId`, `SignUpSignInPolicyId` re-mapping

**Mitigation:** Check `Program.cs` for `AddMicrosoftIdentityWebAppAuthentication` + `AddMicrosoftIdentityWebApiAuthentication`. The new `Microsoft.Identity.Web` 3.x uses `AddMicrosoftIdentityWebApp` (singular) and changed DI convention.

### 3.5 Azure.Bicep.Decompiler 0.15.31 → 0.44.1

**⚠️ Unsupported package** — MS explicitly warns breaking changes at any time.
- Bicep version bumps (0.15 → 0.44 spans ~2 years of language changes)
- Decompiler output may use newer Bicep syntax that ARM converter expects
- ARM template schema version differences

**Mitigation:** 
- Option A: Update to latest decompiler; verify decompiled output against expected ARM schema
- Option B: Replace with `az bicep decompile` CLI invocation if programmatic API is too unstable

### 3.6 EF Core 7.0.4 → 8.0.x

**Breaking changes:**
- `SaveChanges` throws new exception types (`DbUpdateConcurrencyException` changes)
- `HasNoKey()` vs `ToView()` for keyless entities
- SQL Server provider: `UseSqlServer` parameter changes
- No more `Find` on owned entities

**Mitigation:** Review `AzureDesignStudioDbContext` usage. The project uses InMemory for dev and SQL Server for prod — both straightforward migrations.

### 3.7 gRPC

**gRPC packages 2.52.0 → 2.66.x** — minimal breaking changes. The gRPC-Web middleware + protobuf wire format is stable. Update all Grpc.* packages in lockstep.

### 3.8 Blazor Diagrams Submodule

**Commit:** `a36f375be58268f39ecf326ab0c960a78f75c322`

- Custom fork of Blazor.Diagrams
- Targets .NET 7 (`net7.0`)
- Must be updated to target .NET 8
- Check if upstream Blazor.Diagrams has updated; fork may need manual TFM change + any API fixes

---

## 4. Architecture-Level Concerns

### 4.1 Azure.Deployments.Core Dependency

`AzureDesignStudio.Server/Utils/` has `Azure.Deployments.Core` as a dependency (seen in `using Azure.Deployments.Core.Definitions`). Package is **unsupported** by MS. The actual deployment logic in `DeployService.cs` uses `Azure.ResourceManager` SDK (`ArmDeployment`, `ArmDeploymentContent`, etc.) — so `Azure.Deployments.Core` may only be used for type helpers. Needs investigation — potentially removable.

### 4.2 Blazor WASM Rendering Model

.NET 8 introduced new Blazor rendering modes (`RenderMode.InteractiveServer`, `RenderMode.InteractiveWebAssembly`, `RenderMode.InteractiveAuto`). The project currently runs WASM-only with a separate ASP.NET Core host (gRPC server). This is compatible with .NET 8 without changes, but worth evaluating if you want to adopt the new unified Blazor Web App model.

### 4.3 Trimming

The project uses `<PublishTrimmed>true</PublishTrimmed>` with trimeroot assemblies for `Microsoft.Authentication.WebAssembly.Msal` and `System.Collections.Immutable`. This pattern still works in .NET 8 but trim warnings are now stricter. May need additional `TrimmerRootAssembly` entries.

---

## 5. Recommended Execution Order

### Phase 1: Foundation (parallel)
- [ ] Update all `.csproj` TFM → `net8.0`
- [ ] Update Blazor.Diagrams submodule → `net8.0`
- [ ] Bump all ASP.NET Core packages → `8.0.x`
- [ ] Bump all EF Core packages → `8.0.x`
- [ ] Bump all gRPC packages → `2.66.x`
- [ ] Bump Nerdbank.GitVersioning → `3.6.x`
- [ ] Bump test packages (xUnit, SDK, coverlet)

### Phase 2: Azure SDK
- [ ] Bump Azure.Identity → `1.21.0`
- [ ] Bump Azure.ResourceManager.Resources → `1.14.0`
- [ ] Fix `ArmClient` API changes in `DeployService.cs`
- [ ] Fix `DefaultAzureCredential` chaining if used
- [ ] Bump Azure.Security.KeyVault.Keys → `4.7.x`

### Phase 3: Auth
- [ ] Bump Microsoft.Identity.Web → `3.x`
- [ ] Update Program.cs auth registration
- [ ] Verify Azure AD B2C config schema

### Phase 4: UI
- [ ] Bump AntDesign → `1.6.2`
- [ ] Fix component API breaks (highest risk — plan for full UI regression test)
- [ ] Fix CSS/themings differences

### Phase 5: Decompiler
- [ ] Bump Azure.Bicep.Decompiler → `0.44.1`
- [ ] Verify decompiled Bicep output
- [ ] Optionally migrate to `az bicep decompile` CLI escape hatch

### Phase 6: Polish
- [ ] Remove `System.Text.Encoding 4.3.0` (not needed on modern .NET)
- [ ] Update Dockerfile base images → `mcr.microsoft.com/dotnet/aspnet:8.0`
- [ ] Update Dockerfile SDK images → `mcr.microsoft.com/dotnet/sdk:8.0`
- [ ] Run `dotnet build` — fix all warnings/errors
- [ ] Run `dotnet test` — fix test breaks
- [ ] Run `dotnet publish` — verify trimming

---

## 6. Risk Summary

| Risk | Level | Reason |
|---|---|---|
| AntDesign migration | **CRITICAL** | 0.14 → 1.6 spans pre-1.0 to stable; extensive API differences |
| Azure.Identity update | **HIGH** | 1.8 → 1.21 covers 3+ years of changes; IClientBuilder breaking change |
| ARM SDK updates | **HIGH** | Resource locator API changes in DeployService |
| Microsoft.Identity.Web | **HIGH** | 2.5 → 3.x rewired DI and config schema |
| Bicep Decompiler | **MEDIUM** | Unsupported package; breaking changes at any MS release |
| EF Core 7 → 8 | **LOW** | Mostly additive, few breaking changes |
| gRPC updates | **LOW** | Stable wire protocol, backward compatible |

---

## 7. Estimation

Rough effort by phase (senior engineer, no unknowns):

| Phase | Effort |
|---|---|
| Foundation (TFM + bulk package bumps) | 1-2 days |
| Azure SDK fixes | 1-2 days |
| Auth migration | 0.5-1 day |
| **AntDesign UI migration** | **3-5 days** |
| Decompiler | 0.5 day |
| Polish + CI | 1 day |
| **Total** | **~7-12 days** |

Biggest unknown: AntDesign component breakage scope may add days depending on how many components use deprecated APIs.
