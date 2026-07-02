# Plan: AzureDesignStudio Platform Refresh

## Objective

Upgrade AzureDesignStudio from .NET 7 + 2023-era Azure SDK to current Azure-compatible stack (.NET 8 LTS, latest NuGet packages, updated auth, current AntDesign, working Docker build). Users can design and deploy Azure architectures without API errors, deprecated resource warnings, or authentication failures.

**Spec:** `.swarm/spec.md` (11 FRs, 6 SCs, 5 entities, 7 edge cases)
**Assessment:** `docs/azure-refresh-plan.md`
**AntDesign migration research:** Embedded in Wave 4 from researcher brief

---

## Wave 1 — Foundation & Build System (parallel)

**Goal:** All `.csproj` files target `net8.0`, packages bumped, solution compiles and tests pass.
**Dependencies:** None (starting from clone).
**Spec FRs:** FR-005, FR-007, FR-009, FR-011

### Task 1.1 — Checkout submodule + TFM bumps
- **File(s):** All 7 `.csproj` files, `src/Blazor.Diagrams/src/*/*.csproj` (2 files), `.gitmodules`
- **Scope:**
  - `git submodule update --init` (Blazor.Diagrams fork at `a36f375`, branch `ads`)
  - Change `net7.0` → `net8.0` in all 9 `.csproj` files
  - Remove `System.Text.Encoding 4.3.0` from `AzureDesignStudio.csproj` (dead weight on modern .NET)
- **Verify:** `dotnet restore` exits 0

### Task 1.2 — Bulk package bumps
- **File(s):** `AzureDesignStudio.csproj`, `AzureDesignStudio.Server.csproj`, `AzureDesignStudio.Core.Tests.csproj`, `AzureDesignStudio.SourceGeneration.csproj`
- **Scope:**
  - ASP.NET Core packages: `7.0.*` → `8.0.*`
  - EF Core: `7.0.4` → `8.0.*`
  - gRPC: all `2.52.0` → `2.66.x` (client, server, tools, web — lockstep)
  - Nerdbank.GitVersioning: `3.5.119` → `3.6.x`
  - xUnit: `2.4.2` → `2.9.x` | SDK: `17.5.0` → `17.12.x` | coverlet: `3.2.0` → `6.0.x`
  - Scriban: `5.7.0` → `5.10.x` | CodeAnalysis: `4.5.0` → `4.11.x`
- **Verify:** `dotnet build` exits 0, no NU* warnings

### Task 1.3 — Build + test baseline
- **File(s):** Whole solution
- **Scope:** Run `dotnet restore && dotnet build`, fix any compilation errors. Then `dotnet test`, record pre-existing failures.
- **Verify:** `dotnet build` exit code 0, `dotnet test` exit code 0

**Estimated:** 1.1 ~30min | 1.2 ~1hr | 1.3 ~30min
**Parallelism:** All three tasks operate on different file sets — no shared mutable state.

---

## Wave 2 — Azure SDK & Resource Model (sequential)

**Goal:** Azure.Identity 1.21.0, Azure.ResourceManager.Resources ~1.14.0, Key Vault packages bumped, DeployService.cs fixed for new ArmClient API, Azure.Deployments.Core removed.
**Depends on:** Wave 1 (SDK packages must target .NET 8).
**Spec FRs:** FR-001, FR-010, FR-011

### Task 2.1 — Azure SDK package bumps
- **File(s):** `AzureDesignStudio.Server.csproj`, `AzureDesignStudio.Core.Tests.csproj`
- **Scope:**
  - Azure.Identity: `1.8.2` → `1.21.0`
  - Azure.ResourceManager.Resources: `1.4.0` → `~1.14.0`
  - Azure.Security.KeyVault.Keys: `4.5.0` → `4.7.x`
  - Azure.Extensions.AspNetCore.Configuration.Secrets: `1.2.2` → `1.3.x`
- **Verify:** `dotnet build` after bump identifies all API changes

### Task 2.2 — Fix DeployService.cs for new ArmClient API
- **File(s):** `src/AzureDesignStudio.Server/Services/DeployService.cs`
- **Scope:**
  - `GetDefaultSubscriptionAsync()` → `GetSubscriptionResource(subscriptionId)` pattern
  - Verify `GetResourceGroupAsync()` → `GetResourceGroupResource()` if changed
  - Verify `GetResourceGroups()` → `GetResourceGroupResources()` if changed
  - Verify ArmDeploymentCollection, ArmDeploymentContent, ArmDeploymentMode API signatures
  - Check `using` directives for Azure.Identity `TypeForwardedTo` pattern (types moved to Azure.Core in 1.21.0)
- **Verify:** DeployService.cs compiles, no Azure.Deployments.Core references remain

### Task 2.3 — Remove Azure.Deployments.Core
- **File(s):** `AzureDesignStudio.Server.csproj`, `src/AzureDesignStudio.Server/Utils/*` (any files using Azure.Deployments.Core.Definitions)
- **Scope:**
  - Identify all `using Azure.Deployments.Core.*` references
  - Replace with equivalent Azure.ResourceManager types
  - Remove package from csproj
- **Verify:** `dotnet build` succeeds without the package

**Estimated:** 2.1 ~15min | 2.2 ~1hr | 2.3 ~30min
**Sequencing:** 2.1 → 2.2 (needs package bumped first) → 2.3 (verify clean removal).

---

## Wave 3 — Authentication & Identity (parallel within wave)

**Goal:** Microsoft.Identity.Web 3.x, client auth packages updated, both Program.cs files register auth correctly.
**Depends on:** Wave 1 (TFM), Wave 2 (Azure.Identity version — Identity.Web depends on it).
**Spec FRs:** FR-003

### Task 3.1 — Server auth update
- **File(s):** `AzureDesignStudio.Server.csproj`, `src/AzureDesignStudio.Server/Program.cs`
- **Scope:**
  - Bump Microsoft.Identity.Web 2.5.0 → 3.x
  - Verify `AddMicrosoftIdentityWebApi` in Server Program.cs — check params for 3.x
  - Verify JwtBearerOptions config binding
  - Verify AzureAdB2C config section binding (schema may have changed)
- **Verify:** `dotnet build` succeeds, auth config binds at startup

### Task 3.2 — Client auth update
- **File(s):** `AzureDesignStudio.csproj`, `src/AzureDesignStudio/Program.cs`
- **Scope:**
  - Bump Microsoft.Authentication.WebAssembly.Msal 7.0.4 → 8.0.x
  - Verify AddMsalAuthentication + BaseAddressAuthorizationMessageHandler compatibility
  - Verify AddGrpcClient with auth handler still works
- **Verify:** `dotnet build` succeeds

**Estimated:** 3.1 ~1hr | 3.2 ~30min
**Parallelism:** 3.1 + 3.2 can run in parallel (different files, different packages).

---

## Wave 4 — UI & AntDesign Migration (sequential)

**Goal:** AntDesign 0.14.4→1.6.2, all component API breaks fixed, gRPC client verified, app compiles.
**Depends on:** Wave 3 (auth packages must be .NET 8 versions before WASM project can link).
**Spec FRs:** FR-004, FR-008

### Task 4.1 — AntDesign package bump + API fixes
- **File(s):** `AzureDesignStudio.csproj`, all `.razor` + `.razor.cs` files
- **Scope (from researcher brief):**
  - Bump AntDesign 0.14.4 → 1.6.2
  - **Table:** `RowTemplate` → `ColumnDefinitions` in SaveDrawerTemplate.razor
  - **Form:** Verify LabelColSpan/WrapperColSpan → LabelCol/WrapperCol (~12 form files)
  - **Modal:** Fix `ModalService.CreateConfirmAsync` if used (return type changed from ConfirmRef to ConfirmResult in 1.5.0)
  - **Input:** Search for OnkeyDown/OnkeyUp → OnKeyDown/OnKeyUp (capital K, 1.5.0)
  - **Message:** Fix `_message.Success()` → `_message.SuccessAsync()` if sync API changed (1.4.0)
  - Add `<TrimmerRootAssembly Include="AntDesign" />` if publish warnings appear
  - Bump BlazorApplicationInsights 2.2.0 → 2.3.x
- **Verify:** `dotnet build` succeeds, all component categories compile

### Task 4.2 — gRPC client + AutoMapper check
- **File(s):** `src/AzureDesignStudio/Program.cs`
- **Scope:**
  - Verify GrpcWebHandler + BaseAddressAuthorizationMessageHandler chain works with updated gRPC packages
  - Verify AutoMapper 12.0.1 → latest 12.x (minor bump if needed)
- **Verify:** `dotnet build` succeeds

### Task 4.3 — Final build sweep
- **File(s):** Whole solution
- **Scope:**
  - `dotnet build` — resolve remaining compilation errors
  - Check trim root assemblies in csproj for new packages
  - Document any pre-existing warnings
- **Verify:** `dotnet build` exit code 0

**Estimated:** 4.1 ~2-3hrs (highest risk) | 4.2 ~15min | 4.3 ~30min
**4.1 + 4.2 can run parallel** (different files/concerns).

---

## Wave 5 — Bicep Decompiler (parallel with Wave 4)

**Goal:** Azure.Bicep.Decompiler 0.15.31→0.44.1, verify compiled output.
**Depends on:** Wave 1 (TFM).
**Parallelism:** Can run in parallel with Wave 4 (different package, different file scope).
**Spec FRs:** FR-002

### Task 5.1 — Decompiler update
- **File(s):** `AzureDesignStudio.csproj`, decompiler service files
- **Scope:**
  - Bump Azure.Bicep.Decompiler 0.15.31 → 0.44.1
  - Fix any API changes in IAdsBicepDecompiler implementation
  - Verify decompiled Bicep output is valid with `az bicep build`
- **Verify:** `dotnet build` succeeds, decompiler output compiles

**Estimated:** 5.1 ~1hr

---

## Wave 6 — Polish, Docker & Verification (final)

**Goal:** Dockerfile updated, trim verified, docker build passes, SAST scan clean, drift check against spec.
**Depends on:** All prior waves.
**Spec FRs:** FR-006, coverage sweep on all FRs.

### Task 6.1 — Dockerfile update
- **File(s):** `src/AzureDesignStudio.Server/Dockerfile`
- **Scope:**
  - `aspnet:7.0` → `aspnet:8.0`
  - `sdk:7.0` → `sdk:8.0`
- **Verify:** `docker build -f src/AzureDesignStudio.Server/Dockerfile .` succeeds

### Task 6.2 — Release build + publish
- **File(s):** Whole solution
- **Scope:**
  - `dotnet build -c Release` — clean build
  - `dotnet publish -c Release` — verify trimming warnings
  - Fix trim warnings with TrimmerRootAssembly entries if needed
- **Verify:** `dotnet build -c Release` exit code 0, publish completes without new errors

### Task 6.3 — Test + QA gates
- **File(s):** Test project, all changed files
- **Scope:**
  - `dotnet test -c Release` — all tests pass
  - SAST scan on changed files
  - Drift check: verify implementation matches `.swarm/spec.md`
- **Verify:** All SCs from spec met, SAST clean, no drift

**Estimated:** 6.1 ~15min | 6.2 ~30min | 6.3 ~30min

---

## Success Criteria

- [ ] `dotnet build` exits 0 (all configurations)
- [ ] `dotnet test` exits 0 (pre-existing failures documented)
- [ ] `docker build` succeeds
- [ ] `dotnet publish -c Release` completes without new trim/package errors
- [ ] All 11 FRs from `.swarm/spec.md` addressed
- [ ] SAST scan passes with no critical/high findings from changes
- [ ] No type suppressions introduced (no `#pragma warning disable`)
- [ ] Wave-level commits with clear messages

## Risk Summary

| Risk | Level | Mitigation |
|---|---|---|
| AntDesign 0.14→1.6 component API breaks | **CRITICAL** | Researcher identified all 5 breaking changes; incremental fix per component category; rollback commit tagged |
| Azure.Identity 1.21 TypeForwardedTo | **MEDIUM** | Check all `using Azure.Identity;` — may need `using Azure.Core;` additions |
| Identity.Web 3.x DI schema change | **MEDIUM** | Server Program.cs uses AddMicrosoftIdentityWebApi which is 3.x-adjacent — verify params |
| ArmClient API change | **HIGH** | GetDefaultSubscriptionAsync() → GetSubscriptionResource() — code already inspected |
| Trim compatibility | **MEDIUM** | Add TrimmerRootAssembly for AntDesign if publish warnings appear |
| gRPC package version sync | **LOW** | All Grpc.* packages bumped lockstep |
| EF Core 7→8 | **LOW** | Mostly additive changes |
| Submodule not initialized | **LOW** | `git submodule update --init` in Wave 1.1 |
