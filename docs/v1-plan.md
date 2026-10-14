# AzureDesignStudio v1 Plan

> **Date:** 2026-10-11
> **From:** v0.4 (post platform-refresh, .NET 8) → **v1.0**
> **Themes (phased):** Platform '26 → Catalog + Cost → Editor UX → Desktop → Release

## Decisions (locked)

| Decision | Choice | Rationale |
|---|---|---|
| Target framework | **.NET 10 LTS** now, before features | .NET 8 EOL 2026-11-10; SDK 10.0.401 already installed |
| Auth | **Entra ID work + personal accounts** (drop B2C) | B2C P2 retired 2026-03-15; new B2C tenants can't be created; plain Entra (`common` authority + MSA) is simpler than External ID migration |
| Diagram library | **Sequence A→B**: patch 2.1.6 fork for .NET 10 now; port to 3.x as its own milestone | Fork delta is +205/−46 (21 files, group behavior); 2.x→3.x is a 427-file rewrite that deletes `GroupingBehavior`/`DiagramOptions` — must not gate the platform wave |
| Desktop | **MAUI Blazor Hybrid** | User choice; Windows-first MSIX |
| v1 scope | Catalog + cost estimate + platform + desktop, phased | IDEA.md items 3-6 |

## Ecosystem facts (verified 2026-10-11)

- .NET 8 EOL **2026-11-10**; .NET 10 LTS supported to Nov 2028.
- Azure AD B2C: P2 discontinued 2026-03-15; end-of-sale 2025-05-01; existing tenants supported ≥ May 2030.
- Blazor.Diagrams: upstream transferred to `Blazor-Diagrams/Blazor.Diagrams` (active, 3.0.4.1, packages still net6/7). `ModelingEvolution.Blazor.Diagrams` 3.0.6 = 3.x fork with .NET 10 static-web-assets fix + NodeRenderer disposal-race fix; active Jan 2026, small maintainer.
- AntDesign 1.6.2 = latest stable (2.0 nightly-only) — current, no action.
- Azure.Bicep.Decompiler latest 0.48.1 (we're on 0.39.26).
- Photino.Blazor semi-dormant (last publish 2025-01) — not chosen.
- Catalog today: 43 resources + 6 reference architectures = 49 stencils (`ads-stencils.json`); AI-era services included (Azure OpenAI, Fabric, Container Apps).
- CI: Docker build only — no `dotnet test`; CodeQL manual-trigger.
- Tests: 9 live-Azure integration tests, 0 offline unit tests.
- PWA service worker scaffolded but unregistered; no WASM AOT; no undo/redo/minimap.

---

## Phase 0 — Platform '26 (blocks everything)

**Goal:** everything on .NET 10 LTS, Entra auth, CI with real tests.

**Status (2026-10-11): Phase 0 complete (8/8).** 0.2 verified live: base path must be `_content/Z.Blazor.Diagrams` (bare package id serves assets at `/Z.Blazor.Diagrams/*` → 404 on `script.js`, diagram JS dead, drops throw "Container not available"); after fix, drag-drop, groups, placement rules, validate + export drawers verified in headless browser. 0.4 verified: model->ARM->Bicep->`bicep build` 0.48.1 exit 0, incl. all 15 Phase 1 resource types (19-resource template). 0.5 measured: AOT publish 11m21s; wire payload 80.2 MB brotli (dotnet.native 18.7 MB br + Azure.Bicep.Types.Az 33.3 MB br); `RunAOTCompilation` gated to Release (Debug publish errors "AOT is not supported in debug configuration"). 0.7: 32 offline tests green (`--filter "Category!=LiveAzure"`); 9 live tests tagged `LiveAzure`. AOT (`0.5`) required `System.Formats.Nrbf` payload fix (Mono AOT + Windows.Compatibility chain from Bicep decompiler). 0.8: Release image starts only with `KeyVaultName` set (prod contract); Debug image (`--build-arg BuildConfiguration=Debug`) serves index/dotnet.js/diagram JS/`ads-stencils.json` on :8080 (aspnet:10.0 default port; Dockerfile `EXPOSE 80` is metadata only). .NET 10 has no `blazor.boot.json` (new JS startup).

| # | Task | Files | Acceptance |
|---|---|---|---|
| 0.1 | TFM `net8.0`→`net10.0` (7 csproj + 3 submodule csproj) | all `*.csproj` | `dotnet build` exit 0 |
| 0.2 | Diagram fork .NET 10 static-asset patch (`StaticWebAssetBasePath` = `_content/<PackageId>`) | `src/Blazor.Diagrams/src/Blazor.Diagrams/Blazor.Diagrams.csproj` | diagram CSS/JS served under .NET 10; canvas renders styled |
| 0.3 | Entra auth: client MSAL authority `common` + MSA, remove B2C policy config; server `AddMicrosoftIdentityWebApi` Entra authority; config + docs | `AzureDesignStudio.csproj` auth wiring, `Server/Program.cs`, `appsettings*`, README | login with work + personal MS account; token validates on server |
| 0.4 | Bicep.Decompiler 0.39.26→0.48.1 + round-trip re-verify | `Server csproj`, `AdsBicepDecompiler` | ARM→Bicep→`bicep build` round-trip passes |
| 0.5 | WASM AOT: `RunAOTCompilation=true`, measure startup + size, trim/AOT interplay with existing `TrimmerRootAssembly` | `AzureDesignStudio.csproj` | publish succeeds, 0 IL2xxx warnings, startup improved |
| 0.6 | CI: `actions/setup-dotnet@v4` net10, add `dotnet test` (offline subset), CodeQL on PR | `.github/workflows/build.yml` | CI green incl. test step |
| 0.7 | Offline unit tests: ARM-generation golden files, stencil JSON schema validation, DTO source-gen output | `Core.Tests` | ≥15 offline tests, no Azure creds needed |

**Effort:** 3-4 days. **Risk:** AOT+trim size regressions; MSAL `common`+MSA redirect-uri registration.

## Phase 1 — Catalog + Validation

**Goal:** ~30 resource types; validation catches real Azure constraints.

**Status (2026-10-11): Phase 1 complete.** 1.1: 15 new types shipped (33 stencils total: LB, Private Endpoint, DNS Zone, CAE, Container App, CA Jobs, Availability Set, Redis, ACR, Service Bus, Event Hubs, Event Grid, Key Vault, Azure OpenAI, Fabric capacity). 1.2: `DesignValidator` (Core/Validation) runs naming (7 types), placement (SQL DB/CA/CA Job/subnet/availability set), SKU-region (Fabric, Redis Premium), subnet delegation (CAE), required-children (empty plan/set/env) and per-node `IsValid`; wired to validate drawer (`TopMenu.OpenValidationDrawer`); 13 offline regression tests (`DesignValidatorTests`, 45 offline total). 1.3: `NewResourceTypesArmTests` pins type@apiVersion per new stencil key + CA→env link + subnet delegation ARM emission; full 19-resource template passes `bicep build` 0.48.1. Required-children warning verified live in headless browser.
- 1.1 New ARM models (`AzureResources/<svc>/`) + node models & forms (`Core/<svc>/`) + stencil entries + icons for: **Microsoft Foundry / Azure OpenAI, Container Apps (+Environment, Jobs), Service Bus, Event Hubs, Event Grid, Key Vault, Load Balancer, Private Endpoint, DNS Zone, Azure Redis, Container Registry, Fabric capacity, Availability Set** (16 new → ~30 total).
- 1.2 Validation rules engine: extend existing per-node validation with cross-node/Well-Architected checks (SKU-region availability, subnet delegation, naming rules, required child resources).
- 1.3 Export verification per new type: generated ARM passes `bicep build` / ARM schema validation in tests.
- 1.4 Catalog expansion wave 2 (2026-10-13): 16 more deployable types -> 43 resources / 49 stencils total. Networking: NAT Gateway, Traffic Manager, ExpressRoute Circuit, NSG; Compute: Container Instance, VM Scale Set; Data: Cosmos DB, AI Search, Data Factory; Others: Recovery Services Vault, Log Analytics Workspace, Logic App, Static Web App, Application Insights, App Configuration, SignalR. Per type: ARM model + node model + AntDesign form + icon + stencil entry; 16 new `NewResourceTypesArmTests` contracts (73 offline green). Verified in browser: all 16 render in panel tabs, drop-to-canvas works, node form fields bind.

**Effort:** 5-8 days (mechanical per-type via source-gen pipeline; ~2-4h/type). **Risk:** icon assets; ARM api-version choices per type.

## Phase 2 — Cost Estimate

**Goal:** monthly cost per node + design total.

- 2.1 Azure Retail Prices API client (`prices.azure.com` OData, public, no auth) — server-side proxy endpoint to avoid WASM CORS + caching.
- 2.2 Cost-map JSON: resource type + SKU → `serviceName`/`skuName`/`meterName` queries; region selector; currency display.
- 2.3 Cost panel UI (AntDesign drawer): per-node monthly, total, per-service breakdown; offline snapshot fallback when API unreachable.

**Effort:** 3-4 days. **Risk:** meter-name mapping maintenance; SKU naming drift between ARM SKUs and retail meters.

## Phase 3 — Editor UX (diagram 3.x port)

**Goal:** modern editor; submodule dies.

- 3.1 Port to `Z.Blazor.Diagrams` 3.0.4.1 NuGet (org package; verified .NET 10 static assets under `_content/Z.Blazor.Diagrams`). Remove submodule; migrate 37 consumer files (`Core.Models`/`Core`/`Geometry` surface); re-implement group behavior on 3.x (upstream deleted `GroupingBehavior` — rebuild drag-into-group/z-order from the +205/−46 fork delta).
- 3.2 Undo/redo: command-stack behavior over diagram events (library lacks it in 2.x and 3.x).
- 3.3 Minimap (3.x Navigator widget), keyboard shortcuts, copy/paste, alignment guides.

**Effort:** 5-7 days. **Risk:** group UX parity; ME fork bus factor — fallback is rebase on org 3.x (same port cost, known).

**Status (2026-10-13): 3.1 complete.** Submodule + net10 patch removed; `Z.Blazor.Diagrams` 3.0.4.1 in both csproj. Fork-only APIs live in `AdsDiagram` (DeleteSelectedModels, print capture + A4 fit, shortcut overrides) and `AzurePort`/`VirtualNetworkPort` (`CanAttachTo` bidirectional rules: source-only check in 3.x, vnet-peering pair rules). Group drag-into-group/z-order verified in headless browser (create, nest, links incl. blue peering, duplicate/self/cross-type rejects, edit form, delete, export-image preview, view-state restore).

**Status (2026-10-13): 3.2 complete.** `DiagramHistory` (Core/Common) — command stack over diagram events: node/group add/remove (incl. child snapshots), move (gesture window per pointer drag, coalesced), link add/remove, group-membership (`RecordChildMembership`; library exposes no group event). `Capture` = one user action = one command; `Suspend` = programmatic (design load/print) records nothing. Toolbar Undo/Redo + Ctrl+Z/Y/Ctrl+Alt+Z wired through `StateChanged`; subnet add/remove in `VirtualNetworkForm` records as composite. Verified headless: drop/undo/redo button states, drag-into-group undo restores position+membership, delete modal undo/redo, subnet path.

**Addendum (2026-10-14): link undo/redo defect fixed.** `DragNewLinkBehavior` fires `Links.Add` *after* the pointer-up that closed the gesture window, so `Push` silently dropped the link command outside `_gesture` — the link never entered history; Ctrl+Z was actually undoing the previous node-add (the link cascaded away with it) and Ctrl+Y re-added only the node. `Push` now flushes to the undo stack directly when no gesture is active. Verified headless: single port→port link, Ctrl+Z removes only the link, Ctrl+Y restores it; full editor suite (drop/group-drag/link/undo/redo) all-pass, 0 console errors.

**Status (2026-10-13): 3.3 complete → Phase 3 done.** `SnappingDragBehavior` (Services): edge/center alignment guides over `DragMovables` semantics, snap window widened by `GridSize` so 10px grid quantization can't strand nodes off-target; Navigator minimap via `DiagramNavigator`; shortcuts Ctrl+Z/Y/Shift+Z + Ctrl+C/V over DTO clipboard; keyboard Delete stays removed (app deletes via context-menu confirm dialog, as in the fork). Verified headless on NuGet build: drop x3, snap-aligned drag (exact X/Y, guides drawn+cleared), drag-into-group, click-select in group, copy/paste, undo/redo (paste round-trip), port→port link commit (`diagram-link attached`; node/body drops correctly rejected by `CanAttachTo`), minimap canvas present, no circuit errors.

## Phase 4 — Desktop (MAUI Blazor Hybrid)

**Goal:** installable Windows app sharing the component stack.

- 4.1 `AzureDesignStudio.Desktop` MAUI project: `BlazorWebView` hosting Core components; JS interop paths + static assets verified under MAUI. **Done 2026-10-13:** root component reuses shared `App.razor` (telemetry gated via `EnableTelemetry=false`); WebView virtual host `https://0.0.0.1/` is JS-only, so host-side `HttpClient` named clients get a `PackageAssetHandler` serving packaged `wwwroot`; `NavigationManager` is circuit-scoped and late — client factories must not resolve it (stencils load from package files with web JSON defaults); PWA service-worker registration guarded by `AdsHostSupportsServiceWorker`. Verified: full boot with zero console/network errors, catalog icons+labels, WASM regression clean.
- 4.2 Auth: MSAL device broker / web account (work + personal); no B2C. **Done 2026-10-13:** `MsalAccountService` (public client, WAM broker via `BrokerExtension.WithBroker`, device-code fallback surfacing code via window title + clipboard, silent acquisition for gRPC via `MsalAuthTokenHandler` on Design/Deploy clients); state via `DesktopAuthenticationStateProvider` mirroring the account cache; shared drawers route through the `AdsHostAuth` host seam (WASM keeps redirect flow). Verified end-to-end in-app: sign-in click -> broker -> device-code fallback -> live STS response surfaced gracefully, zero WebView errors. Completing a sign-in needs a real app registration + the client id/scope filled into Desktop `appsettings.json` (`AzureAd:ClientId`, `ServerScope`); unconfigured state degrades to anonymous like unauthenticated WASM.
- 4.3 gRPC: native `Grpc.Net.Client` (drop `Grpc.Net.Client.Web`); server URL config + offline design cache (local save, sync online). **Done 2026-10-13:** native clients live since 4.1 with bearer handler; `ServerUrl` config; `FileSystemDesignOfflineStore` (AppData `designs/*.adsdesign.json`, atomic writes) behind an `IDesignOfflineStore` seam the shared `DesignGrpcService` resolves via `IEnumerable<>` (WASM unregistered -> behavior unchanged, transport errors still propagate). Save/list/load/delete fall back to the cache on `RpcException Unavailable`/`HttpRequestException`; a successful server call flushes pending saves; local pending wins on load; honest UI toast differentiates cached vs server save. Verified with throwaway E2E against a dead-port gRPC client: save->200+stored, list->local names, pending-wins load, missing->404, delete->removed, no-store path still throws; desktop launch + save drawer auth gate intact, no new crash entries.
- 4.4 MSIX packaging + publish workflow. **Done 2026-10-13:** real identity in `Package.appxmanifest` (`AzureDesignStudio.Desktop` / `CN=Azure Design Studio` / 1.0.0.0, DisplayName "Azure Design Studio"); `dotnet publish -p:WindowsPackageType=MSIX -p:WindowsAppSDKSelfContained=true` produces `AppPackages/AzureDesignStudio.Desktop_1.0.0.0_Test/…_x64.msix` (276 MB, self-contained; verified contents: exe, appsettings.json, full `wwwroot/_framework` WASM payload, WinAppSDK deployment extensions; warnings only - PRI263 neutral-resource noise for satellite .wasm assets + no symbols without VS BuildTools). `.github/workflows/publish.yml`: WASM site zip + Docker image (push gated on `vars.REGISTRY`) + MSIX (version stamped from `v*` tag, optional signtool signing via `MSIX_PFX_*` secrets, unsigned upload otherwise) + GitHub Release on tags.

**Effort:** 6-9 days. **Risk:** AntDesign + BlazorWebView asset/JS friction; component stack currently assumes WASM host (`NavigationManager`, `localStorage`) — audit needed.

## Phase 5 — v1 Release

- 5.1 `version.json` 0.4 -> 1.0; CHANGELOG; README/docs refresh. **Done 2026-10-13:** `src/version.json` 1.0; `CHANGELOG.md` 1.0.0 entry; `docs/entra-setup.md` (app-registration + desktop build + MSIX + offline cache guide); README: 8 projects, .NET 10, desktop, test tiers (offline gate vs `Category=LiveAzure` + quota caveat), doc links; AGENTS.md commands.
- 5.2 Publish: WASM site, Docker image, MSIX. **Workflow live** (`publish.yml`, tag/manual). Local validation: MSIX job command proven (artifact above); WASM publish + Docker build steps run on CI; registry push requires configuring `vars.REGISTRY` + registry secrets once (first-time infra step, repo settings).
- 5.3 Gates: **offline tests green in CI** (`build.yml`: Server+WASM+Tests Release builds + `--filter "Category!=LiveAzure"`; proven locally - 3 projects 0 errors, 73/73). MAUI project excluded from ubuntu job (Windows TFM; SDK 10.0.401 slnf parsing also broken on Linux -> explicit project set). Live-Azure suite documented (README Test Tiers); container smoke runs in CI Docker step.

**Effort:** 1-2 days.

---

## Totals & sequencing

| Phase | Effort | Depends on |
|---|---|---|
| 0 Platform '26 | 3-4 d | — |
| 1 Catalog + Validation | 5-8 d | 0 |
| 2 Cost Estimate | 3-4 d | 1 (types to price) |
| 3 Editor UX | 5-7 d | 0 (parallel-able with 1/2) |
| 4 Desktop | 6-9 d | 0, 3 (library settled) |
| 5 Release | 1-2 d | all |
| **Total** | **~23-34 days** | |

## Top risks

1. **MAUI hybrid host assumptions** — biggest unknown; spike 4.1 early (1-day feasibility probe after Phase 0).
2. **3.x group reimplementation** — fork delta is small but load-bearing; keep fork branch until parity proven.
3. **Retail-price meter mapping** — start with top-10 SKUs, mark unmapped as "estimate unavailable", never show wrong numbers silently.
4. **ME fork longevity** — re-evaluate at Phase 3 start; org release preferred if .NET 10-ready.

## Handoff

v1 closed 2026-10-13 (Phases 0-5 all done). Continuation: `docs/v2-plan.md` (Phases 6-13 - live tenant discovery, topology, change intelligence, IaC breadth, safe execution, stacks, decommission), sequenced per `docs/top-100-improvements.md` section 3.
