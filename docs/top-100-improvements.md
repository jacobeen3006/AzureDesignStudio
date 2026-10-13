# Azure Design Studio — Top 100 Improvements & Fixes

> **Date:** 2026-10-13 · **Scope:** desktop app → tenant-wide Azure analysis + visualized deploy / change / decommission
> **Status of the app today:** design-time only. 33 stencils (`ads-stencils.json`), ARM/Bicep export, validation, monthly cost estimate (`RetailPriceService`), deploy via `DeployService` (ARM REST, RG-scoped), gRPC + EF Core storage. **Zero** live-Azure read path: no inventory, no import, no drift, no decommission.
> **Not duplicated by the running plan:** `.swarm/plan.md` (platform refresh: .NET 8, packages, auth, AntDesign, Docker) and `docs/v1-plan.md` Phases 0–5 (catalog, cost, editor UX/undo-redo/minimap, MAUI desktop, release). Items already covered there are listed in §Appendix A and deliberately **not** numbered here.

---

## 1. The landscape — what the competition actually does

| Tool | Live cloud read | Design/drag-drop | IaC out | Drift | Cost | Notable weakness (our opening) |
|---|---|---|---|---|---|---|
| **Eraser** | Yes — AWS/Azure/GCP org+tenant, builds a resource+relationship inventory; reachability/IAM queries | Yes (canvas + diagram-as-code) | **No IaC export** | Rescan, edits independent of refresh | **None** | No cost at all; no IaC generation — diagrams only |
| **Cloudcraft (Datadog)** | Yes, AWS+Azure, tag/type/region filters, group-by presets | Yes | **Terraform export removed** | Editing a live diagram converts it to a static snapshot; version history doesn't diff | Yes (list price) | No tenant-level connection; editing kills live-ness; no AI generation |
| **Lucid Cloud Accelerator (ex-Lucidscale)** | Yes, all 3 clouds incl. org/tenant | Yes (inside Lucidchart) | Terraform gen in beta; no IaC import | Scheduled refresh + Version Compare vs *last saved only* | Yes (AWS list price) | Enterprise sales only; one cloud frame per document; no Azure/GCP prompt agent; no draw.io/PPT export |
| **Hava** | Yes, AWS/Azure/GCP; **does not support Azure tenant scope** | **No — explicitly not a drag-and-drop tool** | **No** | Daily sync, compare **any two** revisions, email diff alerts | Yes, per resource + per env | Can't design; can't generate code; no MCP/agent access |
| **Brainboard** | Reverse-engineers existing architectures | Yes, collaborative | **Terraform only — no Bicep** | TF-state drift, scheduled cron scans | Infracost | No ARM/Bicep; Azure is second-class |
| **Azure Resource Inventory (ARI, Microsoft)** | Yes, tenant/MG/sub/RG | No | No | No | Optional (Az.CostManagement) | Excel + draw.io XML only; read-only documentation tool; PS7 required, no auto module upgrade |
| **Azure Tenant Insights (ATI, OSS)** | Yes, 100% read-only, tenant→MG→sub→RG, WAF/CAF/Zero-Trust framing | No | No | Point-in-time snapshots only | Yes (needs Cost Management Reader) | No design, no deploy, no IaC; runtime 15–60 min for 5k–50k resources; Foundry agent configs invisible via Resource Graph |
| **TerraVision / Azure Resource Visualizer / azure-diagram-mcp** | From Terraform / live RG | No | No (diagram-only) | No | No | Diagram generators, not platforms |

### The wedge (nobody occupies it)

**Live tenant truth + editable design + safe execution in one desktop app, with ARM/Bicep as the first-class output language.**

- Every live-scan tool (Hava, Cloudcraft-live, ARI, ATI) is **read-only or documentation-only** — none generate deployable code you can trust.
- Every IaC designer (Brainboard, Cloudcraft-standalone) either **dropped code generation** or **doesn't support Bicep**.
- Nobody does the **design ↔ live round trip** with an explicit per-resource reconcile (adopt / update / detach / delete).
- Nobody wraps **Azure Deployment Stacks** (managed-resource lists, `denySettings`, `actionOnUnmanage`) in a visual app — and Microsoft hasn't either: the portal **still can't create or update a deployment stack** ("This feature isn't implemented at this time" in the current docs).
- Nobody offers **decommissioning** as a first-class, dependency-aware, reversible workflow.

That last row is the product. "Analyze everything, change anything safely, retire anything cleanly."

---

## 2. The 100

Tags: **[Feature]** new capability · **[Fix]** defect/gap in existing code · **[UX]** · **[Perf]** · **[Infra]**
Effort: **S** ≤1 day · **M** 2–5 days · **L** ≥1 week. Impact: **P0** do now · **P1** next · **P2** later.

### A. Tenant discovery & inventory (1–12)

1. **[Feature][L][P0] Read-only tenant onboarding.** Connect with Reader at management-group or tenant scope; discovery must work with **zero write permissions**. Show exactly which role unlocks which feature before the first scan (ATI publishes this matrix; copy it and improve the UX). Inspiration: ARI, ATI.
2. **[Feature][M][P0] Azure Resource Graph as the query spine.** One local KQL-capable query engine over all subscriptions, paginated at **1,000 rows/page**, honoring **429 `Retry-After`** with bounded exponential backoff. Verified limitation of the whole market (ATI documents it explicitly). This single component powers inventory, drift, and cost reconcililation.
3. **[Feature][M][P0] Scope tree: tenant → management groups → subscriptions → resource groups.** Enrolled vs unenrolled subscriptions, MG hierarchy, policy/RBAC inheritance at each level. Inspiration: ARI/ATI. This is also the natural navigation chrome for the desktop app.
4. **[Feature][M][P0] Generic, typeless inventory layer.** Auto-discover *any* resource type and render it generically (raw ARM JSON + enriched properties) — new Azure services get full inventory support with **no code change**, while the design layer stays typed. ATI's "dynamic resource-type coverage" is the right architecture; ADS's per-type model classes are the bottleneck.
5. **[Feature][S][P1] Tag & owner harvesting.** Owner/cost-center/env/app tags promoted to first-class facets; "untagged resources" and "orphaned-cost" views. Feeds ownership, chargeback, and decommission notification.
6. **[Feature][M][P1] Retirement/deprecation watch.** Local catalog mapping type + API version → deprecation/retirement status, with **catalog freshness tracking** (`current` / `review_due` / `stale`) and a visible "data as of" date. Inspiration: ATI's `deprecated_types.json` + monthly review workflow.
7. **[Feature][M][P0] Orphan & idle detection.** Unattached disks, unassociated public IPs, orphaned NICs/NSGs/route tables, empty App Service Plans, dangling DNS records, VNet peerings to deleted VNets, unused load balancers. This is the highest-value read-only output for most tenants. Inspiration: ATI network orphan detection, azqr, Azure Orphaned Resources.
8. **[Feature][L][P0] Import live resources into a design ("adopt").** Select scope → generate an editable ADS diagram **and** the matching ARM/Bicep. This is the missing half of the product and the one thing no competitor does with Bicep. Closest analogue: `aztfexport` (Terraform only, verified — generates config + state, explicitly *not* guaranteed comprehensive).
9. **[Feature][S][P1] Sovereign clouds.** AzurePublic / Government / China / (legacy Germany) endpoint sets as a scope setting. ATI supports all four; ADS hardcodes public ARM endpoints.
10. **[Feature][M][P0] Local snapshot store (SQLite).** Every scan persisted with timestamp, scope, and content hash. Turns point-in-time scans into **trend history** — ATI's biggest admitted limitation ("produces snapshots; trend analysis requires scheduling regular runs").
11. **[Feature][M][P1] Scan engine: incremental, parallel, resumable.** Per-collector parallelism, progress + cancel, checkpointing, and a scan manifest (scope, counts, duration, partial failures) written next to the snapshot for auditability.
12. **[Feature][S][P0] Permission preflight & graceful degradation.** Probe each data source (Resource Graph, Advisor, Policy, Resource Health, Defender, Cost) and disable precisely what's unavailable with an explanation, instead of failing the whole scan. Verified: ATI isolates collectors this way; Cost needs `Cost Management Reader`, *above* Reader.

### B. Live topology graph & visualization (13–25)

13. **[Feature][L][P0] Relationship derivation from real configuration.** Edges computed from VNet peerings, private endpoints → Private DNS zones, subnet delegations, NSG/UDR references, app-setting and connection-string references, managed-identity and RBAC grants, diagnostic-settings targets. Inspiration: Eraser (relationships from routing/SG/IAM/config) and Azure Resource Visualizer (Mermaid from a live RG). Config-derived edges are what make a topology diagram *true* rather than decorative.
14. **[Feature][M][P0] Canonical nested framing.** Tenant → MG → subscription → RG → region → VNet/subnet/zone frames, with auto-layout that respects the nesting (the shapes ADS already has for groups). Inspiration: ARI's Organization/Network/Subscription diagram set.
15. **[Feature][S][P1] View templates.** Infrastructure / Network / Security / Cost / Identity / Changes — switchable overlays on one graph. Inspiration: Lucid's view templates + Hava's Infrastructure/Extended/List/Security views.
16. **[Feature][L][P0] Reachability & access queries.** Natural-language-ish questions answered from the graph: *"what can reach this database?"*, *"which principals can read this storage account?"*, *"is anything public-facing in front of this VM?"*. Eraser ships this; no Azure desktop tool does.
17. **[Feature][S][P0] Scope filters with saved views.** Filter by type/tag/region/RG/owner; save as named scopes; scopes are reusable as deploy and decommission targets.
18. **[Feature][L][P1] Two-plane canvas: live plane + intent plane.** Live resources are read-only-ish (refreshed); planned/edited resources live on a separate plane, so refresh never destroys edits. Verified competitor failure mode: Cloudcraft **converts a live diagram to a static snapshot on first edit** and discards edits when switching back; Eraser keeps edits independent of refresh. Do what Eraser does.
19. **[Feature][M][P1] Diagram-as-code export/import.** Export `.drawio` XML, Mermaid, and SVG/PNG; **import** `.drawio`. Round-trippable text beats a proprietary canvas for review and long-term storage. Inspiration: ATI/ARI emit draw.io; TerraVision/Terraform-style graph input.
20. **[Feature][S][P1] Icon pipeline with generic fallback.** Bundle the official Azure architecture icon set, map type→icon from config, and fall back to a categorized generic icon for unknown types so **no resource ever renders as a blank box**. Verified pattern: ATI's "config-driven icon map with generic fallback".
21. **[Feature][M][P0] Identity-stable layout.** Node identity = ARM resource ID; reuse last position on rescan so a diff highlights *change*, not shuffling. Precondition for usable visual diffing.
22. **[Feature][S][P1] Aggregation for scale.** Collapse N identical resources into one "×12 VMs" node with drill-down; expand on demand.
23. **[Feature][S][P0] Resource inspector.** Per-node properties, raw ARM JSON, tags, RBAC, activity history, and a deep link to the portal. Inspiration: ARI's hover-to-reveal details.
24. **[Feature][M][P1] Diagram version history with arbitrary comparison.** Save named visual snapshots; compare **any two**. Verified competitor bar: Hava compares any two revisions, Lucid only compares to the last saved — beat Hava's model, not Lucid's.
25. **[Feature][M][P1] Shareable static report bundle.** Self-contained HTML + PNG + JSON, no login, works offline — for design reviews and audit packs. Inspired by Hava's embeddable iFrames, but local-first and honest about being a snapshot.

### C. Drift, diff & change intelligence (26–36)

26. **[Feature][M][P0] Change feed.** Query Resource Graph change history (+ Activity Log) on a schedule, producing a normalized timeline: what changed, when, by which principal. Foundation for drift, audit, and cost attribution.
27. **[Feature][M][P0] Drift detection, three-way.** (a) last scan vs now, (b) **design/IaC vs live** (Bicep/ARM/TF state), (c) design vs design across environments. Inspiration: Brainboard's scheduled TF-state drift with cron expressions — but ours runs locally and covers Bicep.
28. **[Feature][M][P0] Visual + structural diff.** Side-by-side diagram diff (added/removed/modified highlighted) **plus** a property-level tree diff. Inspiration: Hava's email-with-diff-diagram and compare-revisions.
29. **[Feature][M][P1] Attribution.** Correlate each change to caller (user/SP/pipeline), source IP, correlation ID, and originating deployment — the audit answer to "who deleted that?"
30. **[Feature][S][P1] "Explain this change" narration.** Deterministic facts from the diff, LLM narration on top, with every claim traceable to a property. Never let the model invent an attribution.
31. **[Feature][M][P1] Baseline/gold-standard comparison.** Declare expected state (from a design or an export), then report tenant-vs-baseline conformance per scope. This is the enterprise governance hook.
32. **[Feature][S][P1] Tenant bill-of-materials with content hashing.** Export the full inventory as deterministic JSON (stable ordering + hashes) so customers can diff tenant state in git and store it as evidence.
33. **[Feature][M][P0] Unmanaged-resource detection.** Which live resources are in **no** design and **no** deployment stack? Complements the stack's managed-resource list (verified: stacks maintain exactly that list) and is the entry point to the decommission flow.
34. **[Feature][S][P2] Resiliency matrix.** Regional distribution + multi-zone signals per type, flagging single-region/single-zone services. Verified caveat to state in the UI: this is property-based, **not** workload-level backup validation.
35. **[Feature][S][P2] Window correlation.** "What changed during the incident window / since this ticket?" — a date-range filter over the change feed for postmortems.
36. **[Feature][S][P0] Do-not-touch guardrails.** A configurable list of scopes, tags, and resource types that every diff/view/deploy/decommission action must respect and visibly flag. Prerequisite for shipping anything that can delete.

### D. Design & IaC authoring (37–49)

37. **[Feature][L][P0] Dual IaC output: keep ARM/Bicep first-class, add Terraform.** Verified market gap: Brainboard is Terraform-only with **no Bicep**; Cloudcraft **removed** Terraform export; Hava has none; Eraser has none. Emit Terraform via the AzAPI provider for fidelity, mirroring `aztfexport`'s verified approach (azurerm *and* azapi).
38. **[Feature][M][P1] Both directions of the ARM⇄Bicep round trip in-app.** Decompile Bicep→ARM (exists via `Azure.Bicep.Decompiler`) plus ARM→Bicep, then validate generated output in-process (the `bicep build` equivalent) before the user ever sees it.
39. **[Feature][L][P0] Generate IaC from live resources ("aztfexport for Bicep").** Per-resource config generation for the top ~30 types, with an explicit "generated config is a starting point, not a guarantee" notice. Verified: `aztfexport` itself warns its output "is NOT meant to be comprehensive".
40. **[Feature][M][P1] Azure Verified Modules as a generation target.** Toggle between raw resources and `br/public:avm/...` module references. Reviewers accept AVM; hand-rolled resource JSON is what makes generated IaC untrusted.
41. **[Feature][M][P0] Proper parameterization.** Typed parameters, `.bicepparam` files, per-environment parameter sets, deploy-time prompts with validation, and Key Vault references for secrets — never plaintext secrets in a generated template.
42. **[Feature][M][P1] Environment overlays.** Dev/test/prod parameter+SKU overlays of one design, with an environment-diff view (cost, SKU, redundancy, location).
43. **[Feature][M][P1] Naming engine.** Cloud Adoption Framework naming rules per type, uniqueness constraints (storage/ACR/KV/key vault name rules), and **live availability checks** for globally-unique names before deploy.
44. **[Feature][L][P1] Design-time guardrails: Well-Architected + Policy compatibility.** Extend `DesignValidator` to WAF pillars and add "would Azure Policy deny this at the target scope?" checks. Inspiration: Brainboard embeds Checkov/tfsec/Terrascan/OPA at design time; the Azure-native analogue is Policy + WAF, which no competitor does.
45. **[Feature][M][P1] Reference-architecture library.** Curated, parameterized starting points (hub-spoke landing zone, app service baseline, AKS baseline, data platform, AI Foundry baseline) built from the stencil catalog.
46. **[Feature][M][P1] Reusable modules/components.** Group nodes (subnet+NSG+UDR, app+plan+slot) into a parameterized unit that can be stamped out repeatedly and versioned — Brainboard's template/module reuse, expressed as Bicep modules.
47. **[Feature][M][P2] Design-level HA/cost lint.** "Single point of failure", "no backup configured", "no zone redundancy", "public by default" warnings evaluated at design time, not discovered in production.
48. **[Feature][M][P1] Text-first design files.** `.ads.json` (or Bicep) as the source of truth, canvas as a view — so architecture changes are reviewable in pull requests and diffable in git. Enables everything in §C.
49. **[Feature][M][P2] Cross-design composition.** Link designs that share resources (shared VNet, Key Vault, Log Analytics workspace) and build a global dependency inventory so nothing gets deleted out from under another workload.

### E. Deployment & change execution (50–62)

50. **[Feature][L][P0] What-if as mandatory preflight.** Run the ARM what-if operation before every apply and render its real change types — **Create, Modify, Delete, Ignore, NoChange, NoEffect, Deploy** — using `FullResourcePayloads` for property-level detail. Verified limitations to surface in the UI: max 500 nested templates, max 800 resource groups, 5-minute expansion, `reference()` cannot be resolved (its properties always report as changed), and write access is required.
51. **[Feature][M][P0] Multi-scope deployment.** Resource group, subscription, management group, and tenant scopes. Verified: deployment stacks support all four; today's `DeployService` is RG-oriented.
52. **[Feature][M][P1] Dependency-ordered orchestration.** Topological apply with parallelism across independent resources, retry/backoff, and a per-resource state machine the user can watch.
53. **[Feature][M][P0] Plan → review → apply approval flow.** A change plan is a first-class artifact: rendered as a diff + diagram overlay, approvable by a named principal, then applied with the approved plan hash recorded.
54. **[Feature][L][P1] Policy-aware preflight.** Evaluate the effective Azure Policy assignments at the target scope (deny/audit/modify effects, exemptions) and name the exact definition that would block the deploy — before ARM fails. No competitor does this per-plan.
55. **[Feature][M][P1] Capacity & quota preflight.** Regional vCPU quotas, SKU availability by region/zone, and known capacity restrictions checked against the plan, so "quota exceeded" never happens mid-deploy. Inspiration: ARI's VM quota/vCPU/memory enrichment.
56. **[Feature][M][P1] Real-time deployment monitor.** Stream deployment operations per resource, extract error code + message + likely cause, and offer targeted retry for the failed subset only.
57. **[Feature][L][P1] Pre-deploy snapshot + inverse plan.** Capture prior state (or `FullResourcePayloads` from what-if) and generate the revert plan; for stacks this maps cleanly to `detach` vs `delete`.
58. **[Feature][L][P0] Reconcile: design ↔ live, per resource.** Adopt (import live into design), Update (design wins), Detach (stack stops managing it), Delete (retire). This is the app's signature interaction and no competitor has it for Azure with Bicep.
59. **[Feature][M][P2] Change windows & scheduling.** Local scheduler plus optional pipeline dispatch (GitHub Actions / Azure DevOps) so applies run through existing governance.
60. **[Feature][L][P0] Deployment Stacks integration.** Create/update stacks with verified semantics: `actionOnUnmanage` = `deleteAll` | `deleteResources` | `detachAll`; `actionOnUnmanage.resourcesWithoutDeleteSupport` = `detach` | `fail`; `denySettings.mode` = `denyDelete` | `denyWriteAndDelete` | `none`, plus `applyToChildScopes` and up to 5 `excludedPrincipals`. **The Azure portal does not implement stack create/update** — a visual stack manager is genuinely ahead of the portal today.
61. **[Feature][M][P0] Stack health & out-of-sync guard.** Surface the managed-resource list vs template drift and the `stack-out-of-sync` condition; expose `BypassStackOutOfSyncError` only after an explicit review screen (Microsoft's own guidance is to redeploy the same template/parameters first and only then bypass).
62. **[Feature][M][P1] Execution audit record.** Every apply writes an immutable local record: template hash, parameters hash, principal, what-if result, stack settings, outcome, timestamps — exportable to git or Log Analytics.

### F. Decommissioning & lifecycle (63–72)

63. **[Feature][L][P0] Decommission planner.** Select resources / RG / subscription → compute the dependency subgraph → list blockers (resource locks, RBAC assignments, diagnostic-settings targets, DNS links, peerings, shared parents, backup vault items) → produce a staged deletion order. No product in this landscape does this; ATI stops at orphan detection.
64. **[Feature][M][P0] Dependency-safe delete.** Refuse to delete (or hard-warn) when the target is referenced elsewhere — shared VNet, Log Analytics workspace, Key Vault, ACR — using the live graph plus cross-design composition (49).
65. **[Feature][M][P0] Delete-impact preview.** Preview the exact delete set (what-if with the resources removed from the stack) and enumerate irreversible consequences: Key Vault soft-delete/purge protection, storage immutability/legal hold, ACR images, backup items, DNS records, RBAC, locks.
66. **[Feature][M][P1] Retain-before-retire.** Detect resources with no backup/export coverage and offer "export ARM template + configuration + diagnostics" before any delete. Decommissioning without an evidence copy is how data gets lost.
67. **[Feature][M][P1] Idle-cost ranking.** Rank retire candidates by 30/90-day metrics (no traffic, no requests, unattached) and $ saving, so the savings case is quantified per candidate.
68. **[Feature][S][P1] Protection manager.** Apply `CanNotDelete` locks or stack `denySettings` before a change window and remove them after — the safety belt for anything marked critical.
69. **[Feature][M][P1] Runbook-as-code generation.** Emit the decommission as reviewable Bicep/CLI/PowerShell plus a human checklist (owner notification, CMDB record, monitoring teardown), so destruction is peer-reviewed in git, not clicked.
70. **[Feature][S][P2] Owner notification.** Generate ready-to-send email/Teams messages from owner tags + inventory.
71. **[Feature][M][P1] Two-stage destroy.** Default to `detach` (stacks support it) with a T+N purge job, giving a real undo window on an operation Azure makes irreversible.
72. **[Feature][M][P2] Retirement calendar.** Deprecated API versions, retiring SKUs/services, and classic-resource migrations turned into dated migration tasks with effort estimates — using the catalog from item 6, but actioned.

### G. Cost & FinOps (73–80)

73. **[Feature][L][P0] Actual spend, not just list price.** Pull real cost via the Cost Management APIs across subscriptions/billing scopes (requires `Cost Management Reader`, which is above Reader — state that in the permission preflight), joined to graph nodes; keep the existing retail-price estimate as a fallback and mark which is which. Inspiration: ATI's cost collector + live retail API with offline fallback.
74. **[Feature][S][P1] Cost by dimension.** Cost per RG / tag / owner / workload / subscription, not only per node.
75. **[Feature][M][P1] Cost-change attribution.** Month-over-month per-resource deltas, correlated with the change feed (item 26) — "this got expensive because X was deployed". This is the killer FinOps view and it depends on items 10, 26.
76. **[Feature][M][P1] Savings backlog.** One prioritized list: orphaned/idle resources (7), Advisor right-size recommendations, reservation/savings-plan coverage gaps — each with $ and effort.
77. **[Feature][S][P2] Chargeback/showback export.** Cost-per-owner/workload digest (Excel/CSV) with an explicit "untagged spend" pile so the number is never silently wrong.
78. **[Feature][S][P1] Budget-aware design.** Show monthly cost per design; flag designs over a budget; show per-environment cost deltas before deploying.
79. **[Feature][M][P1] Pre-deploy cost delta.** What will this change plan cost per month, and what does the decommission plan save per month — the business case attached to the diff.
80. **[Feature][S][P0] Price provenance everywhere.** Every cost figure carries region, currency, source (actuals vs retail), and an "as of" timestamp; offline snapshots warn when stale. Verified: stale offline prices are an admitted limitation of the reference implementation.

### H. Security, governance & compliance (81–88)

81. **[Feature][M][P1] Posture overlay.** Defender for Cloud secure score and per-resource recommendations mapped onto diagram nodes. Inspiration: ATI's Defender posture collector + Defender asset inventory.
82. **[Feature][M][P1] Policy compliance overlay.** Non-compliant resources highlighted on the graph with policy definition/assignment/effect, exemption state, and remediation link.
83. **[Feature][M][P1] Built-in best-practice scanner.** Either implement azqr-equivalent rules or drive `azqr` through the Azure MCP server's compliance tool; either way, findings render per resource on the graph and roll up to a score. Inspiration: `Azure/azqr` + its Azure MCP Server exposure.
84. **[Feature][M][P0] Exposure analysis.** Public IPs, open/over-permissive NSG rules, storage/ACR/KV/app endpoints with public network access enabled, private-endpoint coverage gaps, and "should be private but isn't" flags. Highest-adoption read-only output after orphan detection.
85. **[Feature][L][P1] Identity & access map.** Role assignments per scope, over-privileged or high-privilege principals (Owner/UAA at broad scopes), managed identities and the resources they reach. Azure analogue of Eraser's IAM/access queries.
86. **[Feature][S][P1] Secret & certificate hygiene.** Key Vault key/secret/certificate expiry watch, unused secrets, expiring TLS certs and app-service cert bindings. Inspiration: the Microsoft `azure-compliance` skill's Key Vault expiry checks.
87. **[Feature][M][P1] Framework mapping + audit export.** Map findings to a chosen framework (Azure Security Benchmark / CIS / WAF pillars / CAF landing-zone principles) and export a two-audience pack — executive summary + technical findings. Verified model: ATI's Executive + Technical dual HTML reports.
88. **[Feature][S][P2] Data-holding resource inventory.** Which resources hold data (SQL/Cosmos/Storage/Fabric) and whether encryption at rest with CMK, private-only access, and soft-delete are enabled — feeds retention requirements (66).

### I. Desktop app platform & offline (89–96)

89. **[Feature][M][P0] Local credential chain.** Azure CLI (`az login`) reuse first, then managed identity, then service principal, then device code — mirroring what the server already does (`DefaultAzureCredential` in `Server/Program.cs`, tests use `AzureCliCredential`). Tokens and any secrets go to DPAPI/Windows Credential Manager, never plaintext config.
90. **[Feature][L][P0] Offline-first local store.** SQLite for designs, scans, snapshots, cost cache, plans, and audit records; everything works with the network down; sync when it returns. Extends the v1 plan's offline design cache to inventory too.
91. **[Feature][M][P0] Non-blocking long scans.** Background runner with progress, pause/resume, throttling control (the `--throttle-delay` analogue for a throttled tenant), and crash-safe checkpoints. Verified scale reference: 5k–50k resources takes 15–60 minutes in the reference implementation — the UI must stay usable throughout.
92. **[Perf][L][P1] Big-tenant rendering.** Virtualized canvas, level-of-detail drawing, server-side aggregation in native AOT, incremental layout; explicit target of 10,000+ nodes without freezing. Both reference implementations sidestep this (Excel sheets / read-only reports) — solving it is a differentiator.
93. **[Feature][L][P1] Local MCP server for the app.** Expose inventory query, diff, cost, what-if, plan, and decommission capabilities as MCP tools so Copilot/Claude/Hermes can drive ADS programmatically. Verified market gap: Hava has **no MCP/agent access**; Eraser does. Being the Azure "brain" for agents is a durable position.
94. **[Infra][M][P1] Signed, auto-updating distribution.** Code-signed MSIX with a real update channel, plus an offline installer; telemetry off by default (this app touches production tenants).
95. **[UX][S][P2] Multi-tenant / multi-window workspace.** Tabs or windows per tenant/design; restore workspace state per tenant.
96. **[Feature][M][P2] Portable workspace bundle.** Export/import the entire workspace (designs, snapshots, catalog, config) as a signed archive for air-gapped and regulated customers. Verified demand signal: Hava sells self-hosted specifically for this.

### J. Quality, testing & release (97–100)

97. **[Fix][M][P0] Close the silent export failures.** Every stencil key must round-trip to ARM/Bicep in a golden-file test, and **no `NotImplementedException` may be reachable from an export path**. Concrete live defect: `src/AzureDesignStudio.Core/Network/VirtualNetworkPeeringModel.cs:37-45` throws in both `GetArmParameters()` and `GetArmResources()` — a VNet peering can be drawn and configured but cannot be exported or deployed. Related risk: the `NotImplementedException` default arms in `DataModelFactory.cs:75`, `SourceGeneration/Templates/DataModelFactory.scriban:27,39`, `ResourceBaseJsonConverter.cs:10`, and the `_ => throw` arms in `WebAppModel.cs`, `FunctionAppModel.cs` — an unknown/stale key crashes instead of degrading.
98. **[Infra][M][P1] Nightly live-Azure CI suite.** Create → assert → delete in a tag-scoped sandbox RG; change paths get what-if-only tests so destructive coverage never runs against shared scopes.
99. **[Infra][M][P1] Failure-mode and safety test suite.** Throttling (429/`Retry-After`), partial permissions, huge tenants, offline mode, malformed input, and — critically — dry-run assertions that **no delete API call is ever issued without an approved plan hash**.
100. **[Feature][S][P0] Docs that make it usable in 5 minutes.** "Connect your tenant" guide with a permission matrix (role → unlocked features), a desktop build guide, ADRs for the big decisions (query spine, two-plane canvas, execution safety), and a per-release changelog.

---

## 3. Sequencing

**First 10 (the spine — everything else depends on it):** 1, 2, 3, 4, 10, 11, 12 (discovery), 13, 14 (topology), 90 (local store).
**Then the read-only value (shippable, no write risk):** 6, 7, 23, 26, 28, 33, 74, 80, 84, 87.
**Then the differentiators:** 8 (adopt), 39 (Bicep export from live), 50 (what-if preflight), 53 (approval flow), 58 (reconcile), 60 (deployment stacks).
**Then destruction, last and carefully:** 36 → 63 → 64 → 65 → 66 → 71 → 62.

Ship read-only depth before anything that can delete. Trust is the product's currency, and every competitor in this space is read-only for exactly that reason.

## 4. What NOT to build

- **Multi-cloud (AWS/GCP)** — every incumbent is stronger there and thinner on Azure. Own Azure completely.
- **Real-time collaboration / CRDT editing** — Brainboard/Lucid territory; a desktop single-user tool wins on depth, not concurrency.
- **A general diagramming tool** — Lucidchart and draw.io already won; export to `.drawio` and be done (19).
- **Its own policy engine** — Azure Policy exists, is evaluated by ARM, and is the authority. Read it (44, 54, 82); don't reimplement it.
- **In-browser/WASM rewrite of the analysis engine** — scans need streaming, retries, and hours-long runs; keep that in the desktop/native process (91, 92).

## 5. Appendix A — Already on your roadmap (excluded from the 100)

Listed so nothing above reads as an omission. These are covered by the running plans and are **not** counted:

| Already planned | Where |
|---|---|
| .NET 8→10 TFM, package bumps (AntDesign, gRPC, EF Core, Azure SDK, Identity.Web), ArmClient API migration, Bicep decompiler bump, Dockerfile, CI | `.swarm/plan.md`, `docs/v1-plan.md` Phase 0 |
| Entra ID auth replacing B2C | Phase 0.3 |
| Diagram fork patch → 3.x port; undo/redo; minimap; keyboard shortcuts; copy/paste; alignment guides | Phase 3 |
| Catalog expansion (~30 types → **done**, 33 stencils) and the `DesignValidator` rules engine (**done**) | Phase 1 |
| Azure Retail Prices API cost estimate (**done** — `RetailPriceService`) | Phase 2 |
| MAUI Blazor Hybrid desktop host, MSIX packaging, offline design cache, native gRPC | Phase 4 |
| Versioning, CHANGELOG, publish gates | Phase 5 |

**Interaction note:** items 37–39 give *concrete* scope to "Terraform generation", which appears only as a phrase in the earlier 40-item draft. Items 89–96 assume the Phase 4 desktop host exists and extend it from "offline designs" to "offline tenant analysis".

## 6. Sources

| # | Source | Quality |
|---|---|---|
| 1 | Eraser, "Best cloud diagram tools in 2026" — Eraser/Cloudcraft/Lucid Cloud Accelerator/Hava feature + weakness matrix | secondary |
| 2 | Hava product site (diagram generation, drift, cost, export, self-hosted) | primary |
| 3 | Brainboard product site (visual designer, Terraform-only, drift, CI/CD, Checkov/tfsec/OPA/Infracost) | primary |
| 4 | Microsoft Learn — "Create and deploy Azure deployment stacks in Bicep"; `Microsoft.Resources/deploymentStacks` 2025-07-01 reference (`actionOnUnmanage`, `denySettings`, scopes, out-of-sync error, portal not implemented) | primary |
| 5 | Microsoft Learn — "Template deployment what-if" (change types, limits, `reference()` caveat, required access) | primary |
| 6 | `Azure/aztfexport` README (Terraform state+config export, azurerm + AzAPI, non-comprehensive output warning) | primary |
| 7 | `microsoft/ARI` README (Excel + draw.io output, data sources, permissions, limitations) | primary |
| 8 | `suellenferreira/azure-tenant-insights` README (read-only tenant scan, collected data sources, dual HTML reports, draw.io diagrams, throttling/ARG limits, permission matrix, runtime scaling, Foundry visibility gap) | primary |
| 9 | `Azure/azqr` and Azure MCP Server compliance tooling | primary |
| 10 | This repository: `README.md`, `AGENTS.md`, `.swarm/plan.md`, `docs/v1-plan.md`, `docs/azure-refresh-plan.md`, source inspection | primary |
