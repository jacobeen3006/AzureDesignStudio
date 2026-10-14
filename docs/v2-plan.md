# Azure Design Studio — v2 Plan: Live Tenant Analysis & Safe Execution

**Starts:** after v1.0 (Phases 0–5 closed, see `docs/v1-plan.md`).
**Source:** `docs/top-100-improvements.md` — item numbers below reference that list.
**Thesis (the wedge):** live tenant truth + editable design + safe execution in one desktop app, ARM/Bicep first-class. Read-only depth ships **before** anything that can delete. Analysis engine lives in the desktop/native process (§4 of the 100-list); WASM site stays the designer.

---

## Phase 6 — Safety net & prerequisites

- 6.1 **[97]** Close silent export failures: implement `GetArmParameters()`/`GetArmResources()` in `Network/VirtualNetworkPeeringModel` (currently throws — peering draws but cannot export); replace throwing default arms (`DataModelFactory`, `ResourceBaseJsonConverter`, `ARMResourceBase`, `_ => throw` in `WebAppModel`/`FunctionAppModel`) with generic-resource degradation + warning; golden-file test: **every stencil key round-trips to ARM/Bicep**; test asserts no `NotImplementedException` reachable from export paths.
- 6.2 **[89]** Desktop credential chain: `AzureCliCredential` → managed identity → service principal → device code (WAM broker from v1 stays the interactive path). One `AdsTokenBroker` seam consumed by the gRPC bearer handler and every new Azure SDK client. Tokens/secrets → Windows Credential Manager/DPAPI, never config.
- 6.3 **[90][10-schema]** SQLite workspace (EF Core): tables for designs, scans, snapshots, cost cache, plans, audit records; migrate `FileSystemDesignOfflineStore` onto it; everything readable with network down.

**Gate:** golden export suite green (33/33 + generic fallback); `az login` present → token without prompt; desktop feature-complete offline against SQLite.

**Effort:** 5–7 days.

**Status (2026-10-14):** 6.1-6.3 done - peering export implemented (no `NotImplementedException` reachable from export paths; golden suite `StencilExportGoldenTests` round-trips every stencil key to ARM/Bicep, inside the 180-test offline run); `AdsTokenBroker` chain AzureCli → ManagedIdentity → service principal → MSAL interactive (WAM broker + device-code fallback), consumed by the gRPC bearer handler and Discovery SDK clients, secrets only via Credential Manager/DPAPI; SQLite `workspace.db` (designs/scans/snapshots/cost cache/plans/audit) with `SqliteDesignOfflineStore` as the `IDesignOfflineStore` and `LegacyDesignImporter` migrating the old JSON files - designs readable with network down.

## Phase 7 — Tenant discovery & inventory

- 7.1 **[2]** Azure Resource Graph query spine: one engine, 1k-row pagination, `Retry-After`-honoring bounded backoff, throttle control.
- 7.2 **[12][1]** Permission preflight + tenant onboarding: probe each data source, disable exactly what's missing with explanation (Role → unlocked-features matrix in UI).
- 7.3 **[3]** Scope tree: tenant → MG → subscription → RG (enrolled/unenrolled).
- 7.4 **[4]** Typeless inventory layer: raw ARM JSON + enrichment for any type, zero code change for new services.
- 7.5 **[11][91]** Scan engine: parallel collectors, progress/pause/cancel, checkpoints, scan manifest (scope, counts, partial failures) persisted per snapshot **[10]**.
- 7.6 **[5][9]** Tag/owner facets; sovereign-cloud endpoint sets.
- 7.7 **[7][6]** Orphan & idle detection report; retirement/deprecation catalog with freshness ("data as of").

**Status (2026-10-13):** 7.1-7.7 done - ARG spine, permission preflight, scope tree, typeless inventory, checkpointed scan engine, tag/owner facets + sovereign-cloud endpoint sets, orphan/idle analyzer + deprecation catalog with freshness tiers. Remaining: browse/report UI + real-tenant smoke (mid-scan kill resume, 429 storm).

**Gate:** scan a real small tenant offline-resumably; browse full inventory + orphan report; kill mid-scan → resume from checkpoint; 429 storm handled (part of 99 suite).
**Effort:** 3–4 weeks (the biggest read-only chunk).

## Phase 8 — Live topology view

- 8.1 **[13]** Relationship derivation: peerings, private endpoints→zones, delegations, NSG/UDR refs, connection strings, MI/RBAC grants, diagnostic targets.
- 8.2 **[14][21]** Canonical nested framing (tenant→…→subnet) + identity-stable layout (node id = ARM id; rescan preserves positions).
- 8.3 **[17][20][23]** Scope filters/saved views; icon pipeline with generic fallback (no blank boxes); resource inspector (props, raw ARM, tags, RBAC, portal link).

**Gate:** scan → topology renders truthfully (edges only from config, never decorative); rescan highlights *change*, not shuffle.
**Effort:** 2–3 weeks.

**Status (2026-10-13):** 8.1-8.2 done - all edge kinds derived from ARM config id references (external refs become ghost nodes, never dropped), canonical sub/RG/resource nested framing with deterministic id-sorted packing, positions persisted by ARM id per frame container (18 tests: unchanged rescan byte-identical, new resources never move saved nodes). Topology tab wired into the scan drawer: scope filters, category-icon fallback, drag-to-move with persistence, inspector (props/tags/raw/portal link), rescan change highlight. Remaining: 8.4 real-tenant gate.

## Phase 9 — Change intelligence (read-only value peak)

- 9.1 **[26][29]** Change feed (ARG change history + Activity Log) with caller attribution.
- 9.2 **[27][28]** Three-way drift (scan↔now, design↔live, design↔design) + side-by-side diagram diff and property-tree diff.
- 9.3 **[33][36-first-cut]** Unmanaged-resource detection; do-not-touch guardrail list v1 (scopes/tags/types flagged everywhere).
- 9.4 **[84]** Exposure analysis (public IPs, open NSGs, public PaaS endpoints, private-endpoint gaps).
- 9.5 **[73][80][74]** Actual spend via Cost Management (declare `Cost Management Reader` need in preflight); provenance on every figure (region/currency/source/as-of); cost by dimension.
- 9.6 **[25][87]** Shareable static report bundle (HTML+PNG+JSON); framework-mapped audit export.
- 9.7 **[98][99]** Nightly live-Azure CI (tag-scoped sandbox RG, what-if-only for changes); failure-mode suite (throttle, partial perms, offline, malformed input).

**Gate:** drift between two scans renders; every $ figure carries provenance; nightly green.
**Effort:** 3–4 weeks. **This closes the read-only product.**

**Status (2026-10-14):** 9.1 spine done - `ChangeFeedCollector` over the ARG `resourcechanges` table: time-windowed, newest-first, projected flat with Change Actor attribution (`changedBy`/`clientType`/RBAC `operation` - the existing Reader role covers the table; the Activity Log probe in preflight stays as the enrichment path for richer operation names). Rows normalize `Unspecified`/`Unknown` to null and drop partially-written records; persisted to `workspace.db` `ChangeFeed` via `WorkspaceChanges` (idempotent by resource+change key, overlapping collection windows add nothing) and exposed to the desktop host through `ChangeFeedService` (`CollectAsync` / `FeedAsync` / `MovedAsync` - the "what moved since the scan" shape 9.2 diffs against). Changes-tab UI lands when the scan drawer integration point is free; 10 offline tests (row normalization, partial-row drops, idempotent persistence, newest-per-resource fold).

**Status (2026-10-14):** 9.2 engine done - `ScanDiffer` flattens two typeless inventories (the same `InventoryItem` shape ARG scans produce) into stable leaf paths (`properties.sku.name`, arrays `[i]`) and classifies Added/Removed/Changed with per-leaf old/new JSON; volatile platform churn (`provisioningState`) is reported but never counted as drift, and a mis-versioned dump caps at 200 property diffs per resource. Same engine serves design↔design and design↔live: `DesignTemplateReader` turns an ARM deployment template into comparable items (ids/types lowercased so ARM id casing matches live). `LiveDrift.Overlay` flags scan↔now from the change feed (New/Deleted/Modified + caller, newest change per resource, pre-scan changes ignored). Desktop seam: `ChangeFeedService.DiffScansAsync` / `DriftSinceScanAsync`; 10 offline tests. Drift-diff rendering joins the drawer UI when the parallel editor work lands.

**Status (2026-10-14):** 9.4 engine done - `ExposureAnalyzer` over the typeless `InventoryItem` scan shape: public-IP VMs (NIC ipConfigurations -> pip, `microsoft.network/loadBalancers|applicationGateways|frontDoors` public frontends), NSG allow-inbound rules with internet sources (all-ports+ `*`/`0.0.0.0/0`/`Internet` -> Critical; 22/3389 -> High; platform `default*` rules and Deny/scoped/Outbound quiet), public PaaS host endpoints (`primaryEndpoints`, `serviceEndpoint`, `hostNames`, `fullyQualifiedDomainName`, vault properties) with private-endpoint suppression by target-id + sub-resource casing (`blob`/`sqlServer`/...), unapproved PE connections (PendingAccepting/NotSpecified + actionsRequired -> Low), SQL `0.0.0.0` firewall rule -> Critical; VM admin-port escalation reads the NSG attached to the public NIC. Desktop seam `ChangeFeedService.ExposureAsync(scanId)`; 12 offline fixture tests. Exposure-tab rendering joins the drawer UI with the rest of the Phase 9 UI integration.

**Status (2026-10-14):** 9.5 engine done - `ActualSpendClient` over the Cost Management query API (any ARM scope; the preflight `costManagement` probe already declares `Cost Management Reader`): custom-window usage query summed per `ResourceId|ResourceGroupName|ServiceFamily|MeterCategory|ResourceLocation`, async-job 202+`Location` polling added to `ArmTransport` (shared 429 budget intact). `CostQueryParser` binds columns by name, never position (API reorders/adds between versions; missing grouping column -> null dimension, never guessed; ragged rows drop; null/quoted cost cells parse); every figure inherits `SpendProvenance` (source/scope/region/currency/window/as-of) and `CostRollup` yields dimension buckets with shares (deterministic tie order, zero-total safe). Desktop DI: `ActualSpendClient` singleton; 11 offline tests. Cost-tab UI + retail-vs-actual reconciliation join the drawer UI with the rest of the Phase 9 UI integration.

**Status (2026-10-14):** 9.3 first cut done - `UnmanagedDetector` over the typeless inventory: Managed when an orchestration marker owns the item (ARM `managedBy` property, `providers/managedby|managed-by|provisioned-by` tags with non-empty value, VMSS member ids) and stays silent; Unmanaged otherwise (`no-orchestration-marker`, ordinal-sorted findings + id set for every consumer view); `GuardrailPolicy` v1 is declarative JSON (`scopes`/`tags[key,value?]/types`, all case-insensitive, subtree match on segment boundaries, type-prefix covers children) and outranks managed markers; legacy `do-not-touch=true` protects under any config. Matched pattern travels as the finding's reason (audit trail feeding the [62] delete gate). 11 offline tests. Policy persistence + surfacing in the scan drawer join the Phase 9 UI integration.

**Status (2026-10-14):** 9.6 done - static report bundle in `Discovery/Report`: `ReportModel` carries scope+as-of and the four analysis sections (exposure, unmanaged/protected audit, live drift, spend+provenance) plus optional topology SVG; absent sections export as absent (no empty-table lies). `ReportBundle.Build` returns deterministic files: `report.json` (camelCase, enums as strings, nulls omitted), `report.html` (self-contained inline CSS, HTML-escaped cells, provenance footer so figures carry their window/currency/source), `controls-audit.csv` (framework mapping + human detail), optional `topology.svg` (script-shaped payloads rejected, same guard in both writers). `ControlsMap` cites ids only where certain (CIS Azure v2.0 3.1/3.2 RDP/SSH); everything else maps to NIST 800-53 control names; unknown kinds export UNMAPPED, never guessed. PNG raster rides on the topology writer + a rasterizer dep - deferred, not faked. 8 offline tests (byte-determinism, escaping, provenance, CSV quoting, mapping + fallback). Drawer wiring + folder picker = Phase 9 UI integration.

**Status (2026-10-14):** 9.7 offline half done - failure-mode suite (`FailureModeSuiteTests`, 12 tests) replays throttle, partial permissions, offline and malformed input against scripted handlers with injected delays (no real sleeping, 57 ms): Retry-After honoured + paging continues after backoff; throttle budget bounded and surfaces as typed `Throttled` (preflight: caveat, never "missing permission"); one 403 source degrades exactly one probe (role named in detail, siblings stay Available); offline is caveat not not-available; cancellation propagates unswallowed; HTML-under-200 throws instead of returning silently empty; error bodies with mis-typed `code`/`message` fields fall back to the HTTP status (fixed `FromErrorBody` - `GetValue<string>` on a number used to throw `InvalidOperationException` and mask the real failure). Nightly live-Azure CI (tag-scoped sandbox RG, what-if-only) stays deferred with the rest of the live-infra work.

## Phase 10 — IaC breadth

- 10.1 **[48]** Text-first `.ads.json` as source of truth (canvas = view) — precondition for §C diffs in git.
- 10.2 **[37][39]** Terraform via AzAPI (mirroring aztfexport's azurerm+azapi split) + generate IaC from live resources for top ~30 types with honest "starting point" notice.
- 10.3 **[38][41]** In-process validate-before-show for ARM/Bicep/Terraform; typed parameters, `.bicepparam`, Key Vault references — no plaintext secrets ever.
- 10.4 **[40][43]** AVM module toggle; naming engine with live uniqueness checks.

**Gate:** exported TF passes `terraform init/validate` in CI; live→Bicep of a scanned RG redeploys to a sandbox matching source.
**Effort:** 3–4 weeks.

**Status (2026-10-14):** 10.1 first cut done - `AdsDesignText` (Core/Common): canonical `.ads.json` v1 text format over the existing `DiagramGraph` DTO document. Deterministic-by-construction: entities identity-sorted (id, full-text tiebreak; links by source/target), one entity one JSON object, `Write(Read(Write(g))) == Write(g)` - the git-diff invariant for §C review; unknown per-type payload survives roundtrip untouched (TypeKey dispatch intact), casing matches the live saved-design format so the canvas loader path is compatible. Malformed text fails typed (`AdsDesignFormatException`, localized message) - bad version/section/entity never half-loads. 10 offline tests (roundtrip losslessness incl. extra fields, idempotence, sorting, casing, 6 malformed-input rows). Canvas cutover (save/load via text + git import/export UI) and workspace seam follow; DB/gRPC blob path untouched meanwhile.

**Status (2026-10-14):** 10.2 first cut done - `Discovery/Iac/TerraformText`: scan inventory → deterministic `.tf` text. Honest aztfexport split for typeless data: every resource is a generic `azapi_resource` (uniform api-version, called out in the header as pin-before-apply), body = recorded live properties as canonical key-sorted JSON (byte-stable output), true ARM parent scopes (nested: drop child type+name; top-level: scope before `/providers/`), sanitized collision-suffixed local names, subscription-level items skipped and listed in the header. Terraform markers (`${`, `%{`) inside scanned strings neutralized - live values can never inject interpolation or directives. Out until follow-ups: per-type api-versions, native azurerm mapping for the top-30, dependency wiring (stated in generated header). `terraform init/validate` CI assertion pending a runner with the binary; generator proof is 7 offline tests.

**Status (2026-10-14):** 10.4 offline half done - `NamingEngine` (Core/Common): per-family `NamingProfile` (charset/length/case from ARM rules) plus the one fact the canvas validator can't know - `GloballyUnique` for the ten DNS-backed families (storage, sql-server, cosmos, registry, keyvault, ai-search, signalr, web/function/app, apim). `Suggest` is deterministic (no RNG): workload+environment segments, stable 6-hex entropy from sha256(workload|env|type) for unique families, workload truncates first while the entropy+counter tail survives, `taken` collisions resolve with stable counters - regenerated designs diff to nothing in git. UI picker wiring + live uniqueness verification (ARM `checkNameAvailability`) follow with the online phases. 6 offline tests.

**Status (2026-10-14):** 10.3 first cut done - `Discovery/Iac`: `ArmTemplateValidator` = in-process validate-before-show over generated ARM templates (stable diagnostic codes): unresolved/cyclic `dependsOn` (cycle named end-to-end), undeclared `parameters()`/`variables()` references, duplicate resource identities, missing api-versions, unused-parameter warnings, and a hard error for any `secureString`/`secureObject` default - the finding never echoes the secret value. `BicepParamsText` = typed `.bicepparam` generation: secure parameters emit Key Vault reference objects only (a supplied plaintext value is dropped and said so in the file), typed literals per bicep, missing required values commented rather than silently defaulted. ARM provider-side rules stay what-if/deploy-time; Bicep/TF syntax validation needs out-of-process binaries (CI-side). 11 offline tests.

**Status (2026-10-14):** 10.2 follow-up done - per-type api-versions. `AzureApiVersions` (Core/Common) derives ARM type -> api-version from the generated models' `[AzureResource]` attributes (single source of truth, stable beats preview, case-insensitive, built once); `TerraformText.Generate` takes the optional table, pins real versions per resource and says so in the header, unknown types keep the uniform fallback. Native azurerm top-30 mapping stays deferred (needs per-type property curation). 5 new offline tests (table contents, case-insensitivity, pin+fallback, real-table integration).

**Status (2026-10-14):** 10.3 wired into the deploy path - `ArmTemplateValidator.TryFindBlockingErrors` is now Server's pre-deploy gate inside `CreateDeployment`: any Error-severity finding (or unparseable parameters) returns 400 with the stable-code list *before* any credential lookup or Azure round-trip; warnings never block, parameter content is checked for parseability only and never echoed. No proto change - clients already surface `error_message`. 4 new offline tests.

## Phase 11 — Safe execution

- 11.1 **[50]** What-if mandatory preflight (FullResourcePayloads; surface limits + `reference()` caveat honestly).
- 11.2 **[51][52][56]** Multi-scope deploys (RG/sub/MG/tenant), topological apply with parallelism + per-resource state machine, live monitor with targeted retry.
- 11.3 **[53][62]** Plan → review → apply: plan is a first-class artifact, named-principal approval, approved-hash recorded; immutable audit record per apply.
- 11.4 **[57]** Pre-deploy snapshot + inverse/revert plan.

**Gate (99):** no delete-bearing operation exists yet; CI asserts **no mutating call without approved plan hash**.

**Status (2026-10-14):** 11.3 pre-work done (bookkeeping only, nothing executes) - `DeployPlanArtifact` (Core/Common): plan identity = scope + template + parameters, canonical compact JSON (keys sorted ordinal, arrays ordered, literals verbatim) hashed `sha256:<hex>`; `Verify` re-canonicalizes so a pretty-printed store copy still proves its hash. `PlanApprovalGate.Evaluate`: Approved + named approver + stored body still hashing to the approved hash, else a typed denial (`NotApproved`/`NoApprover`/`MalformedBody`/`BodyEditedAfterApproval`) - an edit to the stored body after approval is caught here, not in Azure. The Workspace `ExecutionPlanRecord`/`AuditRecord` (Phase 9) already carry `PlanHash`/`ApprovedBy`; this slice gives those columns a single, testable meaning. Host-side approval writes + audit rows land with the 11.x execution wiring (delete-gate policy). 11 offline tests.

**Status (2026-10-14) update:** 11.1 **done** + 11.3 **done**. `WhatIfClient` POSTs the RG-scope deployment whatIf endpoint (2025-04-01; LRO-polled through `ArmTransport`'s 429 budget) - REST has no result-format switch and ARM's default is FullResourcePayloads, whose before/after payloads now travel to the review UI truncated per row with explicit `…[truncated]` flags. `WhatIfTokenGate` binds a single-use 15-min token to the exact payload hash; `CreateDeployment` refuses missing/expired/reissued/edited payloads and the gate fails closed on indeterminate predictions (non-`Succeeded` status, `Bad`/unclassified rows) and on any Delete. `ApprovePlan` records the named principal against the `sha256:` plan hash. Apply is an append-only `AdsPlanApplyEvents` stream: `Started` (canonical plan body + sha256 of the consumed preview token, before any ARM mutation, approval consumed in the same transaction), then at most one verified terminal from ARM state alone - `Completed` on Succeeded, `Failed` only on ARM-proven refusal (4xx) or failed/partial provisioning - persisted before any client notification; ambiguous submissions (transport fault, 408, 5xx) and unconfirmable polling record `OutcomeUnknown`, never a false `Failed`, and the deploy UI shows that as "Outcome Unknown - verify in Azure", not a failure. Transport failure is never a provisioning outcome: a lost client stream records non-terminal `TransportLost` and polling continues for the real result. Honest crash window: a server dying mid-apply leaves a lone `Started`; truth then lives only in ARM. The vocabulary and the rejection-proof predicate live in Core, tested offline. The Workspace `AuditRecord` was checked: desktop-plane sink only (desktop-local SQLite the server cannot reach), so the server DB carries this trail. Release startup initializes the EF schema before the audit-table DDL guard. 11.4 (pre-deploy snapshot/revert) untouched.

**Status (2026-10-14) update 3:** 11.2 engines **done** (offline, not yet wired to execution). `DeployScopePlanner` (`Discovery/Iac`): stages one template into ordered batches Tenant → ManagementGroup → Subscription → ResourceGroup; classification by resource type plus literal `scope` path, unscoped authorization/policy inherits the caller's `ambientScope` (never silently tenant), expression scopes and later-scope dependencies fail the plan closed (`ScopeExpressionNotPlanned`, `ScopeOrderConflict` = Errors, zero batches); parameter/variable closure travels with each batch and outputs are dropped with a warning; serialization is deterministic. `DeploymentOperationsParser` (`Discovery/Arm`): ARM deployment-operations payload → per-resource apply state, newest timestamp wins per resource, unrecognized display statuses stay `Unknown` with a warning, malformed payloads yield diagnostics instead of exceptions. Offline suite 378/378 (19 new rows). **Not done:** execution wiring - multi-scope `ArmTransport` staging, batch-by-batch apply, live operations monitor, targeted retry (server + UI increment).

**Status (2026-10-14) update 4:** planner **wired as a pre-deploy gate**. `DeployScopePlanner.TryFindBlockingErrors(template, ambientScope)` joins the validator gate in both `WhatIf` and `CreateDeployment` before any ARM round-trip: staging errors block, and any batch beyond the pipeline's single (subscription, resource group) approval scope is rejected `MultiScopeNotDeployable` - a multi-scope template now gets a typed, honest 400 instead of reaching ARM at the wrong scope. Fail-closed hardening: `null`/non-object `resources` members yield `InvalidTemplate` with zero batches instead of being silently dropped. Offline suite 380/380. **Still not done:** staged batch-by-batch execution at the extra scopes (proto scope targets + per-batch ARM PUT), live operations monitor, targeted retry.

**Effort:** 3–4 weeks.

## Phase 12 — Reconcile & deployment stacks

- 12.1 **[18]** Two-plane canvas: live plane (refreshed) + intent plane (edits) — refresh never destroys edits (Eraser behavior; Cloudcraft's convert-on-edit failure avoided).
- 12.2 **[8][58]** Adopt (scope → editable diagram + matching ARM/Bicep); per-resource reconcile: adopt / update / detach / delete *(delete disabled until 13)*.
- 12.3 **[60][61]** Deployment-stack manager: create/update with `actionOnUnmanage` + `denySettings`, managed-list drift + `stack-out-of-sync` guard, bypass behind review screen. (Portal still can't do this — ship ahead.)

**Gate:** round trip adopt→edit→what-if→apply→rescan shows zero drift; stack guard tests green.
**Effort:** 3–4 weeks.

**Status (2026-10-14):** 12.2 read-only core done — `AdoptEngine` (Core/Common) turns an ARM-agnostic inventory (`AdoptableResource`: id/type/name/location/sku/kind/tags/resourceGroup) into an editable `.ads.json` graph: catalog-derived ARM-type→stencil table (probes every `DataModelFactory` key, claims only the model's own `GetArmResources()[0]` type; 41/46 keys mapped, case-insensitive), ARM-nesting parent resolution (subnet→vnet, db→server) landing children inside parent frames with `GroupId`, deterministic ordinal grid layout, unknown types degraded to openable placeholder nodes and reported in `AdoptionResult`. Host icon seam via optional `iconPathByStencilKey`. Honest gaps: aks-cluster/app-gateway/azure-bastions/azure-firewall (exports need diagram links) and function-app (shares `Microsoft.Web/sites` with web-app, which wins the claim). 7 tests in `AdoptEngineTests`. Reconcile actions (adopt/update/detach/delete) and the two-plane canvas (12.1) remain; delete stays gated behind Phase 13.

## Phase 13 — Decommissioning (last, carefully)

- 13.1 **[36-final]** Guardrails enforced on every destructive path (blocks listed in 100-list §3).
- 13.2 **[63][64][65]** Planner: dependency subgraph → blockers (locks, RBAC, DNS links, peerings, backups) → staged order; dependency-safe delete refusal; delete-impact preview incl. soft-delete/purge/immutability.
- 13.3 **[66][67][68]** Retain-before-retire export; idle-cost ranking ($ per candidate); protection manager (locks/denySettings pre/post windows).
- 13.4 **[69][71]** Runbook-as-code (reviewable Bicep/PS/CLI + checklist); two-stage destroy: `detach` default + T+N purge.

**Gate:** sandbox tenant teardown fully staged, every deletion preceded by impact preview + approved plan hash.
**Effort:** 3–4 weeks.

---

## Deferred (triggered, not scheduled)

| Item | Trigger |
|---|---|
| **[92]** 10k-node virtualized rendering | tenants > ~2k visible nodes in practice |
| **[93]** Local MCP server | Phase 9 spine stable (inventory/diff/cost/plan as tools) |
| **[94]** Signed auto-update | external users before trust via store |
| **[16]** reachability queries | Phase 8 graph proven |
| **[54][55]** policy/capacity preflight | Phase 11 needs |
| **[22][24][42][44–47][49][59][70][72][75–79][81–88][95][96]** | backlog, attach to nearest phase |

## Totals & ordering

| Phase | Theme | Effort | Can delete Azure? |
|---|---|---|---|
| 6 | safety net, creds, SQLite | 5–7 d | no |
| 7 | discovery spine | 3–4 w | no |
| 8 | topology | 2–3 w | no |
| 9 | change intelligence | 3–4 w | no |
| 10 | IaC breadth | 3–4 w | no |
| 11 | safe execution | 3–4 w | create/modify only |
| 12 | reconcile + stacks | 3–4 w | detach; no delete |
| 13 | decommission | 3–4 w | yes — guardrailed |

~4.5–6 months at 1 dev. Order is load-bearing: trust is the currency — Phases 7–9 ship standalone read-only value; nothing that deletes ships before guardrails (36), impact previews (65) and plan-hash audit (62) exist.
