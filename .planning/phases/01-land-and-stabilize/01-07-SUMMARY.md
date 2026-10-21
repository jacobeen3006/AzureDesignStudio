---
phase: 01-land-and-stabilize
plan: 07
subsystem: verification
tags: [git-bundle, fresh-clone, citation-audit, dotnet-test, cdp, dom-probe, sc4, phase-gate]

requires:
  - phase: 01-land-and-stabilize
    provides: "the three land commits on origin/main (01-04/01-05) and the clone-verified recovery bundle at BUNDLE_SHA 3127ba3 (01-06)"
provides:
  - "SC#1 proven on a real clone of the recovery bundle: clean tree, no tracked DLL/.omo/.swarm, 37 tracked .planning files, 11 project files, 8/8 cited docs present"
  - "SC#2 clone leg: the clone's HEAD equals the BUNDLE_SHA recorded in 01-06 and nothing outside .planning changed after the cut"
  - "SC#3: offline suite Passed 755 / Failed 0 at the local head, with the 753+2 arithmetic recorded"
  - "SC#4: DOM-identity probe answered true (collapse/expand does not recreate .ads-diagram-wrapper); real-app pointer drop created a node with zero drop-phase console entries; primary proof remains the 01-02 harness"
affects: [01-08, phase-gate]

actuals:
  tokens: 4196
  tasks: 3
  commits: 0              # measured before this plan's own metadata commit: `git rev-list --count 57226f3..HEAD` = 0; the metadata commit itself is counted separately
plan_head_before: 57226f36230b84c9bc12c480859f8b0f7e5ede17
plan_head_after: 57226f36230b84c9bc12c480859f8b0f7e5ede17

tech-stack:
  added: []
  patterns:
    - "Bundle-anchored verification: compare a clone to a recorded BUNDLE_SHA, never to a live head, because the workflow's own metadata commits land afterwards"
    - "Phase-gate manual evidence recorded as three column-0 labelled lines with exact allowed values"

key-files:
  created: []
  modified:
    - .planning/phases/01-land-and-stabilize/01-07-SUMMARY.md

key-decisions:
  - "SC#2 is asserted in its supersession-proof form: clone HEAD == recorded BUNDLE_SHA plus a zero non-.planning unpushed count, not a live head equality"
  - "The DOM-identity probe returned true, so no line claims a remount and SC#4's primary proof is named as the 01-02 harness"
  - "The real-app drop is recorded as observed with its console qualification rather than softened: the only console errors are nine pre-existing Application Insights 400s at load, and the error count is unchanged across the drop"

requirements-completed: [SC1, SC2, SC4]

coverage:
  - id: D1
    description: "SC#1 fresh-clone audit of the recovery bundle: clean clone, no tracked DLL/.omo/.swarm, .planning tracked (37), 11 project files, all README/AGENTS docs paths tracked"
    requirement: SC1
    verification:
      - kind: other
        ref: "git clone ../AzureDesignStudio-3127ba3-20261021.bundle C:/Users/jakub/AppData/Local/Temp/ads-fresh-audit → status 0 lines, predicate 0 matches, .planning 37, csproj 11, citations 8/8"
        status: pass
    human_judgment: false
  - id: D2
    description: "SC#2 published-content leg: clone HEAD == BUNDLE_SHA 3127ba3 recorded in 01-06, and zero non-.planning paths changed after the cut"
    requirement: SC2
    verification:
      - kind: other
        ref: "git rev-parse HEAD in clone = 3127ba3a0e22b4200de0a46b9738f8b4cd58490a; git rev-list --count 3127ba3..HEAD -- . ':!.planning/' = 0"
        status: pass
    human_judgment: false
  - id: D3
    description: "SC#3 offline suite green at the local head: 755 passed, 0 failed, 0 skipped"
    requirement: SC3
    verification:
      - kind: unit
        ref: "dotnet test src/AzureDesignStudio.Core.Tests --filter \"Category!=LiveAzure\" → Passed! Failed: 0, Passed: 755, Skipped: 0, Total: 755"
        status: pass
    human_judgment: false
  - id: D4
    description: "SC#4 browser leg recorded honestly: DOM-identity probe plus real-app pointer drop, with the harness named as primary proof"
    requirement: SC4
    verification:
      - kind: manual_procedural
        ref: "headless Edge over CDP (ads-probe-07b.mjs): probe sameNode=true; Input.dispatchMouseEvent drop of virtual-network grew .ads-diagram-canvas descendants 2→11 with zero drop-phase console entries"
        status: pass
    human_judgment: true
    rationale: "A browser interaction is not covered by any committed automated test in this phase (D-15 defers TEST-04's Playwright E2E to Phase 8); the recorded transcript is the evidence a human reviews."

# Metrics
duration: 9min
completed: 2026-10-21
status: complete
---

# Phase 01 Plan 07: Phase-Gate Evidence Summary

**A real clone of the 95 MB recovery bundle restores the published land head 3127ba3 with a clean tree and exactly the tracked source, the local head re-runs green at 755/0, and the SC#4 browser question is answered by an executed probe: collapse/expand does not recreate `.ads-diagram-wrapper`, so the 01-02 harness remains the primary proof while the real-app drop did create a node.**

## Performance

- **Duration:** 9 min
- **Started:** 2026-10-21T10:38:00Z
- **Completed:** 2026-10-21T10:47:00Z
- **Tasks:** 3
- **Files modified:** 0 tracked source/docs paths (this plan's only commit is the metadata commit below)

## SC#1 fresh-clone audit

Bundle resolved as the single `../AzureDesignStudio-*.bundle` beside the repository:

```
$ ls -l ../AzureDesignStudio-3127ba3-20261021.bundle
-rwxrwxrwx 1 somebody somegroup 99616738 Oct 21 12:21 ../AzureDesignStudio-3127ba3-20261021.bundle
$ rm -rf C:/Users/jakub/AppData/Local/Temp/ads-fresh-audit
$ git clone C:/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle C:/Users/jakub/AppData/Local/Temp/ads-fresh-audit
Cloning into 'C:/Users/jakub/AppData/Local/Temp/ads-fresh-audit'...
```

Predicates inside the clone:

```
$ git -C <clone> status --porcelain | wc -l
0
$ git -C <clone> ls-files | grep -E '\.dll$|^\.(omo|swarm)/'
(no output; predicate_exit=1 → zero matches = PASS)
$ git -C <clone> ls-files .planning | wc -l
37
$ git -C <clone> ls-files 'src/**/*.csproj' | wc -l
11
$ git -C <clone> ls-files | wc -l
1021
$ git -C <clone> rev-parse HEAD
3127ba3a0e22b4200de0a46b9738f8b4cd58490a
```

Citation audit (run inside the clone; extraction asserted non-empty before matching):

```
$ { grep -hoE 'docs/[A-Za-z0-9._/-]+\.md' README.md; grep -hoE 'docs/[A-Za-z0-9._/-]+\.md' AGENTS.md; } | sort -u | wc -l
8
docs/architecture.md
docs/assessment-2026-10-21.md
docs/entra-setup.md
docs/next-phases-plan.md
docs/top-100-improvements.md
docs/top-100-ui-improvements.md
docs/v2-plan.md
docs/v3-plan.md
$ git -C <clone> ls-files --error-unmatch <path>   # per path
OK docs/architecture.md
OK docs/assessment-2026-10-21.md
OK docs/entra-setup.md
OK docs/next-phases-plan.md
OK docs/top-100-improvements.md
OK docs/top-100-ui-improvements.md
OK docs/v2-plan.md
OK docs/v3-plan.md
```

**SC#1: PASS** — 8 extracted citations, 8 tracked; extraction was non-empty, so the audit cannot read clean by accident. This is the leg that would have failed before the land, when `docs/v3-plan.md`, `docs/next-phases-plan.md`, and `docs/assessment-2026-10-21.md` were untracked while README already linked them.

## SC#2 clone comparison against the recorded BUNDLE_SHA

```
$ SHA=$(git -C <clone> rev-parse HEAD)
$ echo $SHA
3127ba3a0e22b4200de0a46b9738f8b4cd58490a
$ grep -cF "$SHA" .planning/phases/01-land-and-stabilize/01-06-SUMMARY.md
14
$ git rev-list --count "$SHA"..HEAD -- . ':!.planning/'
0
$ git rev-parse HEAD
57226f36230b84c9bc12c480859f8b0f7e5ede17
```

The bundle restores the published content at `BUNDLE_SHA`; the source repository is exactly one `.planning`-only commit ahead, and no non-`.planning` path changed after the cut. No comparison to a live head is made (prohibited by the plan: the `docs(01-07): complete …` commit lands after this measurement).

## SC#3 suite, ancestry and remote observation

```
$ dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"
Passed!  - Failed:     0, Passed:   755, Skipped:     0, Total:   755, Duration: 1 s - AzureDesignStudio.Core.Tests.dll (net10.0)

$ DOCS=$(git log --format=%H -n 1 --fixed-strings --grep='docs: correct project count and auth model; record publisher and signing route')
$ echo $DOCS
3315b9473fd6d8b583e5409ed451474181c24525
$ git log -1 --format='%h %s' "$DOCS"
3315b94 docs: correct project count and auth model; record publisher and signing route
$ git merge-base --is-ancestor "$DOCS" origin/main; echo $?
0
$ git rev-list --count origin/main..HEAD -- . ':!.planning/'
0
$ git status --porcelain --untracked-files=no | wc -l
0
```

Timestamped observation only (not a standing equality claim):

```
2026-10-21T10:41:04Z
HEAD=57226f36230b84c9bc12c480859f8b0f7e5ede17
ls_remote=3127ba3a0e22b4200de0a46b9738f8b4cd58490a
```

`origin/main` at that instant was the land head `3127ba3`; the local head was one `.planning`-only commit ahead. Plan 01-08 owns the closing literal equality and the bundle re-cut.

Clean-tree measurement: `git status --porcelain --untracked-files=no` reports 0 lines before and after the suite. The working tree also carries four untracked orchestrator files under `.planning/` — `.omp-checkpoint.json`, `.omp-next-action.json`, `.omp-task-results.json`, `milestone.lock` — which are live tool state and are excluded by the `--untracked-files=no` form; they are neither deleted, staged, nor gitignored.

SC#3 arithmetic recorded verbatim:

`Suite: 753 at core + 2 StencilTests facts in shell = 755 from the shell commit onward (D-03)`

## SC#5 grep audits

```
$ grep -rc "8 projects" README.md AGENTS.md .planning/codebase/CONCERNS.md docs/*.md | grep -v ':0$'
docs/assessment-2026-10-21.md:1
docs/azure-refresh-plan.md:1
docs/next-phases-plan.md:2
docs/v1-plan.md:1
docs/v3-plan.md:1
$ grep -rn "AAD B2C" AGENTS.md README.md | wc -l
0
$ grep -n "Jakub Talaj" .planning/PROJECT.md docs/entra-setup.md
.planning/PROJECT.md:84:| Publisher legal name and signing route: `CN=Jakub Talaj` (individual validation) via Azure Artifact Signing | ...
docs/entra-setup.md:88:Signing) with individual validation, certificate subject `CN=Jakub Talaj`
```

README.md and AGENTS.md carry zero `8 projects` statements (per-file counts omit them). The five remaining hits are historical or defect-record prose: the 2026-10-21 assessment quoting the defect, the historical refresh/v1 plans describing past state, and the next-phases plan describing the drift it records. No current-truth statement remains in the two files SC#5 names, and the publisher decision is present in both required places.

## SC#4 — DOM-identity probe and real-app pointer smoke

Environment: app built (`dotnet build src/AzureDesignStudio` → Build succeeded, 0 warnings, 0 errors) and served at `http://127.0.0.1:5199`; headless Edge driven over CDP by a throwaway script at `C:/Users/jakub/AppData/Local/Temp/ads-probe-07b.mjs` (never in the repository). The page rendered the diagram without sign-in (`wrapperPresent: true`, 13 visible stencil tiles), so the probe was executed rather than parked behind the placeholder `ClientId`.

Raw result (abridged to the observed fields):

```
{"url":"http://127.0.0.1:5199/","wrapperPresent":true,
 "probe":"clicked-close","expand":"clicked-open","sameNode":true,
 "wrapperCount":1,"refPresent":true,
 "geo":{"tile":"virtual-network","tx":124,"ty":221,"cx":350,"cy":174,
        "canvasDescendantsBefore":2,"canvasChildrenBefore":2,"canvasHtmlLengthBefore":277},
 "after":{"canvasDescendantsAfter":11,"canvasChildrenAfter":2,"canvasHtmlLengthAfter":1159},
 "pointerCounters":{"down":1,"move":11,"up":1},
 "errorsAtDropStart":9,"errorsAtDropEnd":9,
 "httpErrors":["load 400 https://dc.services.visualstudio.com/v2/track", "... x4"]}
```

Interpretation: `sameNode: true` — after collapsing and re-expanding the stencil rail, `document.querySelector('.ads-diagram-wrapper') === window.__probe`, with one wrapper in the DOM. Collapse/expand does **not** recreate the wrapper, so this phase cannot claim an in-app remount as SC#4's proof.

The pointer drop used CDP `Input.dispatchMouseEvent` (1 pointerdown, 11 pointermoves, 1 pointerup) from the `virtual-network` tile onto the canvas centre; the canvas's descendant count grew 2 → 11 and its HTML length 277 → 1159, i.e. a node was created.

Console qualification, stated rather than softened: the console was **not** globally silent — nine `Failed to load resource: … 400` entries were present, all from Application Insights uploads to `https://dc.services.visualstudio.com/v2/track`, all captured in the `load` phase before the drop. The error count was 9 immediately before the drag and 9 immediately after it (`errorsAtDropStart` = `errorsAtDropEnd` = 9); no console entry carried a `drop` phase. The drop itself produced no error.

Remount probe: true
Real-app drop: observed — node created (canvas descendants 2→11); zero console entries during the drop; nine pre-existing Application Insights 400s at load are the only console errors and their count is unchanged across the drop
SC#4 primary proof: harness

The deterministic primary proof is the plan 01-02 harness transcript: with three `init` calls after two wrapper replacements, exactly one `HandleDropAtAsync` fired on the live node `b` (`RESULT={"calls":[["b",["HandleDropAtAsync",68,86,"virtual-network"]]],...}`), and the BEFORE run against the unfixed module produced zero calls. It covers the replacement path this probe just showed the real app does not exercise through collapse/expand, needs no credentials, and remains the reachable SC#4 proof for this phase.

## Cleanup

```
$ rm -rf C:/Users/jakub/AppData/Local/Temp/ads-fresh-audit
$ test -d C:/Users/jakub/AppData/Local/Temp/ads-fresh-audit && echo yes || echo no
no
$ ls -l C:/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle
... 99616738 ... AzureDesignStudio-3127ba3-20261021.bundle
$ ls -l C:/Users/jakub/AppData/Local/Temp/ads-final-test.log
... 1474 ... ads-final-test.log
```

The temporary clone is gone; the test log is retained; the recovery bundle remains at the project parent (99,616,738 bytes). Deletion stays with plan 01-08 (superseded copy in Task 2, converged copy at the recorded gate step). Both services were stopped and no process listens on 5199 or 9333 (`netstat … LISTENING` → 0).

## Task Commits

1. **Task 1: fresh-clone audit** — verification only, no repository file changed
2. **Task 2: suite and remote invariant** — verification only, no repository file changed
3. **Task 3: SC#4 record and cleanup** — verification only; this summary is the artefact

**Plan metadata:** the `docs(01-07): complete …` commit created after this file (commits measured at SUMMARY write: 0).

## Files Created/Modified

- `.planning/phases/01-land-and-stabilize/01-07-SUMMARY.md` — this phase-gate evidence block
- Temporary, outside the repository: `ads-fresh-audit` (deleted), `ads-probe-07b.mjs` (throwaway CDP driver), `ads-final-test.log` (retained)

## Decisions Made

- SC#2 is recorded in the supersession-proof form the plan mandates: clone HEAD equals the `BUNDLE_SHA` written in 01-06, plus `git rev-list --count "$SHA"..HEAD -- . ':!.planning/'` = 0. No live-head equality is asserted.
- With `sameNode: true`, the summary names the 01-02 harness as the primary SC#4 proof and does not claim an in-app remount.
- The real-app drop is recorded as observed with an explicit console qualification (nine pre-existing telemetry 400s, zero drop-phase entries) instead of being downgraded to blocked, because nothing blocked the interaction and no error was produced by it.

## Deviations from Plan

None — the plan's commands were executed as written, with two environment-level adjustments that changed no repository file:

1. **MSYS path translation.** `git` in this shell is a native Windows binary; POSIX paths like `/c/Users/...` are not translated, so the first clone landed at `C:/c/Users/...`. The stray directory was removed and every subsequent call used an absolute `C:/...` path. No repository state was touched.
2. **Browser availability.** Chrome is not installed; the probe used the installed Microsoft Edge in headless mode over CDP. The probe semantics and the recorded values are unchanged.

## Issues Encountered

- The first headless app launch attempt (backgrounded subshell) produced an empty log and no listener; it was re-run as a managed service after `dotnet build`, which started cleanly and served the page. No process was left listening.

## Next Phase Readiness

- SC#1, the published-content leg of SC#2, SC#3 and the answer to research Q1 are now recorded evidence.
- Ready for plan 01-08: re-cut the bundle at the converged head, delete the superseded copy, and perform the closing literal `origin/main == HEAD` push check.
- Phase-record caveat: the in-app browser leg does not exercise a remount (probe true), so TEST-04's committed Playwright E2E (Phase 8) remains the durable SC#4 automation.

---
*Phase: 01-land-and-stabilize*
*Completed: 2026-10-21*

## Self-Check: PASSED

- `01-07-SUMMARY.md` exists; the three labelled lines each appear once with allowed values; the `753 at core + 2 StencilTests facts in shell = 755` arithmetic is present; the probe/proof consistency rule holds (`Remount probe: true` ⇒ proof `harness`).
- The metadata commit exists at HEAD and contains `.planning/phases/01-land-and-stabilize/01-07-SUMMARY.md`, `.planning/STATE.md`, `.planning/state.json`, and `.planning/ROADMAP.md`; the plan's task work produced zero repository commits, and `git rev-list --count 57226f3..HEAD` = 1 after the metadata commit.
- `.planning/phases/01-land-and-stabilize/01-07-SUMMARY.md`, `.planning/STATE.md`, `.planning/state.json`, `.planning/ROADMAP.md` all present in the commit.
- Non-`.planning` unpushed count = 0; temporary clone absent; recovery bundle present at 99,616,738 bytes; test log retained.
