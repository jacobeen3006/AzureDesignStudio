---
phase: 01-land-and-stabilize
plan: 02
subsystem: ui
tags: [blazor, jsinterop, pointer-events, dotnetobjectreference, headless-edge, dom-lifetime]

# Dependency graph
requires:
  - phase: 01-land-and-stabilize
    plan: 01
    provides: the core land commit and the byte-identical working-tree shell slice (ads.js +209/-4 vs HEAD, no AdsStencilDrag in HEAD)
provides:
  - "window.AdsStencilDrag with an element-scoped init guard, one-time document listeners, and event-time re-resolution of the live .ads-diagram-wrapper and its __adsRef"
  - "SC#4 BEFORE/AFTER transcripts produced by a real headless-browser harness that never entered the repository"
affects: [01-03, 01-07, "Phase 8 TEST-04"]

# Actuals (#2632) — chars/4 over the realized diff (baseline → edited), not a harness token count.
actuals:
  tokens: 1010
  tasks: 3
  commits: 0

tech-stack:
  added: []
  patterns:
    - "Element-scoped init guard set before the document-level early return (copy of AdsDropZone's discipline)"
    - "Event-time re-resolution via liveZone() = document.querySelector(selector) instead of a captured node"

key-files:
  created: []
  modified:
    - src/AzureDesignStudio/wwwroot/js/ads.js

key-decisions:
  - "BEFORE was captured against the unfixed working-tree module (HEAD contains no AdsStencilDrag at all), per RESEARCH finding #2 — there is no pre-fix commit to diff against."
  - "Confinement is proven against a byte-exact pre-edit baseline (git hash 3adf0aec, identical to the parent checkout's copy), not against HEAD, because the pre-existing slice already edits AdsDropZone relative to HEAD."
  - "No .catch/try around invokeMethodAsync (D-16); the absent-zone case is an explicit null branch in inZone/clear/hover/finish."
  - "Not committed here — plan 01-03 owns the shell commit that carries this file."

requirements-completed: [SC4]

coverage:
  - id: D1
    description: "A pointer drag from a stencil tile into the live .ads-diagram-wrapper invokes HandleDropAtAsync exactly once on the live node's DotNetObjectReference after two wrapper replacements; detached nodes receive nothing; a cycle with no zone present invokes nothing and throws nothing."
    requirement: "SC4"
    verification:
      - kind: automated_ui
        ref: 'node C:/Users/jakub/AppData/Local/Temp/ads-land-harness/run-harness.mjs <repo> → RESULT={"calls":[["b",["HandleDropAtAsync",68,86,"virtual-network"]]],...}'
        status: pass
    human_judgment: false
  - id: D2
    description: "The edit is confined to window.AdsStencilDrag; AdsDropZone, AdsTheme, AdsShortcuts are byte-identical and the harness never entered the repository."
    requirement: "SC4"
    verification:
      - kind: automated_ui
        ref: "sha256 prefix/suffix equality + git diff --no-index hunk range 127..242, min > 118"
        status: pass
      - kind: manual_procedural
        ref: "real-app DevTools check (dotnet run, sign in, remount, pointer drag, zero console errors) — plan 01-07 human check"
        status: unknown
    human_judgment: true
    rationale: "Synthetic pointer events exercise listener logic and node resolution but not real hit-testing inside a signed-in Blazor app; both hosts still ship placeholder ClientId values, so the real-app half of D-14 cannot run unattended on this machine."

metrics:
  duration: not instrumented
  completed: 2026-10-21
status: complete
---

# Phase 1 Plan 2: Stencil Drag Pointer-Zone Repair Summary

**`window.AdsStencilDrag` re-resolves the live `.ads-diagram-wrapper` and its `__adsRef` at event time behind an element-scoped guard, so a stencil drop still reaches the canvas after two wrapper replacements — one invocation on the live node, zero on the detached ones, zero and no error when no zone exists.**

## Performance

- **Duration:** not instrumented (no plan-start timestamp was captured; wall-clock is not claimed)
- **Completed:** 2026-10-21
- **Tasks:** 3/3
- **Files modified:** 1 (`src/AzureDesignStudio/wwwroot/js/ads.js`)
- **Commits:** 0 — the task explicitly forbids committing; plan 01-03 owns the shell commit.

## Accomplishments

- Reproduced the defect in a real browser before touching the module: three `init` calls after two wrapper replacements produced zero `invokeMethodAsync` calls, with the replacement node live and the pointer coordinates inside its 400×200 rect.
- Replaced the window-scoped `window.__adsStencilPointerInit` guard with `zone.__adsPointerInit` (set before the `window.__adsPointerDocInit` early return) and one-time document listeners.
- Routed every `zone` read (`inZone`, `clear`, hover/active class writes, `__adsRef` read) through `document.querySelector(selector)` at event time, each tolerating a null result.
- Proved the fix with the same harness: exactly one `HandleDropAtAsync` on the live node `b`, none for `old`/`a`, and a zero-zone cycle with 0 calls and no thrown error.

## SC#4 evidence

### 1. Harness route

**Real browser — headless Microsoft Edge (`--dump-dom`). No DOM shim was used.** No mock DOM, no fake `DotNetObjectReference`: the page loads the repository module itself and captures calls on real `PointerEvent`s.

Files (outside the repository, deleted in Task 3):

- `C:/Users/jakub/AppData/Local/Temp/ads-land-harness/harness.html` — wrapper + tile markup, a persistent `#probe` tile for the zero-zone cycle, and an inline classic script. The script installs a recording `__adsRef` on the first wrapper (`old`), calls `init`, replaces the wrapper with `a` + ref + `init`, replaces again with `b` + ref + `init`, then dispatches `pointerdown` (left+10/top+10), `pointermove` (left+40/top+40, past the 4 px threshold) and `pointerup` (left+60/top+60) on the tile inside `b`.
- `C:/Users/jakub/AppData/Local/Temp/ads-land-harness/run-harness.mjs` — dependency-free launcher.

Exact command line (run from the workspace root):

```
node "C:/Users/jakub/AppData/Local/Temp/ads-land-harness/run-harness.mjs" "$(git rev-parse --show-toplevel)"
```

The launcher resolves the checkout with `git -C <repo> rev-parse --show-toplevel`, rewrites the `__ADS_JS_URL__` token in `harness.html` to that file's `file:///` URL, and spawns:

```
"C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless --disable-gpu --no-sandbox
  --allow-file-access-from-files --virtual-time-budget=2000
  --user-data-dir=<temp>/profile-XXXX --dump-dom file:///.../harness.generated.html
```

Module under test for both runs: `file:///C:/Users/jakub/.omp/wt/t5180d7106/m/src/AzureDesignStudio/wwwroot/js/ads.js` (the repository file, not a copy).

Harness controls in the transcript: `liveRect` (the live wrapper's measured rect), `inLiveRect` (the dispatched coordinates really are inside it), and `initCalls` (3). The pre-fix run also reports `legacyWindowGuard:true` and `bGuarded:false`, proving the module executed and bailed on the old window guard rather than never running.

### 2. BEFORE (unfixed working-tree `ads.js`)

`git hash-object src/AzureDesignStudio/wwwroot/js/ads.js` before and after the run: `3adf0aecdfdafc0368b249b2f6208f20960b7880` (unchanged).

```
RESULT={"calls":[],"errors":[],"liveRect":{"left":8,"top":26,"width":400,"height":200},"inLiveRect":true,"liveIsReplacement":true,"bGuarded":false,"docGuard":false,"bodyDraggingCleared":true,"ghosts":0,"legacyWindowGuard":true,"initCalls":3,"zero":{"zoneAbsent":true,"calls":0,"error":null,"ghostsAfter":0,"bodyDraggingCleared":true}}
```

**Why this demonstrates the defect:** the live wrapper is the replacement node (`liveIsReplacement:true`), the pointer cycle ran fully inside its real 400×200 rect (`inLiveRect:true`), and all three `init` calls happened (`initCalls:3`) — yet `calls` is empty. `legacyWindowGuard:true` with `bGuarded:false` shows the first `init` set `window.__adsStencilPointerInit` and the second and third returned early, so the listeners kept measuring the *first* wrapper. That node is detached, so `getBoundingClientRect()` is all zeros, `inZone` returns false, and `finish` never reaches `__adsRef`. The failure is silent: `errors` is empty, no ghost leaks, and `ads-dragging` is cleared — exactly the "drag does nothing, no console error" symptom.

### 3. AFTER (fixed working-tree `ads.js`)

```
RESULT={"calls":[["b",["HandleDropAtAsync",68,86,"virtual-network"]]],"errors":[],"liveRect":{"left":8,"top":26,"width":400,"height":200},"inLiveRect":true,"liveIsReplacement":true,"bGuarded":true,"docGuard":true,"bodyDraggingCleared":true,"ghosts":0,"legacyWindowGuard":false,"initCalls":3,"zero":{"zoneAbsent":true,"calls":0,"error":null,"ghostsAfter":0,"bodyDraggingCleared":true}}
```

| Observation | Required | Observed |
|---|---|---|
| Invocations on the live node `b` | exactly 1 | 1 (`["b",["HandleDropAtAsync",68,86,"virtual-network"]]`) |
| Invocations on detached `old` / `a` | 0 | 0 (no `["old",` or `["a",` in `calls`) |
| Method + key | `HandleDropAtAsync`, `virtual-network` | same |
| `liveIsReplacement` | true | true |
| `bGuarded` (new element marked) | true | true |
| `docGuard` (listeners attached once) | true | true |
| `bodyDraggingCleared` | true | true |
| `ghosts` | 0 | 0 |
| Zero-zone cycle: calls | 0 | 0 |
| Zero-zone cycle: thrown error | none | `null` |
| `errors` (window `error` events) | empty | `[]` |

The pixel values (68, 86) are layout-dependent and are not asserted; only the method name and key are.

**Known limitation (stated honestly):** synthetic `PointerEvent`s exercise the listener logic, node resolution, and the ref read — the defect — but they do not exercise the browser's real hit-testing, pointer capture, or a signed-in Blazor circuit. That gap is what the human check in plan 01-07 adds. The harness is also not a substitute for TEST-04's committed Playwright E2E (Phase 8).

## Task Commits

None. Per the dispatch instruction and D-15, this plan does not commit: plan 01-03 stages `src/AzureDesignStudio/wwwroot/js/ads.js` inside the shell topic.

## Files Created/Modified

- `src/AzureDesignStudio/wwwroot/js/ads.js` — `window.AdsStencilDrag` only: element-scoped `zone.__adsPointerInit`, one-time `window.__adsPointerDocInit` document listeners, and `liveZone()` re-resolution for `inZone`, `clear`, the `ads-drop-active` add, the `ads-drop-hover` toggle, and the `__adsRef` read in `finish`. Blob `3adf0ae` → `2a160fc`.

## Confinement proof

### `git diff --stat -- src/AzureDesignStudio/wwwroot/js/ads.js`

```
 src/AzureDesignStudio/wwwroot/js/ads.js | 245 +++++++++++++++++++++++++++++++-
 1 file changed, 241 insertions(+), 4 deletions(-)
```

That range is **against HEAD, which contains no `AdsStencilDrag` at all** (RESEARCH finding #2), so it includes the pre-existing +209/−4 working-tree slice and cannot isolate this plan's edit. The isolating instrument is the byte-exact pre-edit baseline:

```
baseline hash: 3adf0aecdfdafc0368b249b2f6208f20960b7880   (identical to C:/Users/jakub/Desktop/Projects/AzureDesignStudio/.../ads.js)
--- diff vs pre-edit baseline (stat) ---
 .../AzureDesignStudio/wwwroot/js/ads.js            | 50 ++++++++++++++++++----
 1 file changed, 41 insertions(+), 9 deletions(-)
```

### Hunk headers

Against the pre-edit baseline (`git diff --no-index -U0`), every hunk is inside `window.AdsStencilDrag` (which starts at line 124):

```
@@ -127 +127,8 @@ window.AdsStencilDrag = {
@@ -130 +137,12 @@ window.AdsStencilDrag = {
@@ -135 +153,5 @@ window.AdsStencilDrag = {
@@ -141,2 +163,5 @@ window.AdsStencilDrag = {
@@ -185 +210,4 @@ window.AdsStencilDrag = {
@@ -199 +227,4 @@ window.AdsStencilDrag = {
@@ -211,2 +242,3 @@ window.AdsStencilDrag = {
```

Minimum new-file hunk start: **127**, i.e. after the `AdsStencilDrag` comment block at line 118. No hunk touches the `pointerdown` handler body, `AdsDropZone`, `AdsTheme`, or `AdsShortcuts`.

### Neighbours byte-identical (sha256 of the raw text)

| Region | Baseline | Edited |
|---|---|---|
| Prefix, lines 1–126 (all of `AdsDropZone`) | `0ed63be7df1186dbe4f7dd9b2ffa39df998de14b932724dea7b347c2f42fa17d` | same |
| Suffix from `/* ---------- shell: theme + keyboard ---------- */` (all of `AdsTheme` + `AdsShortcuts`) | `f510fcfb8628770ae74db660ab69ea59873e2af65ae2b4fb0fa3bc8965d64395` | same |
| `AdsDropZone` body, lines 56–116 | `586376895fe8c471d75a613ea988947e9cf4196dd864ce8509d138cc9746a460` | same |

The file remains UTF-8 with BOM and **pure CRLF** (325/325 lines carry CRLF); the diff is 7 line-level hunks, not a whole-file rewrite.

### Guard audit

```
__adsStencilPointerInit=0 __adsPointerInit=2 __adsPointerDocInit=2
```

`grep -c` counts: the legacy window flag is gone; `__adsPointerInit` is the test + set pair; `__adsPointerDocInit` is the test + set pair.

### No `.catch` / `try`

The diff adds no `try`, `catch`, or `.catch` anywhere. The absent-zone path is an explicit branch (`if (!z) return false;`, `if (z) { ... }`, `dropZone && dropZone.__adsRef`).

## Repository cleanliness

Harness directory deleted in Task 3 (`test ! -d .../ads-land-harness` → **ABSENT**):

```
git ls-files | grep -ci harness            → 0
git status --porcelain | grep -ci harness  → 0
```

`git status --porcelain | grep -vE '^\?\? \.planning/'` — 41 paths, exactly the pre-existing baseline dirty set (no harness, test, or tooling path):

```
 M README.md
 M docs/top-100-ui-improvements.md
 M src/AzureDesignStudio.Core.Tests/StencilTests.cs
 M src/AzureDesignStudio.Desktop/MauiProgram.cs
 M src/AzureDesignStudio.Desktop/wwwroot/app.html
 M src/AzureDesignStudio/Components/DiagramNavigator.razor
 M src/AzureDesignStudio/Components/DiagramNavigator.razor.css
 M src/AzureDesignStudio/Components/DiagramPanel.razor
 M src/AzureDesignStudio/Components/DiagramPanel.razor.cs
 M src/AzureDesignStudio/Components/DiagramPanel.razor.css
 M src/AzureDesignStudio/Components/Stencil.razor
 M src/AzureDesignStudio/Components/Stencil.razor.css
 M src/AzureDesignStudio/Components/StencilPanel.razor
 M src/AzureDesignStudio/Components/StencilPanel.razor.css
 M src/AzureDesignStudio/Components/TopMenu.razor
 M src/AzureDesignStudio/Components/TopMenu.razor.cs
 M src/AzureDesignStudio/Components/TopMenu.razor.css
 M src/AzureDesignStudio/Models/StencilModel.cs
 M src/AzureDesignStudio/Pages/Index.razor
 M src/AzureDesignStudio/Pages/Index.razor.css
 M src/AzureDesignStudio/Program.cs
 M src/AzureDesignStudio/Shared/MainLayout.razor
 M src/AzureDesignStudio/wwwroot/ads-stencils.json
 D src/AzureDesignStudio/wwwroot/assets/icons/others.svg
 M src/AzureDesignStudio/wwwroot/index.html
 M src/AzureDesignStudio/wwwroot/js/ads.js
?? docs/assessment-2026-10-21.md
?? docs/next-phases-plan.md
?? docs/v3-plan.md
?? src/AzureDesignStudio/Components/CommandPalette.razor
?? src/AzureDesignStudio/Components/DesignInspector.razor
?? src/AzureDesignStudio/Components/StatusBar.razor
?? src/AzureDesignStudio/Components/ThemeToggle.razor
?? src/AzureDesignStudio/Services/AdsCommandService.cs
?? src/AzureDesignStudio/wwwroot/assets/azure/subnet.svg
?? src/AzureDesignStudio/wwwroot/assets/icons/ai.svg
?? src/AzureDesignStudio/wwwroot/assets/icons/containers.svg
?? src/AzureDesignStudio/wwwroot/assets/icons/general.svg
?? src/AzureDesignStudio/wwwroot/assets/icons/integration.svg
?? src/AzureDesignStudio/wwwroot/assets/icons/security.svg
?? src/AzureDesignStudio/wwwroot/css/ads-tokens.css
```

`ads.js` was already `M` in that baseline; this plan is the only thing that changed it. `Stencil.razor` still shows only the pre-existing `draggable="true"` → `"false"` flip (plus the new icon `draggable="false"`), untouched by this plan.

## Decisions Made

- Captured BEFORE against the unfixed working-tree module rather than a commit, because HEAD has no `AdsStencilDrag` (RESEARCH finding #2 contradicts D-14's "pre-fix commit" wording).
- Used the real headless browser instead of the shim fallback: `msedge.exe` was present at the plan's exact path, so the authoritative route was available.
- Added an `inLiveRect` control to the harness so the BEFORE miss cannot be explained by bad coordinates.
- Proved confinement against the byte-exact pre-edit baseline (hash `3adf0ae`, cross-checked against the parent checkout) instead of HEAD.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Task 2's verify command mangled the harness path under MSYS**
- **Found during:** Task 2 verification
- **Issue:** `H=/c/Users/...; node "$H/run-harness.mjs"` passed `/c/Users/...` to `node.exe`, which resolved it as `C:\c\Users\...` → `Error: Cannot find module 'C:\c\Users\jakub\AppData\Local\Temp\ads-land-harness\run-harness.mjs'`.
- **Fix:** invoked the launcher with the Windows path form (`C:/Users/jakub/AppData/Local/Temp/ads-land-harness/run-harness.mjs`), as every successful run in this summary does. No repository change.
- **Verification:** the full Task 2 verify chain printed `VERIFY_OK`.

### Plan assertions that do not hold as written (reported, not silently worked around)

**2. [Rule 4 - Architectural/instrument] Task 3's `min(hunk start) >= 118` check fails against HEAD**
- **Found during:** Task 3
- **Issue:** the plan's literal check `git diff -U0 -- ads.js` yields hunks `[71, 80, 85, 100, 109, 114]` and exits 1. Those hunks belong to the **pre-existing** working-tree slice, not to this plan: HEAD's `ads.js` already contains `AdsDropZone`, and the uncommitted slice edits it (adopt-newest-ref comment, `dragenter` cancel, `clearDropState`) before inserting `AdsStencilDrag` at line 114. The plan's instrument implicitly assumed a pre-fix commit containing `AdsStencilDrag`, which RESEARCH already established does not exist.
- **Resolution:** ran the same hunk-range assertion against the byte-exact pre-edit baseline (`3adf0ae`, proven identical to the parent checkout's copy): hunks `[127, 137, 153, 163, 210, 227, 242]`, min 127 ≥ 118, all labelled `window.AdsStencilDrag = {`. Both outputs are recorded above. No code change was needed; the plan's stated threshold is satisfied by the correct baseline.

**3. [Rule 4 - Instrument] Task 2's `git diff --numstat` "fewer than 120 added lines" holds only against the baseline**
- Against HEAD the numstat is `241 4` because the whole module is new relative to HEAD; against the pre-edit baseline it is `41 9`. The intent (<120 added, pointerdown body intact) is met by the baseline measurement.

## Issues Encountered

- `cmp <(head -n … fileA) <(head -n … fileB)` is unavailable in this shell (`/dev/fd/63: The system cannot find the path specified`); byte equality was instead proven by comparing `sha256sum` of the exact line ranges (table above).
- `git diff --no-index` exits 1 when the files differ, so its exit code is expected and was not treated as a failure.

## User Setup Required

None.

## Next Phase Readiness

- The edited module sits uncommitted in the working tree exactly as plan 01-03 expects; it must be staged by explicit path inside the shell topic, never with `git add -A`.
- Plan 01-07's human check should confirm real hit-testing in the running app (collapse/expand + pointer drag, zero console errors), since synthetic events do not cover it.
- Nothing else in the repository was added, moved, or deleted by this plan.

---

*Phase: 01-land-and-stabilize*
*Completed: 2026-10-21*

## Self-Check: PASSED
