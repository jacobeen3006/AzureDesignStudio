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

## Phase 7 — Tenant discovery & inventory

- 7.1 **[2]** Azure Resource Graph query spine: one engine, 1k-row pagination, `Retry-After`-honoring bounded backoff, throttle control.
- 7.2 **[12][1]** Permission preflight + tenant onboarding: probe each data source, disable exactly what's missing with explanation (Role → unlocked-features matrix in UI).
- 7.3 **[3]** Scope tree: tenant → MG → subscription → RG (enrolled/unenrolled).
- 7.4 **[4]** Typeless inventory layer: raw ARM JSON + enrichment for any type, zero code change for new services.
- 7.5 **[11][91]** Scan engine: parallel collectors, progress/pause/cancel, checkpoints, scan manifest (scope, counts, partial failures) persisted per snapshot **[10]**.
- 7.6 **[5][9]** Tag/owner facets; sovereign-cloud endpoint sets.
- 7.7 **[7][6]** Orphan & idle detection report; retirement/deprecation catalog with freshness ("data as of").

**Gate:** scan a real small tenant offline-resumably; browse full inventory + orphan report; kill mid-scan → resume from checkpoint; 429 storm handled (part of 99 suite).
**Effort:** 3–4 weeks (the biggest read-only chunk).

## Phase 8 — Live topology view

- 8.1 **[13]** Relationship derivation: peerings, private endpoints→zones, delegations, NSG/UDR refs, connection strings, MI/RBAC grants, diagnostic targets.
- 8.2 **[14][21]** Canonical nested framing (tenant→…→subnet) + identity-stable layout (node id = ARM id; rescan preserves positions).
- 8.3 **[17][20][23]** Scope filters/saved views; icon pipeline with generic fallback (no blank boxes); resource inspector (props, raw ARM, tags, RBAC, portal link).

**Gate:** scan → topology renders truthfully (edges only from config, never decorative); rescan highlights *change*, not shuffle.
**Effort:** 2–3 weeks.

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

## Phase 10 — IaC breadth

- 10.1 **[48]** Text-first `.ads.json` as source of truth (canvas = view) — precondition for §C diffs in git.
- 10.2 **[37][39]** Terraform via AzAPI (mirroring aztfexport's azurerm+azapi split) + generate IaC from live resources for top ~30 types with honest "starting point" notice.
- 10.3 **[38][41]** In-process validate-before-show for ARM/Bicep/Terraform; typed parameters, `.bicepparam`, Key Vault references — no plaintext secrets ever.
- 10.4 **[40][43]** AVM module toggle; naming engine with live uniqueness checks.

**Gate:** exported TF passes `terraform init/validate` in CI; live→Bicep of a scanned RG redeploys to a sandbox matching source.
**Effort:** 3–4 weeks.

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
