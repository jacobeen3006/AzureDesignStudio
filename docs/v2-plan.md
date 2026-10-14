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

## Phase 11 — Safe execution

- 11.1 **[50]** What-if mandatory preflight (FullResourcePayloads; surface limits + `reference()` caveat honestly).
- 11.2 **[51][52][56]** Multi-scope deploys (RG/sub/MG/tenant), topological apply with parallelism + per-resource state machine, live monitor with targeted retry.
- 11.3 **[53][62]** Plan → review → apply: plan is a first-class artifact, named-principal approval, approved-hash recorded; immutable audit record per apply.
- 11.4 **[57]** Pre-deploy snapshot + inverse/revert plan.

**Gate (99):** no delete-bearing operation exists yet; CI asserts **no mutating call without approved plan hash**.
**Effort:** 3–4 weeks.

## Phase 12 — Reconcile & deployment stacks

- 12.1 **[18]** Two-plane canvas: live plane (refreshed) + intent plane (edits) — refresh never destroys edits (Eraser behavior; Cloudcraft's convert-on-edit failure avoided).
- 12.2 **[8][58]** Adopt (scope → editable diagram + matching ARM/Bicep); per-resource reconcile: adopt / update / detach / delete *(delete disabled until 13)*.
- 12.3 **[60][61]** Deployment-stack manager: create/update with `actionOnUnmanage` + `denySettings`, managed-list drift + `stack-out-of-sync` guard, bypass behind review screen. (Portal still can't do this — ship ahead.)

**Gate:** round trip adopt→edit→what-if→apply→rescan shows zero drift; stack guard tests green.
**Effort:** 3–4 weeks.

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
