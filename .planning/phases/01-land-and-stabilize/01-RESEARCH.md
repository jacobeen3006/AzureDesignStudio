---
phase: 01
slug: land-and-stabilize
researched: 2026-10-21
domain: git history landing, tracked-artifact hygiene, vanilla-JS DOM lifetime, docs truth, publisher decision
confidence: HIGH
---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Commit boundary**
- **D-01:** The land is exactly three commits — core / shell / docs — with the tracked-artifact removals (`bd2.dll`, `bd_served.dll`, `.omo/`, `.swarm/`) riding inside the **core** commit. Keeps SC#3's "three reviewable commits" literal. — **Reversibility:** costly — once pushed, re-slicing means rewriting public history on `origin/main`.
- **D-02:** Each commit is verified green *before* push via a throwaway worktree: `git worktree add` at the SHA, `dotnet build src/AzureDesignStudio.sln` (Desktop included, Windows-only so only this machine can prove it), `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` expecting 755 passed, then `git worktree remove`.
- **D-03:** Tests travel with their subject. Core commit = `src/AzureDesignStudio.Core/**` + the new Core tests (`LinkTypeTests.cs`, `SubnetPlacementTests.cs`) + hygiene. Shell commit = `src/AzureDesignStudio/**` (WASM), `src/AzureDesignStudio.Desktop/**`, `wwwroot/ads-stencils.json`, the seven new SVG assets, **and** `src/AzureDesignStudio.Core.Tests/StencilTests.cs`. Reason (verified): `AzureDesignStudio.Core.Tests.csproj:34` links `..\AzureDesignStudio\wwwroot\ads-stencils.json` as Content, and the new `EveryCategoryHasAtLeastOneStencil` asserts categories 0–9 while the HEAD catalog only has 0–5 — a path-glob split leaves commit 1 red. — **Reversibility:** costly — same reason as D-01.
- **D-04:** `src/AzureDesignStudio/wwwroot/assets/icons/others.svg` is **restored** (`git checkout HEAD -- …`) in the shell commit, not deleted. `TopologyView.razor:316` still returns `assets/icons/others.svg` as its fallback for unknown resource types, so the working-tree deletion was a dangling reference. Retiring the icon is deferred, not done here.

**Repository hygiene**
- **D-05:** Removals are **forward-only** (`git rm` at the tip). No history rewrite. — **Reversibility:** one-way — `.omo/` and `.swarm/` are already in public `origin/main`, and purging the two root blobs from the 126 local commits later requires a force-push over public history that invalidates every existing clone. The tip satisfies SC#1; history does not need to.
- **D-06:** `.swarm/plan.md` (227 lines, a ".NET 8 refresh" plan the repo has outgrown — it references a Blazor.Diagrams submodule that no longer exists), `.swarm/spec.md` (95 lines, superseded by tracked `docs/azure-refresh-plan.md`), and `.omo/run-continuation/ses_*.json` (July 2026 background-task session record) are **deleted outright** — no archiving. Nothing is lost that isn't already in `docs/`.
- **D-07:** `.gitignore` gains four rules: anchored `/*.dll`, `.omo/`, `.swarm/`, and `.planning/.omp-ui-state.json` (currently untracked and dirty). `.planning/` otherwise stays tracked (SC#1).
- **D-08:** SC#1 is proven by a **recorded command audit** whose output goes in the phase summary — not a checked-in script (Phase 2 owns CI): `git ls-files | grep -E '\.dll$|^\.(omo|swarm)/'` empty, `git ls-files .planning | wc -l` non-zero, `git status --porcelain` empty, and every `docs/*.md` path cited by README/AGENTS resolving (verified today: zero missing citations).

**Remote landing**
- **D-09:** Direct push to `origin/main` — no PR, no integration branch. Matches SC#2 verbatim ("`origin/main` points at the local head"), keeps all 126 commit SHAs auditable, and there is no branch-protection evidence in-repo (no CODEOWNERS, no PR template, no required-checks config).
- **D-10:** Two pushes: the 126 pre-existing commits first, then the 3 land commits after the worktree proof. A failure is then isolated to the thing that failed.
- **D-11:** Out-of-band bundle at the **project parent** (outside the tree, never trackable): `git bundle create ../AzureDesignStudio-<sha7>-<date>.bundle --all`, proven by `git clone`-ing it into a temp dir and comparing `rev-parse HEAD` to the local head. Deleted when the phase closes — recovery insurance, not an artifact. `origin/dev` and `origin/Hackathon22` are left alone; tag `v0.3.8` is already pushed and does not move.
- **D-12:** Push failure handling: after each push, `git ls-remote origin refs/heads/main` must equal `git rev-parse HEAD`; on any rejection (auth, protection, non-fast-forward) **stop and report**. Never `--force` or `--force-with-lease`; no auto-rebase over 126 commits.

**Pointer-zone fix**
- **D-13:** Fix shape is element-scoped guard + event-time re-resolution, confined to `window.AdsStencilDrag` in `src/AzureDesignStudio/wwwroot/js/ads.js`: guard becomes `zone.__adsPointerInit`, document listeners attach once behind a separate `window.__adsPointerDocInit`, and `inZone`/`clear`/`finish`/the move handlers re-resolve via `document.querySelector('.ads-diagram-wrapper')` at event time and read `__adsRef` off the **live** node. Handles any number of remounts; keeps existing drag semantics. If the zone is absent at event time, nothing is invoked (no stale `DotNetObjectReference`, therefore no console error).
- **D-14:** Evidence for SC#4 is two proofs: (1) real app — `dotnet run --project src/AzureDesignStudio`, sign in, remount the rail (collapse/expand), pointer-drag a stencil onto the canvas, expect the node plus **zero** DevTools console errors; the same steps on the pre-fix commit must show the failure. (2) a throwaway harness HTML page in `wwwroot` temp that loads `ads.js`, calls `init`, replaces the zone node, calls `init` again, dispatches synthetic pointerdown/move/up and asserts `__adsRef.invokeMethodAsync` fired — deterministic, no auth.
- **D-15:** The harness is **not committed** — deleted after the smoke, steps and output recorded in the phase summary. TEST-04 (Phase 8) owns the committed Playwright E2E covering stencil drop and zero console exceptions; the repo has no JS test toolchain today and this phase will not create one.
- **D-16:** Scope is `AdsStencilDrag` only. Untouched: `AdsCommandService`/registry owner lifetime (SHELL-02, Phase 7), `AdsDropZone`'s already element-scoped `zone.__adsDrop` guard, `AdsTheme`, `AdsShortcuts`. No `.catch` wrapper on `invokeMethodAsync` — D-13's re-resolution removes the disposed-ref path, so swallowing errors would only mask real target bugs.

**Docs truth**
- **D-17:** Full docs reconcile, not just the two names in SC#5. Correct to current truth: `README.md:48` ("8 projects" → 11) and its solution tree (add `Discovery`, `Workspace`, `Mcp`); `AGENTS.md:9` ("AAD B2C" → Entra ID work+personal, "8 projects in src/" → 11); `.planning/codebase/CONCERNS.md:9` ("8 projects"); `docs/architecture.md`; and the historical plan docs that state the count or auth model as current fact (`docs/v1-plan.md`, `docs/azure-refresh-plan.md`). Verified today: 11 `.csproj` under `src/`; `AGENTS.md:69` already states the correct auth model.
- **D-18:** The publisher decision is recorded in `.planning/PROJECT.md` Key Decisions (required by SC#5) and mirrored in the MSIX section of `docs/entra-setup.md` (which already documents the `dotnet publish` packaging command).

**Publisher identity and signing route**
- **D-19:** Route is **Azure Artifact Signing** (formerly Trusted Signing) — no private-key custody, no PFX in CI secrets, and already the path ROADMAP Phase 11 names (`azure/trusted-signing-action@v0`). It carries an external lead time (Microsoft identity validation), which is why the decision is recorded here at phase entry. — **Reversibility:** costly — switching to an OV PFX later replaces CI secret handling and introduces certificate custody plus expiry tracking.
- **D-20:** Validation is an **individual** (sole developer) validation. Certificate subject, and therefore the manifest `Publisher` and the PROJECT.md record, is `CN=Jakub Talaj` (the repo's git identity for 128 of 245 commits). `PublisherDisplayName` may remain the friendly "Azure Design Studio".
- **D-21:** The land does **not** edit `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest`. Its placeholder `Publisher="CN=Azure Design Studio"` (line 9) is knowingly left until Phase 11, which owns "one publisher authority" and the CI assertion that `Publisher` equals the certificate subject verbatim.

### Claude's Discretion
- Commit subject wording and whether the land commits use a scope (`land(core): …`) — repo history uses bare conventional types (`docs:`, `chore:`, `feat:`), GSD's own commits use scoped forms like `docs(01):`.
- The exact per-commit path inventory, provided D-03's boundary and D-02's green proof hold.
- Filename and location of the throwaway harness page, and the temp-clone path used to verify the bundle.
- Order of operations inside the core commit (hygiene removal vs code staging).
- Whether the post-push push order is scripted or run by hand.

### Deferred Ideas (OUT OF SCOPE)
- **Retire `assets/icons/others.svg` and re-point `TopologyView.razor`'s `_ =>` fallback** — deferred out of Phase 1 (D-04) to keep the land behavior-neutral; belongs with the shell/icon work.
- **Remove the divergent `src/Configurations/ads-stencils.json` copy** (CONCERNS: "Two divergent `ads-stencils.json` copies", already drifted) — not in Phase 1's scope; belongs with the catalog/ConfigMap linkage work.
- **Prune or document the stale `origin/dev` and `origin/Hackathon22` branches** — left untouched; no phase currently owns remote branch hygiene.
- **`AdsCommandService` / registry owner lifetime (SHELL-02)** — explicitly out of scope; Phase 7.
- **Committed JS-level regression test for the pointer zone** — deferred to TEST-04's Playwright E2E in Phase 8 (D-15).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| — | No v1 requirement is delivered. See `.planning/REQUIREMENTS.md` traceability: "Phase 1 (Land and Stabilize) is an enabling gate with no mapped v1 requirement; it protects the baseline every mapped phase depends on." `[VERIFIED: .planning/REQUIREMENTS.md traceability footer]` | This phase is the precondition for BUILD-01 (Phase 2: clean-clone restore/build) and for the PROJECT.md Active item "Current uncommitted shell/domain/docs slice is committed in reviewable chunks and pushed". Every finding below exists to make the *next* phase's baseline reproducible — no capability is added. |

## Summary

Phase 1 is a **git and truth-repair phase**, not a feature phase. Every input to it is already on disk: 128 unpushed commits, 56 dirty paths, six tracked artifacts that assert something false (two 64 KB WebCIL blobs referenced by nothing, three agent-scratch files, one deleted SVG still returned by a desktop fallback), a pointer-drag module that is *new and uncommitted* and guards its initialisation on `window`, and four documents that still describe an 8-project B2C system. The research below replaces every count in CONTEXT.md with a number re-measured against the working tree at `0d0ffdc`, and identifies three places where the locked plan's *stated evidence method* does not survive contact with the tree.

The four findings that change planning shape:

1. **The unpushed count is 128, not 126** (and the author split is 130/247, not 128/245). The delta is exactly the two GSD commits at the tip — `a4b90c5 docs(01): capture phase context` and `0d0ffdc docs(state): record phase 01 context session` — which did not exist when CONTEXT.md's numbers were measured. D-05/D-09/D-10/D-12 cite "126" as a fixed quantity — the plan must use `git rev-list --count origin/main..HEAD` at execution time, never the literal.
2. **The pointer-drag defect is not pre-existing history — it is the uncommitted change itself.** `ads.js` is **+209/−4** against HEAD; HEAD has no `AdsStencilDrag` at all, and HEAD's `Stencil.razor:6` has `draggable="true"` while the working tree has `draggable="false"`. The pointer path is therefore the *only* drag path in the landed shell commit. There is no "pre-fix commit" to compare against (D-14 assumes one); the before/after evidence must be captured against the unfixed *working-tree file* before the fix is applied.
3. **The real-app half of SC#4 cannot run as written.** Both hosts ship `"ClientId": "<client id>"` placeholders (`src/AzureDesignStudio/wwwroot/appsettings.json`, `src/AzureDesignStudio.Desktop/appsettings.json`), and the route is wrapped in `AuthorizeRouteView` (`src/AzureDesignStudio/App.razor:4`), so `dotnet run --project src/AzureDesignStudio` cannot reach the canvas without a real Entra registration. The deterministic harness (D-14 part 2) is the only executable proof available on this machine; the browser check must be classified as a human-verify checkpoint with an explicit precondition, not an automated step.
4. **D-02's "expecting 755 passed" cannot hold for all three commits.** `StencilTests.cs` adds exactly two `[Fact]`s (`EveryCategoryHasAtLeastOneStencil`, `EveryImplementedResourceTypeHasAStencil`) and D-03 ships that file in the shell commit, so the core commit's expected count is **753** and shell/docs are **755** `[VERIFIED: 755 working-tree cases include both]`.

**Primary recommendation:** sequence the phase as *evidence-first, land-second*: capture the two "before" artefacts (dirty-tree inventory + harness failure against the unfixed `ads.js`), then fix and verify, then build the three commits, prove each in a worktree, push 128 + 3, then bundle and audit. Every count used in commit messages or the phase summary must be regenerated with a command at the moment it is written, never copied from CONTEXT.md.

## Verified Repository State

All measurements taken this session in `C:\Users\jakub\Desktop\Projects\AzureDesignStudio` at HEAD `0d0ffdc25fd29063dc49eabdf992b34c032ec506` on branch `main`.

### Git state

| Fact | Value | Evidence | Confidence |
|------|-------|----------|------------|
| Branch | `main` | `git rev-parse --abbrev-ref HEAD` | HIGH |
| HEAD | `0d0ffdc` (`docs(state): record phase 01 context session`) | `git rev-parse HEAD`, `git log --oneline -3` | HIGH |
| `origin/main` | `e0e3080a92c7102ec5c6e4aac1ae175a6d640c75` | `git ls-remote origin refs/heads/main` | HIGH |
| Ahead / behind | **128 ahead, 0 behind** | `git rev-list --left-right --count origin/main...HEAD` → `0 128` | HIGH |
| Merge base | equals `origin/main` ⇒ **fast-forward push, no divergence** | `git merge-base origin/main HEAD` | HIGH |
| Remote reachability | read path works without prompting (credential helper active) | `git ls-remote` returned a SHA | HIGH |
| Push permission | **unverified** — read access does not prove write access | not exercised (D-12 forbids experimentation) | LOW |
| Tags | only `v0.3.8` | `git tag` | HIGH |
| Stashes | 0 | `git stash list \| wc -l` | HIGH |
| Remote branches | `origin/main`, `origin/dev`, `origin/Hackathon22` | `git branch -r` | HIGH |
| Commit authorship | Jakub Talaj 130, Chun Liu 115, others 2 — **247 total** | `git log --format='%an' \| sort \| uniq -c` | HIGH |
| `core.autocrlf` | `true` (commits normalize to LF; checkout is CRLF) | `git config --get core.autocrlf` | HIGH |

Conflicts with CONTEXT.md (all are **staleness, not error**): "126 unpushed commits" → **128** `[VERIFIED: git rev-list]`; "128 of 245 commits" → **130 of 247** `[VERIFIED: git log]`. The two-commit delta is `a4b90c5` (the context commit itself) and `0d0ffdc` (the state commit after it); neither was pushed when the context numbers were measured. Plan text must not hard-code either number — re-measure at write time.

### Working tree (56 paths)

| Category | Count | Evidence |
|----------|-------|----------|
| Modified (`M`) | 35 | `git status --porcelain` |
| Deleted (`D`) | 1 (`src/AzureDesignStudio/wwwroot/assets/icons/others.svg`) | same |
| Untracked (`??`) | 20 | same |
| **Total** | **56** | `git status --porcelain \| wc -l` → `56` |

*Measurement note:* this count predates this file. After `01-RESEARCH.md` was written, `git status --porcelain` reports **57** — the extra path is this untracked research document, which GSD commits separately. Any later audit must expect 57 until it is committed, and must not treat the delta as a leak.

Bucketed by the D-03 commit boundary `[VERIFIED: git status, path prefixes]`:

| Bucket | Dirty paths | Notes |
|--------|-------------|-------|
| `src/AzureDesignStudio.Core/**` | 10 | 2 `.razor.css`, `DTO/AzureNodeProfile.cs`, `DataModelFactory.cs`, 6 Network files |
| `src/AzureDesignStudio.Core.Tests/**` | 3 | `StencilTests.cs` → **shell** (D-03); `LinkTypeTests.cs`, `SubnetPlacementTests.cs` → **core** |
| `src/AzureDesignStudio/**` (WASM) | 32 | 20 modified + 12 untracked: `wwwroot/ads-stencils.json`, 5 icon SVGs + `subnet.svg`, `ads-tokens.css`, 4 new components, `AdsCommandService.cs` |
| `src/AzureDesignStudio.Core/**` untracked | 2 of the 10 above | `Common/LinkTypeClassifier.cs`, `Network/SubnetForm.razor` — both belong to Core, i.e. the **core** commit |
| `src/AzureDesignStudio.Desktop/**` | 2 | `MauiProgram.cs`, `wwwroot/app.html` |
| `README.md` + `docs/**` | 5 | `README.md`, `docs/top-100-ui-improvements.md` (M); `docs/assessment-2026-10-21.md`, `docs/next-phases-plan.md`, `docs/v3-plan.md` (new) |
| Deleted (D) | 1 | `src/AzureDesignStudio/wwwroot/assets/icons/others.svg` — restored, not committed (D-04) |
| `.planning/.omp-ui-state.json` | 1 | ignored by D-07, **not committed** |

`src/AzureDesignStudio/wwwroot/assets/icons/others.svg` is the single `D` path and is tracked in HEAD `[VERIFIED: git cat-file -e HEAD:…]` — D-04's restore is a one-command no-op on the diff.

`git status` also confirms there are **no** tracked `bin/`/`obj/` artefacts and no `.exe`/`.zip`/archive files tracked `[VERIFIED: git ls-files \| grep -Ei '\.(exe|zip|7z|tar|gz)$'` → empty].

### Tracked artefacts that must go (SC#1)

| Path | Size / lines | Nature | Reference check |
|------|--------------|--------|-----------------|
| `bd2.dll` | 64,277 bytes; sha256 `fd7b9ff5ab1e…1f65f` | WebAssembly (WebCIL) binary v1 | referenced only by `.planning/codebase/CONCERNS.md:49-50`, `.planning/codebase/STRUCTURE.md:294`, `docs/assessment-2026-10-21.md:50`, `docs/next-phases-plan.md:24` — **no csproj/sln/code reference** `[VERIFIED: grep across csproj/sln/props/cs]` |
| `bd_served.dll` | 64,277 bytes; **identical sha256** | same | same |
| `.omo/run-continuation/ses_0dbd65604ffe8SHcgoBM9S6Jw2.json` | 9 lines, 223 bytes | background-task session record, `"updatedAt": "2026-07-02T18:50:06.248Z"` | nothing reads it `[VERIFIED: cat]` |
| `.swarm/plan.md` | 227 lines, 10,979 bytes | ".NET 8 refresh" plan | superseded `[VERIFIED: CONTEXT/D-06, wc -l]` |
| `.swarm/spec.md` | 95 lines, 7,064 bytes | superseded by `docs/azure-refresh-plan.md` | `wc -l` |

`git ls-files` matches exactly five paths under `^\.(omo|swarm)/` plus the two root `.dll` files — the removal set is closed, no glob surprises `[VERIFIED: git ls-files \| grep -E '^\.(omo\|swarm)/']`.

### `.gitignore` (D-07)

Current file is the stock VisualStudio template (≈330 lines) plus six project-specific lines (`/src/…/appsettings.Development.json`, `…Production.json`, `graphify-out/`, `.planning/graphs/graph.json`, `graph.html`, `.last-build-snapshot.json`). **None of the four D-07 rules exist** `[VERIFIED: grep for '^/\*\.dll', '^\.omo/', '^\.swarm/', 'omp-ui-state' → no matches]`, and `git check-ignore -v bd2.dll .omo/plan .swarm/plan.md .planning/.omp-ui-state.json` prints nothing (exit: not ignored) `[VERIFIED]`.

`.planning/` currently tracks **20** files (PROJECT, REQUIREMENTS, ROADMAP, STATE, 7 codebase maps, config.json, 1 graph report, 2 phase-01 files, 5 research files) `[VERIFIED: git ls-files .planning \| wc -l]` — SC#1's "`.planning/` still tracked" has a concrete baseline of 20 and will rise to 21 when this RESEARCH.md is committed.

### Test and build baseline

| Fact | Value | Evidence | Confidence |
|------|-------|----------|------------|
| Projects under `src/` | **11** `.csproj` | `git ls-files 'src/**/*.csproj' \| wc -l` | HIGH |
| Solution entries | 13 = 11 projects + 2 solution folders (`Solution Items`, `Tests`) | `grep -c 'Project(' src/AzureDesignStudio.sln` | HIGH |
| Offline test cases (`Category!=LiveAzure`) | **755** in the **working tree** (includes the two new `StencilTests` facts) | `dotnet test … --list-tests` piped to a count of `AzureDesignStudio.Core.Tests.` lines | HIGH |
| Suite total (incl. LiveAzure) | **764** (9 live cases excluded) | same, unfiltered | HIGH |
| Test framework | xUnit 2.9.2, `Microsoft.NET.Test.Sdk` 17.12.0, single project, no `xunit.runner.json`/`.runsettings` | `Core.Tests.csproj:16-21`, `.planning/codebase/TESTING.md` | HIGH |
| SDK on this machine | **10.0.401**, workloads `maui 10.0.110`, `wasm-tools 10.0.112` installed | `dotnet --version`, `dotnet workload list` | HIGH |
| `global.json` | **absent** (Phase 2 owns pinning) | `ls global.json` → not found | HIGH |
| Suite *green* status | **not re-run this session** — count verified via `--list-tests`; green is asserted by CONTEXT/STATE/PROJECT ("755 passed") | `[CITED: .planning/PROJECT.md Context]` | MEDIUM |

Counting method is stated explicitly because a `--list-tests` count proves *what will run*, not that it *passes*. The planner's worktree gate (D-02) is the first place a real pass/fail is produced; it is also the phase's only defence against a commit that compiles but reddens the suite.

### Pointer-drag defect: exact current code

`src/AzureDesignStudio/wwwroot/js/ads.js` is 293 lines (`wc -l`), **+209/−4 against HEAD** `[VERIFIED: git diff --numstat]`. HEAD contains **no** `AdsStencilDrag` and no call to it `[VERIFIED: git show HEAD:… \| grep]`. `Stencil.razor:6` changed from `draggable="true"` (HEAD) to `draggable="false"` (working tree) `[VERIFIED: git show HEAD vs working tree]`, which disables the `dragstart`-based `AdsDropZone` seam for stencil tiles — only the pointer path can move a stencil in the landed state.

`window.AdsDropZone` (lines 56–116) is the correct precedent: it adopts `zone.__adsRef = dotNetRef` **before** its guard (line 65) and stores the guard on the element (`zone.__adsDrop`, lines 66–69), attaching element listeners so a recreated element rebinds.

`window.AdsStencilDrag` (lines 124–230) — every place `zone` is captured `[VERIFIED: ads.js:124-230]`:

| Lines | Symbol | Capture/use of `zone` |
|-------|--------|-----------------------|
| 126 | `const zone = document.querySelector(selector)` | the single closure capture — the root cause |
| 127 | `if (!zone \|\| window.__adsStencilPointerInit) return` | **window-scoped** guard |
| 130 | `window.__adsStencilPointerInit = true` | set on first mount only |
| 132 | `let active = null` | shared drag state in the same closure |
| 134–137 | `inZone(x,y)` → `zone.getBoundingClientRect()` | measures the **captured** node on every move/up |
| 139–147 | `clear(drag)` → `zone.classList.remove(…)` ×2 | toggles classes on the captured node |
| 148–167 | `pointerdown` (capture) | reads tile via `e.target.closest('[data-stencil-key]')`; no zone use |
| 171–208 | `pointermove` (capture, passive:false) | `zone.classList.add('ads-drop-active')` (185), `zone.classList.toggle('ads-drop-hover', inZone(…))` (199) |
| 202–214 | `finish(e, cancelled)` | `inZone(…) && zone.__adsRef` (211) → `zone.__adsRef.invokeMethodAsync('HandleDropAtAsync', …)` (212) |
| 216–217 | `pointerup` / `pointercancel` listeners | call `finish` |
| 218–223 | `window` `blur` listener | clears `active` + ghost |

`DotNetObjectReference` lifetime: `DiagramPanel.razor.cs:42-50` creates `dropRef` in `OnAfterRenderAsync(firstRender)` (line 47) and calls `AdsDropZone.init(dropRef, ".ads-diagram-wrapper")` (line 48) then `AdsStencilDrag.init(".ads-diagram-wrapper")` (line 49). `Dispose()` (line 53) calls `dropRef?.Dispose()` (line 61). Because `AdsDropZone.init` adopts the newest ref on the element it is given, a recreated wrapper gets a valid ref; because `AdsStencilDrag` captured the *first* node, it never sees it.

**Minimal diff shape (D-13), confined to lines 125–223:**

```js
init: function (selector) {
    const zone = document.querySelector(selector);
    if (!zone) {
        return;                     // nothing to bind yet; a later mount retries
    }
    if (zone.__adsPointerInit) {
        return;                     // already bound to THIS element
    }
    zone.__adsPointerInit = true;   // element-scoped guard (replaces window.__adsStencilPointerInit)
    if (window.__adsPointerDocInit) {
        return;                     // document listeners already attached once
    }
    window.__adsPointerDocInit = true;

    const liveZone = function () { return document.querySelector(selector); };

    let active = null;

    const inZone = function (x, y) {
        const z = liveZone();
        if (!z) { return false; }    // absent at event time → no drop, no throw
        const r = z.getBoundingClientRect();
        return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
    };

    const clear = function (drag) {
        document.body.classList.remove('ads-dragging');
        const z = liveZone();
        if (z) {
            z.classList.remove('ads-drop-active');
            z.classList.remove('ads-drop-hover');
        }
        if (drag && drag.ghost) { drag.ghost.remove(); }
    };
    // pointermove: const z = liveZone(); if (z) { z.classList.add(...); z.classList.toggle('ads-drop-hover', inZone(...)); }
    // finish:      const z = liveZone(); if (drag.dragging && !cancelled && inZone(...) && z && z.__adsRef) {
    //                  z.__adsRef.invokeMethodAsync('HandleDropAtAsync', e.clientX, e.clientY, drag.key); }
}
```

Ordering is load-bearing: the element guard must be set **before** the doc-guard early return, otherwise a second element never gets marked and a third mount re-enters. The `selector` closed over by the document listeners is a call-site constant (`".ads-diagram-wrapper"`), so re-resolving it is safe; there is exactly one caller `[VERIFIED: grep DiagramPanel.razor.cs:49]`.

### Remount path: what actually recreates the wrapper

| Fact | Finding | Confidence |
|------|---------|------------|
| `DiagramPanel` mount points | **one** — `src/AzureDesignStudio/Pages/Index.razor:34`, inside `<Content>` | HIGH `[VERIFIED: grep]` |
| Rail collapse/expand | toggles `collapsed` (`Index.razor:70`, `toggleSider()` at line 116); the `@if (collapsed)` block is **inside the `Sider` only** (lines 15–26). `DiagramPanel` is in the sibling `<Content>`, so the toggle re-renders Index but does not change the `DiagramPanel` component's position in the render tree | HIGH for the code layout; **MEDIUM** that Blazor preserves the instance (standard diff-by-sequence-type behaviour, not executed here) |
| Documented page routes | `@page "/"` and `@page "/authentication/{action}"` — **both on `Index.razor`** (lines 1–2), so authenticating does not change page component type | HIGH `[VERIFIED]` |
| `@key` usage in shell | none that affects the wrapper | HIGH `[VERIFIED: grep '@key' — only `KeyVaultForm.razor`]` |
| Full reload paths | service-worker update notification calls a JS reload; a reload resets `window.__adsStencilPointerInit`, so it does **not** reproduce the defect | MEDIUM |

Consequence: **no in-app action I can find reliably recreates `.ads-diagram-wrapper` while the JS context survives.** The defect is latent-but-real (the guard is provably wrong and any future remount — route change, `@key`, hot reload, host-driven circuit restart — will hit it), but D-14's stated trigger ("collapse/expand") is not mechanically supported by the render tree. This is an open question (Q1) with a cheap empirical test in Validation Architecture, and it is the reason the harness must be the authoritative SC#4 proof.

### Docs drift inventory

| File:line | Current text | Truth | In scope |
|-----------|--------------|-------|----------|
| `README.md:48` | `## 🏗️ Solution Structure (8 projects)` | 11 `[VERIFIED]` | **required by SC#5** |
| `README.md:51-59` | tree lists 8 projects; missing `Discovery`, `Workspace`, `Mcp` | 11 exist | **required by SC#5** |
| `AGENTS.md:9` | "hosted as WASM (AAD B2C) … .NET 10, 8 projects in src/" | Entra ID work+personal; 11 | **required by SC#5** |
| `AGENTS.md:69` | "Entra ID work+personal (not B2C)" | already correct — wording source for line 9 | reference only |
| `.planning/codebase/CONCERNS.md:9` | "`src/` — 8 projects, ~64k lines…" | 11 | D-17 |
| `docs/architecture.md:28` | "**Target**: .NET 7.0, Blazor WebAssembly" | .NET 10 | D-17 |
| `docs/architecture.md:36-37,54` | "Blazor WebAssembly + .NET 7", "Ant Design Blazor 0.14.4" | .NET 10, AntDesign 1.6.2 `[VERIFIED: AzureDesignStudio.csproj]` | D-17 |
| `docs/architecture.md:39,87,101,221` | "Azure AD B2C" / "MSAL → Azure AD B2C" / "AzureAdB2C" | Entra ID work+personal, `common` authority | D-17 |
| `docs/architecture.md:41,230` | "EF Core (InMemory dev / SQL Server prod)" | SQLite workspace + SQL Server server | D-17 (check before rewriting) |
| `docs/v1-plan.md:103` | "…README: 8 projects…" inside a **"Done 2026-10-13"** entry | historically true at that date | **out of scope** — a changelog record, not a current-fact claim (see Q2) |
| `docs/v1-plan.md:12,20,42,95` | auth decisions already say Entra / "drop B2C" | already correct | no edit |
| `docs/azure-refresh-plan.md:14` | "All 8 projects must retarget" (dated 2026-06-27, a .NET 8-era plan) | 11 projects | D-17 — but it is a superseded dated plan; a staleness banner (Q2) beats rewriting numbers inside a 2026 plan |
| `docs/azure-refresh-plan.md:115,117,189` | B2C config specifics | B2C retired | same caveat |

Citation audit for SC#1: every `docs/*.md` path referenced from `README.md` (lines 276–285) and `AGENTS.md` **exists on disk** `[VERIFIED: loop over grep-extracted paths → zero MISSING]`; **three are untracked** (`docs/assessment-2026-10-21.md`, `docs/next-phases-plan.md`, `docs/v3-plan.md`) `[VERIFIED: git ls-files --error-unmatch]` and therefore must ride in the docs commit or SC#1 fails on a fresh clone.

### Publisher / docs-of-record shapes

| Artefact | Shape | Evidence |
|----------|-------|----------|
| `.planning/PROJECT.md` Key Decisions | heading `## Key Decisions` (line 76); table header `\| Decision \| Rationale \| Outcome \|` (line 78); 4 rows, Outcome column uses `— Pending` | `[VERIFIED: .planning/PROJECT.md:76-83]` |
| `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest:9` | `<Identity Name="AzureDesignStudio.Desktop" Publisher="CN=Azure Design Studio" Version="1.0.0.0" />` | `[VERIFIED]` |
| same file:15 | `<PublisherDisplayName>Azure Design Studio</PublisherDisplayName>` | `[VERIFIED]` |
| `docs/entra-setup.md` §4 "Build & package the desktop app" | heading line 63; MSIX command lines 72–75; publisher paragraph lines 77–85 currently says the manifest uses `CN=Azure Design Studio` and that `publish.yml` signs via `MSIX_PFX_BASE64`/`MSIX_PFX_PASSWORD` | `[VERIFIED: numbered read]` |
| `.github/workflows/publish.yml:5-6,59,87-95` | today's signing path is `signtool` + PFX secrets; **no** Artifact Signing step | `[VERIFIED]` |

D-18's mirror therefore lands on a section that currently documents the *old* route. It must state the decision (Artifact Signing, individual validation, subject `CN=Jakub Talaj`) and label the manifest value as a placeholder until Phase 11 (D-21) — otherwise the doc would claim the manifest already carries the decided publisher.

## Implementation Approach

### SC#1 — Fresh clone contains exactly the tracked source

1. `git rm bd2.dll bd_served.dll` and `git rm -r .omo .swarm` at the tip. Files disappear from the working tree; no `--cached`, no history rewrite (D-05/D-06).
2. Append the four D-07 rules to `.gitignore` in the core commit (hygiene rides there per D-01). Anchored `/*.dll`, not `*.dll`, so nothing under `src/**` can be silently ignored later.
3. Restore `others.svg` (`git checkout HEAD -- src/AzureDesignStudio/wwwroot/assets/icons/others.svg`) before staging the shell commit (D-04). Reference chain that makes the deletion a dangling pointer: `TopologyView.razor:316` `_ => "assets/icons/others.svg"` `[VERIFIED]`.
4. Add the three untracked docs in the docs commit (citation audit above).
5. Audit (D-08), output pasted into the phase summary:
   - `git ls-files | grep -E '\.dll$|^\.(omo|swarm)/'` → empty
   - `git ls-files .planning | wc -l` → 21 (20 today + this RESEARCH.md)
   - `git status --porcelain` → empty
   - cited-docs loop → zero missing **and** zero untracked

**Pitfall specific to this step:** `.gitignore` is the one file changed in a commit that also deletes tracked files. If the ignore lines are appended *before* `git rm`, `git rm` still works (tracked files are not affected by ignore rules), but `git status` will stop showing the scratch as untracked afterwards — harmless, but do the `git rm` first so the removal is visible in `git status` while reviewing.

### SC#2 — Clean status, `origin/main` at local head, bundle restores it

1. Two pushes (D-10): first the **128** pre-existing commits (`git push origin main` when HEAD is still the pre-land tip), then the 3 land commits. Re-measure the number immediately before pushing; CONTEXT's 126 is stale.
2. After each push: `git ls-remote origin refs/heads/main` must equal `git rev-parse HEAD` (D-12). Any rejection → stop and report; never `--force`, never `--force-with-lease`, never auto-rebase.
3. Bundle (D-11): `git bundle create ../AzureDesignStudio-<sha7>-<date>.bundle --all`. The parent directory `C:\Users\jakub\Desktop\Projects` is a normal writable directory next to five sibling repos `[VERIFIED: ls ..]`. Verify by `git clone <bundle> <tempdir>` then `git -C <tempdir> rev-parse HEAD` == local `rev-parse HEAD`; delete on phase close.
4. `git status --porcelain` empty — depends on D-07's `.planning/.omp-ui-state.json` rule (today it is the sole non-committable dirty path).

**Ordering hazard:** the bundle must be created **after** the final push, or the "restores that same head" claim is about a different SHA. If the bundle is taken first as insurance, re-take it after the push and verify the second one.

### SC#3 — Three reviewable commits, each restores, builds, stays green

Path inventory derived from the live tree (all counts verified):

| Commit | Paths | Count |
|--------|-------|-------|
| **core** | 10 modified `src/AzureDesignStudio.Core/**` + 2 new Core files (`Common/LinkTypeClassifier.cs`, `Network/SubnetForm.razor`) + `LinkTypeTests.cs` + `SubnetPlacementTests.cs` + `.gitignore` edit + 5 tracked removals (`bd2.dll`, `bd_served.dll`, `.omo/run-continuation/ses_….json`, `.swarm/plan.md`, `.swarm/spec.md`) | 14 dirty + 1 new edit + 5 removals = **20 paths** |
| **shell** | 20 modified WASM paths (incl. `wwwroot/ads-stencils.json`, `ads.js`, `index.html`) + 12 new WASM paths (5 icon SVGs, `subnet.svg`, `ads-tokens.css`, 4 components, `AdsCommandService.cs`) + 2 Desktop + `StencilTests.cs` (`others.svg` restored, so it is not a change) | **35** |
| **docs** | `README.md`, `docs/top-100-ui-improvements.md`, 3 new docs, `.planning/PROJECT.md` (D-18), `docs/entra-setup.md` (D-18), `.planning/codebase/CONCERNS.md`, `docs/architecture.md` | **9** |

Total committed paths = **64** = 56 dirty − 1 ignored (`.planning/.omp-ui-state.json`) − 1 restored (`others.svg`) + 5 removals + 5 new edits (`.gitignore`, `PROJECT.md`, `entra-setup.md`, `CONCERNS.md`, `architecture.md`).

`LinkTypeClassifier.cs` lives under `src/AzureDesignStudio.Core/Common/`, so it belongs to **core** even though it is a shell-facing feature — it is a Core type with Core tests (`LinkTypeTests.cs`), so tests travel with their subject (D-03). `SubnetForm.razor` (`Core/Network/`) is Core by the same rule.

Per-commit proof (D-02), for **each** of the three SHAs:

```bash
git worktree add ../ads-land-core <sha>
dotnet build src/AzureDesignStudio.sln            # Desktop included; maui workload present
dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"
git worktree remove --force ../ads-land-core
```

**Expected pass count differs per commit — D-02's "755 for all three" is arithmetically impossible.** `StencilTests.cs` gains exactly **two** `[Fact]`s in the working tree (`EveryCategoryHasAtLeastOneStencil`, `EveryImplementedResourceTypeHasAStencil` — `git diff` shows +30/−1) `[VERIFIED: git diff StencilTests.cs]`, and D-03 ships that file in the **shell** commit to keep the core commit's linked `ads-stencils.json` (HEAD version, categories 0–5) consistent with `<= 5`. Therefore:

| Commit | Stencil tests | Expected `Passed:` |
|--------|---------------|--------------------|
| core | HEAD version (4 facts) | **753** |
| shell | working-tree version (+2 facts) | **755** |
| docs | same as shell | **755** |

Record the observed count per commit in the phase summary; a core run that reports 755 means `StencilTests.cs` was staged into the wrong commit, and a core run that reports <753 means a test did not travel with its subject `[INFERENCE: 755 minus the two added facts]`. Do not "fix" the 753 by moving the test — that reintroduces D-03's red commit.

**Worktree pitfalls (concrete, will be hit):**
- `dotnet build` writes `bin/`/`obj/` inside the worktree; `git worktree remove` refuses a worktree containing untracked files. Use `git worktree remove --force` or `git clean -xdff` first — `--force` is safe here because the worktree is throwaway and nothing in it is authored.
- Each worktree needs a **cold NuGet restore** (no shared `obj/`). Budget minutes per commit, not seconds, and run them sequentially — three concurrent restores on one machine contend on the NuGet cache lock.
- The restore requires network; it is available (`git ls-remote` succeeded) but it is a *different* dependency than the push credential.
- The solution build is the only thing that proves Desktop compiles; CI (`.github/workflows/build.yml`) deliberately builds only Server/WASM/MCP/Tests on Linux `[VERIFIED: build.yml:24-32]`, so a Desktop regression cannot be caught by CI and **must** be caught here.

Verification that the three commits were sliced correctly (beyond green): `git show --stat <sha>` should read as one topic, and `git show <sha> --name-only | grep` should show no cross-topic path.

### SC#4 — Pointer drop reaches the canvas after remount, no console error

Sequence that produces real before/after evidence despite the defect being new code:

1. **Before (unfixed working tree):** create the throwaway harness (D-14 part 2, D-15 not committed), run it against the current `ads.js`, record the failure (expected: zero `invokeMethodAsync` calls on the replacement node's ref). This *is* the "pre-fix" proof — there is no commit that contains the defect except the working tree `[VERIFIED: HEAD has no AdsStencilDrag]`.
2. Apply the D-13 diff (shape above), re-run the harness → exactly one invocation, on the **new** node's `__adsRef`, with the expected `(x, y, key)`.
3. Real-app smoke (D-14 part 1) — **blocked by configuration**, see Q3. Minimum reachable version: run the app, confirm the canvas accepts a pointer drop and DevTools shows no error *if* a working Entra registration is available; otherwise record the harness as the proof and mark the browser step a human-verify checkpoint.
4. Delete the harness and stage the fixed `ads.js` into the shell commit.

Harness shape (disposable, no toolchain — plain HTML + inline script):

```html
<div class="ads-diagram-wrapper" id="old">
  <div class="ads-stencil" data-stencil-key="virtual-network">VNet</div>
</div>
<script src="js/ads.js"></script>
<script>
  const calls = [];
  const old = document.querySelector('.ads-diagram-wrapper');
  old.__adsRef = { invokeMethodAsync: (...a) => calls.push(['old', a]) };
  AdsStencilDrag.init('.ads-diagram-wrapper');

  const replacement = document.createElement('div');
  replacement.className = 'ads-diagram-wrapper';
  replacement.innerHTML = '<div class="ads-stencil" data-stencil-key="virtual-network">VNet</div>';
  replacement.__adsRef = { invokeMethodAsync: (...a) => calls.push(['new', a]) };
  old.replaceWith(replacement);
  AdsStencilDrag.init('.ads-diagram-wrapper');

  const tile = replacement.querySelector('[data-stencil-key]');
  const r = replacement.getBoundingClientRect();          // synthetic events use viewport coords
  const fire = (type, x, y) => tile.dispatchEvent(new PointerEvent(type,
      { bubbles: true, cancelable: true, pointerId: 1, button: 0, buttons: 1, clientX: x, clientY: y }));
  fire('pointerdown', r.left + 10, r.top + 10);
  fire('pointermove',  r.left + 40, r.top + 40);
  fire('pointerup',    r.left + 60, r.top + 60);
  document.title = JSON.stringify(calls);                  // assert: [["new", …]], never [["old", …]]
</script>
```

Notes that make this deterministic: the wrapper must have non-zero size (give it `height:200px`), `pointermove` needs ≥4 px of travel to arm `dragging` (`ads.js:181-183`), the ghost is appended to `document.body` so it needs no styles, and the `pointerup` coordinate must be inside the **live** rect. Additionally assert `document.querySelector('.ads-diagram-wrapper') === replacement` and that the old ref received nothing.

**What "no console error" means here:** the observed failure mode of the current code is *silent* (a detached node's `getBoundingClientRect()` is all zeros, so `inZone` returns false and the invoke at line 212 is never reached — `[INFERENCE from DOM semantics + code path]`). A disposed `DotNetObjectReference` exception would only appear if `__adsRef` were reached on a dead ref. So SC#4's two assertions have different weights: the harness proves *reach*, the app smoke proves *no error*. Do not let the absence of an error in the app smoke stand in for proof of reach.

Do **not** add a `.catch` to `invokeMethodAsync` (D-16) — it would suppress exactly the disposal error Phase 8's TEST-04 must be able to see.

### SC#5 — Docs truth + publisher record

1. `README.md:48` → `(11 projects)`; insert `AzureDesignStudio.Discovery`, `AzureDesignStudio.Workspace`, `AzureDesignStudio.Mcp` into the tree at lines 50–60 in solution order (`Discovery` before `Mcp`/`Workspace` alphabetically where the list is alphabetical; the current list is by role, so place them after `AzureResources`/`SourceGeneration` and keep the `└──`/`├──` box characters correct).
2. `AGENTS.md:9` → reuse the verified wording already at `AGENTS.md:69`: "Entra ID work+personal (not B2C)"; "11 projects in src/".
3. `.planning/codebase/CONCERNS.md:9` → "11 projects".
4. `docs/architecture.md` — at minimum lines 28, 36-37, 39, 54 (count/auth/stack). Lines 87, 101, 221 are ASCII-diagram/auth prose. Verify each remaining claim before editing (Q2): the file is the only *current* architecture doc cited from README:276, so its stale stack table is the highest-value fix after the count.
5. Historical plan docs: apply the "superseded" treatment rather than rewriting history (Q2).
6. Publisher (`PROJECT.md` Key Decisions, one row, exact table shape at line 78):
   `| Publisher legal name and signing route: \`CN=Jakub Talaj\` (individual validation) via Azure Artifact Signing | No private-key custody; matches the Phase 11 \`azure/trusted-signing-action@v0\` path; identity validation has external lead time | Decided 2026-10-21 — manifest update deferred to Phase 11 (D-21) |`
   Outcome wording is free; **the subject string must be byte-identical** to the future certificate subject (`CN=Jakub Talaj`), because Phase 11 asserts equality and a mismatch is the `0x8007000B` install failure.
7. `docs/entra-setup.md` §4 (lines 77–85): add the decision and a one-line note that the manifest still carries the placeholder `CN=Azure Design Studio` until Phase 11; the existing `MSIX_PFX_BASE64` sentence describes today's workflow and stays accurate as *current* behaviour.

## Risks and Pitfalls

1. **Stale counts in prose become lies in the land.** CONTEXT.md's 126/54/128 all shift to 128/56/130 `[VERIFIED]`. Every number written into a commit message, PROJECT.md or the phase summary must be regenerated at write time. Warning sign: a summary that cites "126 commits" while `git rev-list --count origin/main..HEAD` prints 128.
2. **Re-slicing after push is public history damage** (D-01). Mitigation: complete the worktree proof for all three SHAs **before** the second push; D-10's split push keeps the pre-existing 128 commits isolated from a slicing mistake.
3. **Worktree removal fails after a build** (ignored/untracked `bin`, `obj`) — use `--force`, and never point the worktree path inside the repo.
4. **Three concurrent worktree builds thrash the machine and the NuGet cache.** Build sequentially; this is CPU-bound, not wall-clock-parallel.
5. **`git rm -r .omo .swarm` deletes files the docs still reference** (`.planning/codebase/STRUCTURE.md:294`, assessment/next-phases-plan). Those references are descriptive, not build inputs, so removal is safe; do not "fix" them in this phase beyond what D-17 covers.
6. **`others.svg` restore is easy to lose.** It is the only `D` path; if the shell commit is built with `git add src/AzureDesignStudio` after the deletion, the deletion is staged. Do the `git checkout HEAD -- …` **before** staging, then re-check `git status` shows no `D`.
7. **`core.autocrlf=true`.** Newly added files are committed LF; an existing CRLF file edited on Windows can produce a whole-file diff if it was committed with CRLF. Check `git diff --stat` before staging; a docs edit that shows the whole file changed is a line-ending artefact, not an edit.
8. **`.gitignore` `/*.dll` anchoring.** `/*.dll` (root-anchored) is required; a bare `*.dll` would hide genuine build outputs from `git status` in test/publish folders that are not otherwise ignored.
9. **D-14's "pre-fix commit" does not exist.** Plan the before/after so the "before" run happens on the unfixed working tree, captured in the summary. If the fix is applied first, the evidence is unrecoverable without `git stash`/re-editing.
10. **SC#4's real-app step needs Entra credentials the repo does not contain.** Both `ClientId` values are `"<client id>"` `[VERIFIED]`. Treat the browser check as a checkpoint with a precondition; never block the phase on it when the harness has produced deterministic evidence.
11. **Collapse/expand may not recreate the wrapper element** (see "Remount path"). Do not design the phase so that its only proof depends on a trigger that may be a no-op; probe node identity first.
12. **Push permission is unverified** (read access proven, write assumed). A rejection means stop (D-12) — after 128 commits are staged this is a real scheduling risk, so the *first* push should be attempted early enough that a credential problem does not strand the phase at the end.
13. **Bundle must post-date the final push** or SC#2's "restores that same head" is vacuous.
14. **Docs commit touches `.planning/PROJECT.md` while GSD also writes STATE/ROADMAP.** Expect the phase's own tooling commits interleaved with the land; stage explicit paths (`git add <path>`), never `git add -A` in that commit.
15. **D-02's "755 passed" is wrong for the core commit.** `StencilTests.cs` carries the only two added cases and ships in the shell commit, so core proves **753** `[INFERENCE: verified 755 working-tree count minus the two verified new facts]`. Treating 753 as a failure would send the executor looking for a broken test that does not exist; treating 755 as the bar at core would tempt a re-slice that reddens commit 1.

## Sequencing

```
0. Evidence capture (no repo mutation)
   a. record dirty inventory + unpushed count + test count
   b. write harness (untracked, temp) → run against UNFIXED ads.js → record failure
1. Repair
   a. apply D-13 diff to ads.js → harness passes on replacement node
   b. restore others.svg
2. Core commit  (Core code + 2 new Core tests + .gitignore + 5 removals)
3. Shell commit (WASM + Desktop + StencilTests + restored svg)
4. Docs commit  (README, AGENTS-adjacent docs, PROJECT Key Decisions, entra-setup)
5. Per-commit proof: worktree build + 755-test run for each of the 3 SHAs (sequential)
6. Push #1: the 128 pre-existing commits   → verify ls-remote == HEAD
7. Push #2: the 3 land commits             → verify ls-remote == HEAD
8. Bundle at ../<sha7>-<date>.bundle, clone-verify, delete at phase close
9. SC#1 audit + SC#5 grep audit → phase summary
10. Browser smoke (if credentials available) → else checkpoint
```

Ordering constraints that are **hard**, not stylistic:
- Harness "before" run must precede the `ads.js` edit (step 0b before 1a).
- All three commits must exist before step 5 (each is proved at its own SHA).
- Push #1 must precede Push #2 (D-10 isolation).
- Bundle after Push #2.
- The worktree proof must precede Push #2 (D-02 "before push").

Soft/parallelisable: SC#5 docs edits can be prepared while worktree builds run, since the docs commit does not affect the build.

## Open Questions (RESOLVED)

**Q1 — Does any in-app action actually recreate `.ads-diagram-wrapper`?**
What we know: one mount point (`Index.razor:34`), no `@key`, both routes on `Index.razor`, collapse toggles a sibling `Sider` (HIGH). What is unclear: whether Blazor/AntDesign's `Content` re-render can replace the wrapper element in practice (MEDIUM). Recommendation: two-minute probe before planning the browser step — in DevTools console `window.__probe = document.querySelector('.ads-diagram-wrapper')`, collapse and expand the rail, then `document.querySelector('.ads-diagram-wrapper') === window.__probe`. `true` → the documented trigger is wrong and the harness is the only reachable proof; `false` → D-14 part 1 works as written. Either outcome is acceptable; only the *claim* changes.

**Q2 — How far does "docs truth" reach into historical plan documents?**
`docs/v1-plan.md:103` is a "Done 2026-10-13" changelog entry that reported 8 projects *at that date* (HIGH); `docs/azure-refresh-plan.md` is a dated 2026-06-27 .NET 8-era plan (HIGH). Rewriting their numbers would falsify a record. Recommendation: correct only statements asserted as *current* truth (README, AGENTS, CONCERNS, architecture.md), and give the two historical docs a dated superseded banner if D-17's "reconcile" is read broadly. Planner should pick one reading and state it; this is not worth a user round-trip unless the banner conflicts with D-17's intent.

**Q3 — Can SC#4's real-app proof be executed on this machine?**
`ClientId` is a placeholder in both hosts (HIGH). `AuthorizeRouteView` wraps the router (`App.razor:4`); whether an unauthenticated visitor sees the canvas or `RedirectToLogin` is framework behaviour I did not execute (MEDIUM — `AuthorizeRouteView` normally requires authentication unless `[AllowAnonymous]`). Recommendation: one 2-minute load of `dotnet run --project src/AzureDesignStudio` decides it. If the canvas is unreachable, either a developer-supplied local `wwwroot/appsettings.Development.json` (untracked, deleted before commit) or a harness-only SC#4 is required — the planner should choose the harness-only path and record the browser smoke as a human-verify checkpoint.

**Q4 — Is `origin/main` protected?**
No in-repo evidence of protection (no CODEOWNERS, no PR template, no required checks) `[VERIFIED: absence in repo]`; protection lives in GitHub settings and was not probed. D-09 assumes direct push. If Push #1 is rejected, D-12's stop-and-report applies — no fallback branch strategy is defined.

**Q5 — Does the harness's synthetic `PointerEvent` reproduce WebView2/Chrome behaviour faithfully enough?**
Synthetic events exercise the listener logic and node resolution (which is the defect), but not real hit-testing or pointer capture (MEDIUM). Acceptable for this phase because TEST-04 owns the real-browser regression; state the limitation in the phase summary.

### Resolution dispositions

- **Q1 → plan 01-07 Task 3.** The probe is a required task step; the summary must record `Remount probe:` as `true`, `false`, or `blocked`, and the automated check refuses `SC#4 primary proof: in-app` unless the probe returned `false`. The claim, not the fix, is what varies.
- **Q2 → plan 01-04 Task 2** (banner-only edits to `docs/v1-plan.md` and `docs/azure-refresh-plan.md`) **and plan 01-04 `<objective>`**, which states the chosen reading: correct statements asserted as current truth, mark dated records as historical. Plan 01-05 Task 3 proves both banners and that no other line moved.
- **Q3 → plan 01-07 Task 3.** The blocked case is first-class: `Real-app drop: blocked` plus the placeholder-`ClientId` reason, with the plan 01-02 harness transcript named as the primary proof. The harness-only path is chosen; the browser smoke is a human check, never an automated one.
- **Q4 → plan 01-06 Task 1 and plan 01-08 Task 1.** No fallback branch strategy is introduced: both pushes run `git merge-base --is-ancestor` first and stop on any rejection, reporting the remote message (D-12). Nothing rebases, and no force/lease refspec exists anywhere in the plan set.
- **Q5 → plan 01-02 Task 2 and plan 01-07 Task 3.** The synthetic-event limitation is stated in both summaries; the harness asserts method name, stencil key and node identity rather than pixel values, and Phase 8's TEST-04 keeps the real-browser regression.

## Validation Architecture

`.planning/config.json` sets `workflow.nyquist_validation: true` `[VERIFIED]` — validation is required. Upstream, `workflow.human_verify_mode` is `end-of-phase`, so every manual step below is collected at the phase gate rather than per-wave.

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit 2.9.2 (`Microsoft.NET.Test.Sdk` 17.12.0, `xunit.runner.visualstudio` 2.8.2) |
| Config file | none — single csproj, no `xunit.runner.json`, no `.runsettings` |
| Quick run command | `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` |
| Full suite command | same command plus `--filter "Category=LiveAzure"` (requires `az login`; **not** part of this phase's gate) |
| Expected case count | **755** offline / 764 total `[VERIFIED: --list-tests]` |
| JS toolchain | **none** — no node, no Jest/Vitest, no Playwright until Phase 8 (D-15) |

No new test framework may be introduced here (D-15); the JS proof is a throwaway page, and the .NET proof is the existing suite.

### Success criteria → proof map

| SC | What must be TRUE | Proof | Type | Command / artefact | Exists? |
|----|-------------------|-------|------|--------------------|---------|
| SC#1 | Fresh clone = tracked source only | command audit (D-08) | manual/scripted | `git ls-files \| grep -E '\.dll$\|^\.(omo\|swarm)/'`; `git ls-files .planning \| wc -l` ≥ 21; `git status --porcelain`; cited-doc loop | ✅ audit only — no test file (D-08 forbids one in Phase 1) |
| SC#1 | Every cited doc file committed | citation loop over README/AGENTS `docs/*.md` + `git ls-files --error-unmatch` | manual | same loop used this session (zero missing, 3 untracked today) | ✅ |
| SC#2 | `origin/main` == local head | `git ls-remote origin refs/heads/main` == `git rev-parse HEAD` | manual | run twice (after each push) | ✅ |
| SC#2 | Bundle restores the same head | `git clone <bundle> <tmp>` → `rev-parse HEAD` compare | manual | clone + compare, then delete tmp | ✅ |
| SC#3 | Each commit restores + builds | `git worktree add … && dotnet build src/AzureDesignStudio.sln` | build gate | per SHA, ×3, sequential | ✅ (worktree is the harness) |
| SC#3 | Each commit keeps the offline suite green | `dotnet test … --filter "Category!=LiveAzure"` expecting `Passed: 753` (core) / `755` (shell, docs) | suite | per SHA, ×3 | ✅ |
| SC#3 | Three commits, one topic each | `git show --stat` + `--name-only` review | manual | read three diffs | ✅ |
| SC#4 | Drop reaches canvas after remount | throwaway harness: old node replaced, new ref invoked exactly once | JS manual harness | harness page (not committed), output pasted in summary | ⚠️ Wave-0 artefact: harness must be written (deleted after) |
| SC#4 | No console error | browser smoke with DevTools console visible | manual | `dotnet run --project src/AzureDesignStudio` + drop | ⚠️ blocked on auth (Q3) → checkpoint |
| SC#5 | README/AGENTS say 11 + Entra | `grep -c "8 project" README.md AGENTS.md` → 0; `grep -n "11 project"` → present; `grep -rn "B2C" AGENTS.md` → 0 | manual | grep audit | ✅ |
| SC#5 | Publisher recorded | `grep -n "CN=Jakub Talaj" .planning/PROJECT.md docs/entra-setup.md` → 2 hits; `grep -n "Artifact Signing" …` → 2 hits | manual | grep audit | ✅ |
| SC#1–SC#5 | Nothing else regressed | offline suite green at the final head (not only per-commit) | suite | `dotnet test … --filter "Category!=LiveAzure"` once more after push | ✅ |

### Sampling rate

- **Per commit (step 5):** worktree build + full offline suite — the suite is the only automated gate this phase has, and ~755 cases complete in seconds once built (`--list-tests` ran in ~8 s; a full run is dominated by build time). Expect **753** at core and **755** at shell/docs (see SC#3).
- **Per push (steps 6–7):** `ls-remote` equality check — cheap, mandatory (D-12).
- **Phase gate:** SC#1 audit + SC#5 grep audit + the one post-push suite run + the harness transcript in the summary.

### Wave 0 gaps

- [ ] **Throwaway JS harness** (path at planner's discretion, suggested `src/AzureDesignStudio/wwwroot/__land-harness.html`, deleted before commit) — the only automated SC#4 evidence.
- [ ] **"Before" transcript** captured against the unfixed `ads.js` (must be produced *before* the fix; cannot be regenerated later).
- [ ] **Worktree build helper** — three sequential `worktree add → build → test → remove --force` cycles; may be run by hand (Claude's discretion), but the exact commands must be in the plan so the summary can quote real output.
- [ ] **No new .NET test files** — this phase adds no test for the JS defect (D-15). The two new Core test files (`LinkTypeTests.cs`, `SubnetPlacementTests.cs`) already exist in the working tree and travel with the core commit; they are part of the offline suite at **both** the core (753) and shell (755) commits.

## Sources

### Primary (HIGH confidence — this session, tool-verified)
- `git rev-parse/rev-list/ls-remote/status/ls-files/log/show/diff/check-ignore/worktree list/tag/stash` against `0d0ffdc` on `main`
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure" --list-tests` (755 lines) and unfiltered (764)
- `dotnet --version` → 10.0.401; `dotnet workload list` → maui, wasm-tools
- `src/AzureDesignStudio/wwwroot/js/ads.js:56-116, 124-230` (full read)
- `src/AzureDesignStudio/Components/DiagramPanel.razor.cs:42-62`; `src/AzureDesignStudio/Components/DiagramPanel.razor:12`
- `src/AzureDesignStudio/Pages/Index.razor:1-120`; `src/AzureDesignStudio/App.razor:1-30`; `src/AzureDesignStudio/Shared/MainLayout.razor`
- `src/AzureDesignStudio/Components/Stencil.razor:6` (HEAD `draggable="true"` vs working tree `draggable="false"`)
- `src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:28-37`; `StencilTests.cs:38-57`
- `src/AzureDesignStudio.Desktop/Components/TopologyView.razor:305-318`
- `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest:9,14-16`
- `.planning/PROJECT.md:76-83`; `.planning/config.json`; `.planning/REQUIREMENTS.md` traceability
- `README.md:48-62,276-285`; `AGENTS.md:9,69`; `.planning/codebase/CONCERNS.md:9`; `docs/architecture.md:28,36-42,54,87,101,221,230`; `docs/v1-plan.md:1-14,100-104`; `docs/azure-refresh-plan.md:1-16,115-117,189`; `docs/entra-setup.md:63-86`
- `.github/workflows/build.yml:1-38`; `.github/workflows/publish.yml:5-6,55-95`
- `sha256sum bd2.dll bd_served.dll` → identical digests

### Secondary (MEDIUM confidence)
- `.planning/PROJECT.md` Context block — "755 passed" suite-green claim (count independently verified; pass status not re-run this session)
- Blazor render-tree preservation across a sibling `Sider` parameter change (framework behaviour, not executed)
- `AuthorizeRouteView` requiring authentication without `[AllowAnonymous]` (framework behaviour, not executed) → Q3

### Tertiary (LOW / inference)
- Whether `origin/main` has branch protection (GitHub-side; not probed)
- Push write permission (only read proven)
- Detached-node `getBoundingClientRect()` → all zeros → silent no-drop (DOM semantics inference supporting the failure mode)
