# Azure Design Studio

## What This Is

Azure Design Studio is a Windows desktop and browser application for designing Azure architectures visually, comparing them against live tenant truth, and changing Azure only through an approved, hashed plan. It serves Azure architects and platform engineers who need one workspace that covers diagram, inventory, drift, cost, IaC, deployment, and decommission review.

## Core Value

Design, observe, and change Azure from one workspace — canvas fidelity, live tenant truth, and gated execution are all first-class and none may be sacrificed for the others.

## Requirements

### Validated

<!-- Shipped in v1.0 and v2 and relied upon today. -->

- ✓ Visual Azure architecture editor with 49 stencils, grouping, undo/redo, minimap, alignment guides — v1.0
- ✓ ARM and Bicep export with per-resource api-versions, golden round-trip coverage, and text `.ads.json` — v1.0 / v2 10.1
- ✓ Cost estimation with Retail Prices API and bundled snapshot fallback — v1.0
- ✓ Entra ID authentication across WASM, MAUI desktop, and gRPC server — v1.0
- ✓ Desktop MSAL/WAM credential chain with device-code fallback and DPAPI/Credential Manager secrets — v1.0
- ✓ SQLite workspace for designs, scans, snapshots, cost cache, plans, audit records, change feed, and decom windows — v1.0 / v2
- ✓ Tenant scans over Azure Resource Graph with checkpoints, permissions preflight, and per-group failure records — v2 7.x
- ✓ Live topology, drift, change feed, exposure, reachability, unmanaged/protected resources, locks, expiry, holdings, and orphan detection — v2 8.x–9.x
- ✓ Mandatory what-if, single-use token, named approval bound to a `sha256:` plan hash, and append-only apply audit — v2 11.x
- ✓ Deployment stacks, guardrails on the deploy path, adopt/refresh/detach reconcile, and live-plane refresh that preserves edits — v2 12.x–13.x
- ✓ Decommission planner, runbook-as-code, and guarded window execution that never performs the purge stage — v2 13.x
- ✓ Read-only MCP server exposing 13 workspace tools over a read-only SQLite connection — v2
- ✓ CI for Server, WASM, MCP, and Core.Tests on Linux plus CodeQL and Docker build — v1.0

### Active

<!-- This milestone: land current work, close the Trust Gate, ship a signed desktop build. -->

- [ ] Current uncommitted shell/domain/docs slice is committed in reviewable chunks and pushed
- [ ] Repository hygiene: root WebCIL blobs and agent scratch removed from tracking
- [ ] Desktop host builds on pull requests and blocks merges once stable
- [ ] Identity extraction returns typed 401/403 instead of throwing on missing or non-GUID claims
- [ ] DEBUG development works without Key Vault or Azure credentials
- [ ] Server request limits, rate limiting, deadlines, and health endpoints are live
- [ ] Design payload size/version contract is enforced client and server side
- [ ] Guardrail policy mutation requires an authorized role and is tenant-scoped
- [ ] Both databases use EF Core migrations; SQLite runs in WAL with backup/restore
- [ ] Server integration, component, and committed E2E suites run in CI
- [ ] Round 2 shell slice is completed and verified (palette, inspector, status bar, theme, three-zone layout)
- [ ] A signed MSIX is produced and installed on a clean Windows machine for pilot use

### Out of Scope

- Multi-cloud providers — the product thesis is Azure-native; supporting AWS/GCP would fork every engine.
- CRDT or real-time multi-user collaboration — no infrastructure or demand; designs are single-author today.
- A home-grown Azure Policy evaluation engine — correctness requires Azure Policy APIs; reimplementation would lie.
- Moving tenant scans into WASM — WASM has no workspace, Discovery reference, or live credentials.
- Multi-tenant/cross-tenant connection store — deferred to the v3 data-foundation milestone, not this one.

## Context

- **Brownfield:** v1.0 closed 2026-10-13; v2 phases 6–13 substantially shipped with status notes in `docs/v2-plan.md`. The codebase map under `.planning/codebase/` was refreshed 2026-10-21.
- **Pending work landed in Phase 1:** the Round 2 shell (`CommandPalette`, `DesignInspector`, `StatusBar`, `ThemeToggle`, `ads-tokens.css`, `AdsCommandService`, `LinkTypeClassifier`) and the rest of the slice that existed only on one machine were committed in the core and shell commits; the residual tree measured at planning time was 16 porcelain paths (9 modified, 7 untracked). The offline suite is green (755 passed).
- **Roadmap authority:** `docs/v3-plan.md` defines N1–N50 and the Trust Gate; `docs/top-100-ui-improvements.md` defines Round 2 U1–U100; `docs/next-phases-plan.md` records the recommended execution order and gates.
- **Known defects from the 2026-10-21 assessment:** stale pointer-drag zone in `wwwroot/js/ads.js`, a singleton command registry that outlives `TopMenu`, and two unused tracked WebCIL blobs at the repo root.
- **Docs drift resolved in Phase 1:** README/AGENTS/CONCERNS/architecture had reported 7-8 projects and the retired identity provider; the docs commit corrects them and banners the two dated plan records.
- **No GSD state existed before this initialization**; `.planning/` previously held only codebase maps and graphify output.

## Constraints

- **Platform:** .NET 10 across 11 projects; Blazor WebAssembly and MAUI Blazor Hybrid share one Razor UI; the desktop host is Windows-only (`net10.0-windows10.0.19041.0`).
- **Host split:** WASM has no `Discovery`/`Workspace` reference and no live tenant data; Desktop-only surfaces must degrade cleanly through `AdsHostFeatures`.
- **Transport:** gRPC-Web over HTTP/1.1 in the browser, native HTTP/2 on loopback 5204 for Desktop; clients must use `GrpcClientFactory` so auth handlers apply.
- **Auth:** Entra ID work+personal accounts (not B2C); server validates JWT and required scopes.
- **Execution safety:** no mutating ARM call without a fresh single-use what-if token and a named approval bound to the plan hash; pending decommission purge stages remain inert.
- **Distribution:** signed MSIX for `win-x64` is required for this milestone; certificate custody and installation verification are part of the gate.
- **Testing:** one test project today (`Core.Tests`, 755 offline cases); server/component/E2E coverage must be added, not assumed.
- **Data safety:** database migrations must preserve existing `workspace.db` and server rows; no silent destructive upgrade.

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Sequence Trust Gate before new UI surfaces | Security, delivery, and tenancy gaps invalidate feature work; docs/v3-plan.md Phase 0 is the gate | — Pending |
| Signed MSIX as the milestone artifact | Pilot users need a verifiable install; unsigned packaging defers trust questions | — Pending |
| Keep v3 non-goals excluded | Multi-cloud, CRDT, custom policy engine, and WASM scans would each fork the architecture | — Pending |
| Land the existing uncommitted slice before new work | 120 commits and 54 paths exist only on this machine; the tree is green now | — Pending |
| Publisher legal name and signing route: `CN=Jakub Talaj` (individual validation) via Azure Artifact Signing | No private-key custody; matches the Phase 11 `azure/trusted-signing-action@v0` path; identity validation has external lead time | Decided 2026-10-21 — manifest update deferred to Phase 11 (D-21) |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd:complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-10-21 after initialization*
