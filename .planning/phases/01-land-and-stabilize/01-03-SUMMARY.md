---
phase: 01-land-and-stabilize
plan: 03
subsystem: shell-ui
tags: [git, commit-slicing, worktree, xunit, blazor, wasm, maui, pointer-events]

# Dependency graph
requires:
  - phase: 01-land-and-stabilize
    provides: "core land commit 078a9d7 on top of pre-land tip e9078d1, plus the proven worktree cycle (add → build → test → remove --force) from plan 01-01"
  - phase: 01-land-and-stabilize (plan 01-02)
    provides: "remount-safe AdsStencilDrag repair in the working-tree copy of src/AzureDesignStudio/wwwroot/js/ads.js"
provides:
  - "Shell land commit 8f5b7f1e97dcc2b500c16148625ab6625d4ac896 (35 paths) on top of dfeac63436b0b4003d2011c9e47ce849ea39e8b6"
  - "First history entry containing the SC#4 pointer-drag re-resolution fix"
  - "Stencil catalog (categories 0-9) and its two new facts landed in the same commit as the linked test project"
  - "Restored src/AzureDesignStudio/wwwroot/assets/icons/others.svg (blob 44a4c0a, byte-identical to HEAD, not in the commit)"
  - "Worktree proof at the shell SHA: full solution build incl. AzureDesignStudio.Desktop, offline suite Passed: 755 / Failed: 0"
affects: [01-05, 01-06, 01-07]

actuals:
  tokens: 25171
  tasks: 3
  commits: 1

tech-stack:
  added: []
  patterns:
    - "Two-topic dependency is carried in one commit when a test project links a runtime asset (Core.Tests.csproj:34 links wwwroot/ads-stencils.json)"
    - "Restore-then-stage for a dangling tracked reference: git checkout HEAD -- file, prove git hash-object equals the HEAD blob, then stage the rest"

key-files:
  created:
    - src/AzureDesignStudio/Components/CommandPalette.razor
    - src/AzureDesignStudio/Components/DesignInspector.razor
    - src/AzureDesignStudio/Components/StatusBar.razor
    - src/AzureDesignStudio/Components/ThemeToggle.razor
    - src/AzureDesignStudio/Services/AdsCommandService.cs
    - src/AzureDesignStudio/wwwroot/css/ads-tokens.css
    - src/AzureDesignStudio/wwwroot/assets/azure/subnet.svg
    - src/AzureDesignStudio/wwwroot/assets/icons/ai.svg
    - src/AzureDesignStudio/wwwroot/assets/icons/containers.svg
    - src/AzureDesignStudio/wwwroot/assets/icons/general.svg
    - src/AzureDesignStudio/wwwroot/assets/icons/integration.svg
    - src/AzureDesignStudio/wwwroot/assets/icons/security.svg
  modified:
    - src/AzureDesignStudio/wwwroot/js/ads.js
    - src/AzureDesignStudio/wwwroot/ads-stencils.json
    - src/AzureDesignStudio/wwwroot/index.html
    - src/AzureDesignStudio.Core.Tests/StencilTests.cs
    - src/AzureDesignStudio.Desktop/MauiProgram.cs
    - src/AzureDesignStudio.Desktop/wwwroot/app.html

key-decisions:
  - "Staged an explicit path list (src/AzureDesignStudio + src/AzureDesignStudio.Desktop + Core.Tests/StencilTests.cs); no -A/. and no csproj change"
  - "others.svg restored before staging, so the commit carries no deletion and no diff for that path (D-04)"
  - "Amend was not needed: the first commit passed every gate and the worktree proof on its own SHA"

patterns-established:
  - "Deletion-free proof for a restored file: git hash-object <path> equals git rev-parse HEAD:<path>, and the path is absent from git show --name-only"

requirements-completed: []

coverage:
  - id: D1
    description: "The Round 2 shell slice lands as one 35-path commit with no deletion, including the widened stencil catalog, the new assets/components, the Desktop host files, and StencilTests.cs"
    requirement: "SC3"
    verification:
      - kind: other
        ref: "git show --name-only --pretty=format: HEAD | sed '/^$/d' | wc -l → 35; git show --name-status --pretty=format: HEAD | grep -c '^D' → 0; path list in Task 3 audit"
        status: pass
    human_judgment: false
  - id: D2
    description: "The shell commit restores into a clean worktree at its own SHA, builds the full solution including the Windows Desktop host, and runs the offline suite at 755 passed / 0 failed"
    requirement: "SC3"
    verification:
      - kind: integration
        ref: "worktree at 8f5b7f1: dotnet build src/AzureDesignStudio.sln -v m → 'Build succeeded', 0 Error(s), line 53 'AzureDesignStudio.Desktop -> …'"
        status: pass
      - kind: unit
        ref: "dotnet test src/AzureDesignStudio.Core.Tests --filter \"Category!=LiveAzure\" → Passed! - Failed: 0, Passed: 755, Skipped: 0, Total: 755"
        status: pass
    human_judgment: false
  - id: D3
    description: "The dangling fallback reference assets/icons/others.svg is restored byte-identically and produces no diff in the shell commit"
    requirement: "SC4"
    verification:
      - kind: other
        ref: "git hash-object src/AzureDesignStudio/wwwroot/assets/icons/others.svg → 44a4c0ac02873e66df2f400e4dc4da3a68569ff5 (= HEAD:<path>); git diff --quiet HEAD -- <path> exit 0; path absent from commit"
        status: pass
    human_judgment: false
  - id: D4
    description: "The commit is single-topic and carries no credential-shaped string, no harness path, and no binary archive"
    requirement: "SC3"
    verification: []
    human_judgment: true
    rationale: "Reviewability is a judgement over the diff; the automated path-set, deletion, secret and archive scans pass (all zero), but the one-topic call belongs to the phase reviewer."

metrics:
  duration: 9min
  completed: 2026-10-21
status: complete
---

# Phase 01 Plan 03: Shell Land Commit Summary

**One 35-path shell commit (8f5b7f1) lands the Round 2 components, the categories-0-9 stencil catalog with its seven assets, the Desktop host changes, the first history copy of the SC#4 pointer repair, and StencilTests.cs — proven green from its own worktree at Passed: 755 / Failed: 0 with the full solution (Desktop included) building at 0 errors.**

## Performance

- **Duration:** ~9 min (3m42s of it the full solution build; suite 1 s)
- **Started:** 2026-10-21 ~12:03 local (first git inspection)
- **Completed:** 2026-10-21 ~12:09 local (build/test logs written 12:08)
- **Tasks:** 3 of 3
- **Files modified:** 35 committed paths (23 modified, 12 added, 0 deleted), 2271 insertions / 141 deletions

## Baseline (observed before any mutation)

```
$ git rev-parse HEAD
dfeac63436b0b4003d2011c9e47ce849ea39e8b6

$ git rev-parse HEAD^{tree}
cfc20c021dc17575291837b3e80ff80a194c9c00

$ git branch --show-current
(empty — detached HEAD in the OMP isolated workspace)

$ git status --porcelain | wc -l
39   # 25 modified, 1 deleted (others.svg), 13 untracked
```

Two baseline facts differ from the dispatch note and from the planning docs, both recorded rather than "corrected":

- **HEAD is `dfeac63`, not `078a9d7`.** `078a9d7` is the parent of `dfeac63`; the actual tip is `dfeac63 docs(01-01): complete core land commit and record worktree proof` (the 01-01 metadata commit). `078a9d7`'s tree is `fa6cb068…`; `dfeac63`'s tree adds only the four `.planning` metadata files, so the shell worktree was pinned to `dfeac63` and the shell commit's parent is `dfeac63`.
- **`01-02-SUMMARY.md` does not exist** in this checkout (`.planning/phases/01-land-and-stabilize/` holds plans 01-01…01-08, 01-CONTEXT, 01-DISCUSSION-LOG, 01-PATTERNS, 01-RESEARCH, 01-VALIDATION, and 01-01-SUMMARY.md only). Plan 01-02's repair was present in the working copy as described — verified directly rather than via a summary: `ads.js` contains the element-scoped guard `zone.__adsPointerInit`, the separate `window.__adsPointerDocInit`, and event-time `document.querySelector(selector)` re-resolution.

## The shell commit

| Field | Value |
|-------|-------|
| Full SHA | `8f5b7f1e97dcc2b500c16148625ab6625d4ac896` |
| Tree SHA | `d0efef502421d8c468f6a585e8d46c7298fcb14c` |
| Parent | `dfeac63436b0b4003d2011c9e47ce849ea39e8b6` |
| Subject | `feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag` |
| Committed paths | 35 |
| Commit stat | `35 files changed, 2271 insertions(+), 141 deletions(-)` |

Full committed path list (`git show --name-only --pretty=format: HEAD | sed '/^$/d'`, 35 lines):

```
src/AzureDesignStudio.Core.Tests/StencilTests.cs
src/AzureDesignStudio.Desktop/MauiProgram.cs
src/AzureDesignStudio.Desktop/wwwroot/app.html
src/AzureDesignStudio/Components/CommandPalette.razor
src/AzureDesignStudio/Components/DesignInspector.razor
src/AzureDesignStudio/Components/DiagramNavigator.razor
src/AzureDesignStudio/Components/DiagramNavigator.razor.css
src/AzureDesignStudio/Components/DiagramPanel.razor
src/AzureDesignStudio/Components/DiagramPanel.razor.cs
src/AzureDesignStudio/Components/DiagramPanel.razor.css
src/AzureDesignStudio/Components/StatusBar.razor
src/AzureDesignStudio/Components/Stencil.razor
src/AzureDesignStudio/Components/Stencil.razor.css
src/AzureDesignStudio/Components/StencilPanel.razor
src/AzureDesignStudio/Components/StencilPanel.razor.css
src/AzureDesignStudio/Components/ThemeToggle.razor
src/AzureDesignStudio/Components/TopMenu.razor
src/AzureDesignStudio/Components/TopMenu.razor.cs
src/AzureDesignStudio/Components/TopMenu.razor.css
src/AzureDesignStudio/Models/StencilModel.cs
src/AzureDesignStudio/Pages/Index.razor
src/AzureDesignStudio/Pages/Index.razor.css
src/AzureDesignStudio/Program.cs
src/AzureDesignStudio/Services/AdsCommandService.cs
src/AzureDesignStudio/Shared/MainLayout.razor
src/AzureDesignStudio/wwwroot/ads-stencils.json
src/AzureDesignStudio/wwwroot/assets/azure/subnet.svg
src/AzureDesignStudio/wwwroot/assets/icons/ai.svg
src/AzureDesignStudio/wwwroot/assets/icons/containers.svg
src/AzureDesignStudio/wwwroot/assets/icons/general.svg
src/AzureDesignStudio/wwwroot/assets/icons/integration.svg
src/AzureDesignStudio/wwwroot/assets/icons/security.svg
src/AzureDesignStudio/wwwroot/css/ads-tokens.css
src/AzureDesignStudio/wwwroot/index.html
src/AzureDesignStudio/wwwroot/js/ads.js
```

Set composition (measured): **12 `A`** entries (`CommandPalette`, `DesignInspector`, `StatusBar`, `ThemeToggle`, `AdsCommandService.cs`, `subnet.svg`, five category icons, `ads-tokens.css`) + **23 `M`** entries = **20 modified WASM paths + 2 Desktop paths + `StencilTests.cs`**. Zero `D` entries. This is exactly RESEARCH § SC#3's 35-path shell inventory.

Commit body as committed:

```
feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag

Add the command palette, design inspector, status bar, theme toggle, command service, and token stylesheet
Widen the stencil catalog to categories 0-9 with the new icon and subnet assets; the two stencil tests travel with it because the test project links the catalog
Resolve the pointer zone at event time so a recreated diagram wrapper keeps accepting drops (SC#4)
```

## Pre-commit gates (literal output)

The restore ran first: `git checkout HEAD -- src/AzureDesignStudio/wwwroot/assets/icons/others.svg` → `RESTORE_RC=0`; `git status --porcelain -- <path>` printed **nothing** (the ` D` entry was gone before staging).

```
GATE1 staged count:
35
GATE2 D-entry count:
0
GATE3 required paths:
src/AzureDesignStudio.Core.Tests/StencilTests.cs
src/AzureDesignStudio/wwwroot/ads-stencils.json
src/AzureDesignStudio/wwwroot/js/ads.js
GATE4 out-of-scope staged paths (should be empty):
GATE4_EXIT=1
GATE5 credential scan (should be empty):
GATE5_EXIT=1
GATE6 harness paths (should be empty):
GATE6_EXIT=1
GATE7 staged stat:
 src/AzureDesignStudio/wwwroot/js/ads.js            | 245 +++++-
 35 files changed, 2271 insertions(+), 141 deletions(-)
```

| Gate | Command | Output |
|------|---------|--------|
| Count 35 | `git diff --cached --name-only \| wc -l` | `35` |
| Zero deletions | `git diff --cached --name-status \| awk -F'\t' '$1=="D"{n++} END{print n+0}'` | `0` |
| Required paths | `git diff --cached --name-only \| grep -E 'StencilTests.cs\|ads-stencils.json\|wwwroot/js/ads.js'` | the three lines above |
| Allowed roots only | `git diff --cached --name-only \| grep -vE '^src/AzureDesignStudio/\|^src/AzureDesignStudio\.Desktop/\|^src/AzureDesignStudio\.Core\.Tests/StencilTests\.cs$'` | empty (exit 1) |
| Credential scan | `grep -inE 'clientsecret\|client_secret\|password=\|api[_-]?key\|BEGIN [A-Z ]*PRIVATE KEY\|AccountKey=\|SharedAccessKey'` over `git diff --cached -U0` | empty (exit 1) |
| No harness path | `git diff --cached --name-only \| grep -i harness` | empty (exit 1) |

The plan's Task 1 `<verify>` block was also run verbatim: `VERIFY01_RC=0`.

## Worktree proof (Task 2)

The plan's `<verify>` block was executed verbatim (worktree at `../ads-land-shell` pinned to `HEAD`, build logged to `/c/Users/jakub/AppData/Local/Temp/ads-land-shell-build.log`, suite tee'd to `/c/Users/jakub/AppData/Local/Temp/ads-land-shell-test.log`):

```
Preparing worktree (detached HEAD 8f5b7f1)
HEAD is now at 8f5b7f1 feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag
Passed!  - Failed:     0, Passed:   755, Skipped:     0, Total:   755, Duration: 1 s - AzureDesignStudio.Core.Tests.dll (net10.0)
verify rc=0
final_rc=0
```

Build (cold restore, full solution; build log lines 53, 146, 239-242):

```
 53:  AzureDesignStudio.Desktop -> C:\Users\jakub\.omp\wt\t7455d23f4\ads-land-shell\src\AzureDesignStudio.Desktop\bin\Debug\net10.0-windows10.0.19041.0\win-x64\AzureDesignStudio.Desktop.dll
146:Build succeeded.
239:    91 Warning(s)
240:    0 Error(s)
242:Time Elapsed 00:03:42.19
```

Test log tail (full file at `/c/Users/jakub/AppData/Local/Temp/ads-land-shell-test.log`):

```
Passed!  - Failed:     0, Passed:   755, Skipped:     0, Total:   755, Duration: 1 s - AzureDesignStudio.Core.Tests.dll (net10.0)
```

`Passed: 755` is the expected shell-state count: 753 at the core commit + the two new stencil facts (`EveryCategoryHasAtLeastOneStencil`, `EveryImplementedResourceTypeHasAStencil`) that travel with the catalog per D-03.

Teardown: `git worktree remove --force ../ads-land-shell` ran in the same block; `git worktree list` afterwards shows only `C:/Users/jakub/.omp/wt/t7455d23f4/m 8f5b7f1 (detached HEAD)` and the directory is gone. Both log files exist (102473 bytes and 1467 bytes, mtime 12:08).

## Task 3 — topic isolation, icon identity, secret-free content

```
A_count=35
B_defaultD=1
  3:Date:   Wed Oct 21 12:04:48 2026 +0200
C_headerlessD=0
D_iconAbsent=0
E_crosstopic=0
F_archive_entries=0
G_credential_lines=0
```

Literal vs headerless deletion count — the exact failing output of the plan's literal predicate is the commit header line:

```
$ git show --name-status HEAD | grep -c '^D'
1
$ git show --name-status HEAD | grep -n '^D'
3:Date:   Wed Oct 21 12:04:48 2026 +0200
$ git show --name-status --pretty=format: HEAD | grep -c '^D'
0
```

`B_defaultD=1` is the `Date:` header matching `^D`, not a deletion entry. `git show --name-status --pretty=format: HEAD` lists 35 entries, all `A`/`M`, and zero `D`. The substantive predicate — the commit contains no deletion — holds; see Deviations.

Restored icon, proven byte-identical and diff-free:

```
$ git hash-object src/AzureDesignStudio/wwwroot/assets/icons/others.svg
44a4c0ac02873e66df2f400e4dc4da3a68569ff5
$ git rev-parse HEAD:src/AzureDesignStudio/wwwroot/assets/icons/others.svg
44a4c0ac02873e66df2f400e4dc4da3a68569ff5
$ git ls-files -s src/AzureDesignStudio/wwwroot/assets/icons/others.svg
100644 44a4c0ac02873e66df2f400e4dc4da3a68569ff5 0	src/AzureDesignStudio/wwwroot/assets/icons/others.svg
$ git diff --quiet HEAD -- src/AzureDesignStudio/wwwroot/assets/icons/others.svg; echo $?
0
$ git show HEAD -- src/AzureDesignStudio/wwwroot/assets/icons/others.svg   # header only, no diff hunks
```

The 243-byte working file vs 242-byte blob difference is `core.autocrlf=true` checkout conversion; `git hash-object` normalises to the identical blob, so the file is unchanged content.

Icon absence from the commit:

```
$ git show --name-only --pretty=format: HEAD | grep -c 'assets/icons/others.svg'
0
```

Cross-topic exclusion (`E_crosstopic=0`): no `src/AzureDesignStudio.Core/` path, no `LinkTypeTests.cs`/`SubnetPlacementTests.cs`, no `docs/`, `README.md`, `.planning/`, `.gitignore`, and no path matching `harness`. The only `Core.Tests` path is `StencilTests.cs`, which belongs here per D-03.

Credential re-scan over the full committed diff (`git show HEAD`), the same pattern as the pre-commit gate: **0 matching lines**, including the three likely carriers `Program.cs`, `MauiProgram.cs`, `wwwroot/index.html`. Added binary archives (`.exe|.zip|.7z|.tar|.gz|.msix|.pfx|.p12`): **0**.

Topic justification (one sentence): all 35 paths are the shell slice — WASM components/pages/models/services/`Program.cs`/assets/tokens/catalog, the two Desktop host paths that consume them, and the single test file whose project links `wwwroot/ads-stencils.json` — so the commit reads as one reviewable shell topic and cannot be split without turning one commit red.

## Dirty paths remaining after the commit

`git status --porcelain` after the commit (9 lines):

```
 M README.md
 M docs/top-100-ui-improvements.md
?? .planning/.omp-checkpoint.json
?? .planning/.omp-next-action.json
?? .planning/.omp-task-results.json
?? .planning/milestone.lock
?? docs/assessment-2026-10-21.md
?? docs/next-phases-plan.md
?? docs/v3-plan.md
```

| Path | Classification |
|------|----------------|
| `README.md` | docs topic (plan 01-05) |
| `docs/top-100-ui-improvements.md` | docs topic (plan 01-05) |
| `docs/assessment-2026-10-21.md` | docs topic — new tracked-but-untracked doc (plan 01-05) |
| `docs/next-phases-plan.md` | docs topic (plan 01-05) |
| `docs/v3-plan.md` | docs topic (plan 01-05) |
| `.planning/.omp-checkpoint.json` | planning artefact (harness state) |
| `.planning/.omp-next-action.json` | planning artefact (harness state) |
| `.planning/.omp-task-results.json` | planning artefact (harness state) |
| `.planning/milestone.lock` | planning artefact (harness lock) |

No shell, core, Desktop, or test path remains dirty — the slice was neither under- nor over-staged. Nothing was pushed, amended, or added as a fourth commit.

## Tasks

1. **Task 1 — Restore the fallback icon and stage the shell topic** (`8f5b7f1`): icon restored from HEAD and proven diff-free, 35 explicit paths staged, all six pre-commit gates green, commit created with the exact subject and three-line body.
2. **Task 2 — Prove the shell commit from a clean worktree** (no commit): solution build 0 errors with the Desktop line present, offline suite `Passed: 755 / Failed: 0`, worktree removed with `--force`.
3. **Task 3 — Topic isolation, restored icon, secret-free audit** (no commit): 35-path set equality, zero deletion entries, icon absent from the commit, no cross-topic/harness path, credential scan and archive scan both zero.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] The literal zero-deletion predicate counts the `Date:` header**
- **Found during:** Task 3 (topic audit)
- **Issue:** `git show --name-status HEAD | grep -c '^D'` returns `1`, not `0`, because default `git show` format prints `Date:   Wed Oct 21 12:04:48 2026 +0200`, which matches `^D`. The plan's Task 3 `<verify>` block therefore exits 1 (`VERIFY03_RC=1`) even though the commit has no deletion.
- **Fix:** Used the headerless form already used in `01-01-SUMMARY.md`: `git show --name-status --pretty=format: HEAD | grep -c '^D'` → `0`, cross-checked against the full 35-entry list (12 `A`, 23 `M`, 0 `D`). No file or commit change was needed.
- **Files modified:** none
- **Verification:** `B_defaultD=1` with the matched line shown, `C_headerlessD=0`, and the complete headerless name-status list quoted above.

**2. [Rule 3 - Blocking] The dispatch note's baseline SHA and the missing 01-02 summary**
- **Found during:** Task 1 baseline
- **Issue:** The dispatch note names `078a9d7` as the workspace head, but the observed head is `dfeac63` (the 01-01 metadata commit whose parent is `078a9d7`). The note also asks for `01-02-SUMMARY.md`, which does not exist in this checkout.
- **Fix:** Pinned the worktree to the real tip `dfeac63` (the commit under test is the shell commit, whose parent is therefore `dfeac63`), and verified plan 01-02's `ads.js` repair directly in source (`zone.__adsPointerInit`, `window.__adsPointerDocInit`, event-time `document.querySelector(selector)`) instead of citing a summary. No content change.
- **Files modified:** none
- **Verification:** `git log --oneline -6` and the `ads.js` symbol check, both quoted above.

---

**Total deviations:** 2 (2 blocking check/measurement adjustments, 0 code fixes)
**Impact on plan:** None on the delivered commit. The 35-path set, its ordering of topics, the restored icon, the subject, and the 755 result are exactly as planned.

## Issues Encountered

- The isolated workspace checkout is on a **detached HEAD**, so `8f5b7f1` is not referenced by a branch here. This is the same condition plan 01-01 recorded: the orchestrator creates the authoritative commit in the parent checkout and compares tree SHAs, and this plan does not push.
- `git add` warned `LF will be replaced by CRLF` for the ten new text files (`core.autocrlf=true`), the same expected noise 01-01 recorded; the committed blobs carry LF.
- The cold-restore build took 3m42s wall clock; the suite then ran in 1 s. No second build ran concurrently in this workspace.
- No `.csproj`, `Directory.Packages.props`, or lock file was staged, so the dependency set is unchanged (`T-01-SC-03` remains `accept`), and `Package.appxmanifest` was untouched (D-21).

## Threat Flags

None — the commit adds UI components, static assets, and a JS module only. It adds no network endpoint, auth path, schema change, or secret; the credential scan over the full committed diff returned zero lines.

## Next Phase Readiness

- The shell commit `8f5b7f1e97dcc2b500c16148625ab6625d4ac896` is the third of the phase's three land commits' prerequisites: core `078a9d7` (proven at 753), shell `8f5b7f1` (proven at 755), with docs still outstanding.
- `StencilTests.cs` and `ads-stencils.json` are now inseparable in history, so no commit in the phase is red on the catalog assertion.
- The pointer fix exists in history for the first time inside this commit; plan 01-04's harness/real-app evidence and plan 01-06's push can reference this SHA.
- Remaining dirty paths are exactly the docs topic (5) and harness planning artefacts (4); plan 01-05 owns the former, and nothing else from the shell slice is left behind.

## Self-Check: PASSED

- The shell commit exists and is HEAD: `8f5b7f1e97dcc2b500c16148625ab6625d4ac896`, tree `d0efef502421d8c468f6a585e8d46c7298fcb14c`, parent `dfeac63436b0b4003d2011c9e47ce849ea39e8b6`.
- Subject is exactly `feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag`; path count is 35 and the full list is quoted.
- Zero deletion entries (`--pretty=format:` name-status → 0; the literal default-format command's `1` is the `Date:` header, shown with its matched line in Deviations #1).
- `assets/icons/others.svg` matches its HEAD blob (`44a4c0a…`), is diff-free, and is absent from the commit.
- Worktree proof observed: Desktop build line, `0 Error(s)`, `Passed: 755`, `Failed: 0`, worktree removed; both log files exist.
- Credential scan over the committed diff: 0 lines; no harness path, no archive, no core/docs/planning path.
- Dirty paths after the commit classified: 5 docs-topic paths and 4 planning artefacts.
