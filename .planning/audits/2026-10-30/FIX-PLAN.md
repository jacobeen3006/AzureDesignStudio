---
type: audit-fix-plan
baseline: fa62bb9
status: implemented-uncommitted
commit_authorized: false
---

# Scoped audit fixes

This is an audit supplement, not a replacement roadmap or a completed GSD phase. Preserve existing `.planning/STATE.md`, roadmap counters and the user's untracked files. Baseline verification owns `dotnet` until it returns. Source edits start after baseline verification, followed by red/green regression tests and final builds. No Azure writes, credentials, user workspace databases, commits or deployments.

## Classification

| ID | Finding | Severity | Classification | Reason / phase |
|---|---|---|---|---|
| FIX-01 | `VirtualNetworkModel.NextFreeSubnetPrefix` compares CIDR strings rather than ranges, invents a fallback on invalid/exhausted input and can allocate outside a narrow VNet | High correctness | Auto-fixable | One allocator and its sole UI caller; domain tests can prove bounds, overlap and failure behavior. Shell provisioning follow-up to Phase 1 / Phase 7. |
| FIX-02 | Pointer drag remains armed after lost release; later click drops a stale stencil and replacement drag leaks a ghost | High UX/correctness | Auto-fixable | Real app JS reproduced in isolated Node event harness: 3 failures, 2 controls pass. Phase 7 / Phase-01 H1. |
| FIX-03 | JS-created drag ghost and body cursor rules live in isolated component CSS and cannot match | Medium UX | Auto-fixable | Move unchanged rules to existing global tokens stylesheet; verify CSS ownership and generated bundle. Phase 7 / Phase-01 H2. |
| FIX-04 | Subnet form presents `Microsoft.Database/custom` as a real delegation | Medium export correctness | Auto-fixable | Remove unsupported canned option; keep existing persisted/custom values untouched. Phase-01 M1. |
| REVIEW-01 | Dedicated subnet lookup and placement checks disagree on casing | Medium correctness | Further platform-contract review | Do not infer reserved-name rules solely from general resource-ID case insensitivity; settle exact Azure requirement before changing model/form predicates. Phase 7. |
| REVIEW-02 | Identity, role/tenant scoping, credentials and DB schema changes | High security / durability | Planned architectural work | Existing Phases 3–6, 8–9 require integration harness and migrations; do not improvise partial authorization/schema fixes during this audit. |

## Regression contract

### FIX-01

- Allocate only a complete aligned `/24` inside configured IPv4 address space.
- Reject overlap with existing subnets of any prefix length, including noncanonical host bits.
- No allocation for exhausted, invalid, absent, IPv6-only or narrower-than-`/24` address spaces; return `null` rather than a fabricated CIDR.
- Consider later configured IPv4 ranges when an earlier range is exhausted or unsuitable.
- An invalid existing subnet makes safe automatic allocation unavailable rather than guessing.
- The UI must show an actionable error and return before diagram/history mutation if allocation fails.
- Test `/0`, upper IPv4 boundary, multiple spaces and covering subnets without unsigned wraparound or a huge linear address scan.

### FIX-02

- Normal mouse/pen/touch drag still emits one drop.
- Pointer cancel and window blur emit none and remove feedback.
- A pointer move with no primary button held cancels stale state.
- A fresh primary pointerdown clears stale state before inspecting its target.
- Secondary/unrelated pointer events cannot finish the original drag.
- Reinitialization/remount still uses the live zone and does not duplicate handlers.
- This harness tests the app's JS state machine; it is not a substitute for real WebView2 pointer capture or rendered UI accessibility testing.

### FIX-03 / FIX-04

Use reproducible source-contract checks for these narrow stylesheet/form changes, followed by WASM/desktop builds. Do not claim rendered visual or live ARM deployment verification.

## Evidence

Initial pointer red run: `C:/Users/jakub/AppData/Local/hermes/cache/scratch/ads-pointer-red.log` (5 tests: 2 pass, 3 fail as expected). Copy final durable tests and evidence into this audit's deliverables after source fixes.

## Completion gate

Record actual fixes, red/green results, full-suite/build outcomes, unchanged lock/config files, and unverified surfaces in the final audit. Do not mark any roadmap phase complete based on this scoped pass.

## Results (2026-10-30, implementation run — uncommitted working tree on top of baseline)

All four auto-fixable items implemented in the working tree; no commits made. `git diff --stat` touches exactly 8 source files (plus 2 new test files); no `.csproj`, `Directory.*.props`, `global.json`, or lock file changed.

| ID | Change | Verification |
|---|---|---|
| FIX-01 | `VirtualNetworkModel.NextFreeSubnetPrefix` rewritten as range-based aligned-`/24` allocator returning `null` on exhaustion/invalid/IPv6-only/narrower-than-/24; `DiagramPanel.razor.cs` shows actionable error toast (`ads-subnet-exhausted`) and returns before diagram mutation | New `Fix01SubnetAllocatorTests.cs` + extended `SubnetPlacementTests.cs`; offline suite **773 passed / 0 failed** (`evidence/test-core-offline-final.log`, was 755 → +18 subnet boundary/overlap/failure tests) |
| FIX-02 | `ads.js` pointer state machine: pointerId tracking, no-button move cancels stale drag, fresh pointerdown clears stale state, pointercancel/lostpointercapture cleanup | `wwwroot/js/tests/ads-pointer.test.cjs` **5/5 pass** (`evidence/test-pointer-final.log`; red baseline was 3 fail). Live browser: ghost appears on drag, removed on drop, mid-air lost release, and pointercancel; no stale `ads-dragging` body state |
| FIX-03 | Drag-ghost/body-cursor rules moved from isolated `Stencil.razor.css` to global `ads-tokens.css` | Live computed style on JS-created ghost: `position: fixed; z-index: 3000` (previously unmatchable); token `--ads-accent` resolves |
| FIX-04 | `Microsoft.Database/custom` canned delegation option removed from `SubnetForm.razor` and `VirtualNetworkForm.razor`; persisted values pass through untouched | `grep` confirms zero occurrences in both forms |

### Builds

- `dotnet build src/AzureDesignStudio.Core -c Release` — succeeded, 0 warn/0 err
- `dotnet build src/AzureDesignStudio -c Release` (WASM) — succeeded, 0 warn/0 err
- `dotnet build src/AzureDesignStudio.Desktop -c Release -p:RunAOTCompilation=false` — succeeded, 0 errors (40.9s)
- Full AOT-enabled solution build `dotnet build src/AzureDesignStudio.sln -c Release` — **succeeded, 0 errors** (evidence: `evidence/build-sln-aot-final.log`); the prior timeout was a wall-clock limit, not a defect. All warnings are pre-existing `WINAPPSDK PRI263` Desktop/MSIX resource noise; none reference the changed files.

### Runtime UI validation

Served via Kestrel dev host (`dotnet run --project src/AzureDesignStudio`); stale `publish/wwwroot` and a service-worker-cached 10-13 bundle were red herrings during validation (empty caches + unregister required for a true cold load). App boots, stencil rail renders, synthetic drag emits validation toast "Application Gateway must be deployed in a subnet" for an out-of-subnet drop — validation path confirmed live. Screenshot reviewed: shell/inspector/status bar render correctly.

New observation (not fixed): Blazor's "restart required" update modal renders without a visible dismiss control and the error toast overlaps the search field. Candidate UI finding for the modernization plan.

### Still unverified

- Real WebView2 pointer capture (Node harness + Chromium synthetic events only)
- REVIEW-01 casing contract and REVIEW-02 security/architecture items remain open by classification
