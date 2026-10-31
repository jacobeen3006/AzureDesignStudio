---
phase: 01-land-and-stabilize
verified: 2026-10-31T00:00:00Z
status: passed
score: 5/5 must-haves verified
covered_files:
  - .gitignore
  - .planning/PROJECT.md
  - .planning/codebase/CONCERNS.md
  - .planning/phases/01-land-and-stabilize/01-01-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-01-SUMMARY.md
  - .planning/phases/01-land-and-stabilize/01-02-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-02-SUMMARY.md
  - .planning/phases/01-land-and-stabilize/01-03-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-03-SUMMARY.md
  - .planning/phases/01-land-and-stabilize/01-04-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-04-SUMMARY.md
  - .planning/phases/01-land-and-stabilize/01-05-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-05-SUMMARY.md
  - .planning/phases/01-land-and-stabilize/01-06-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-06-SUMMARY.md
  - .planning/phases/01-land-and-stabilize/01-07-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-07-SUMMARY.md
  - .planning/phases/01-land-and-stabilize/01-08-PLAN.md
  - .planning/phases/01-land-and-stabilize/01-08-SUMMARY.md
  - AGENTS.md
  - README.md
  - docs/architecture.md
  - docs/assessment-2026-10-21.md
  - docs/azure-refresh-plan.md
  - docs/entra-setup.md
  - docs/next-phases-plan.md
  - docs/top-100-ui-improvements.md
  - docs/v1-plan.md
  - docs/v3-plan.md
  - src/AzureDesignStudio.Core.Tests/LinkTypeTests.cs
  - src/AzureDesignStudio.Core.Tests/StencilTests.cs
  - src/AzureDesignStudio.Core.Tests/SubnetPlacementTests.cs
  - src/AzureDesignStudio.Core/Common/LinkTypeClassifier.cs
  - src/AzureDesignStudio.Core/Components/AzureGroupComponent.razor.css
  - src/AzureDesignStudio.Core/Components/AzureNodeComponent.razor.css
  - src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs
  - src/AzureDesignStudio.Core/DataModelFactory.cs
  - src/AzureDesignStudio.Core/Network/AzureFirewallForm.razor
  - src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs
  - src/AzureDesignStudio.Core/Network/BastionsForm.razor
  - src/AzureDesignStudio.Core/Network/BastionsModel.cs
  - src/AzureDesignStudio.Core/Network/SubnetForm.razor
  - src/AzureDesignStudio.Core/Network/SubnetModel.cs
  - src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs
  - src/AzureDesignStudio.Desktop/MauiProgram.cs
  - src/AzureDesignStudio.Desktop/wwwroot/app.html
  - src/AzureDesignStudio/Components/CommandPalette.razor
  - src/AzureDesignStudio/Components/DesignInspector.razor
  - src/AzureDesignStudio/Components/DiagramNavigator.razor
  - src/AzureDesignStudio/Components/DiagramNavigator.razor.css
  - src/AzureDesignStudio/Components/DiagramPanel.razor
  - src/AzureDesignStudio/Components/DiagramPanel.razor.cs
  - src/AzureDesignStudio/Components/DiagramPanel.razor.css
  - src/AzureDesignStudio/Components/StatusBar.razor
  - src/AzureDesignStudio/Components/Stencil.razor
  - src/AzureDesignStudio/Components/Stencil.razor.css
  - src/AzureDesignStudio/Components/StencilPanel.razor
  - src/AzureDesignStudio/Components/StencilPanel.razor.css
  - src/AzureDesignStudio/Components/ThemeToggle.razor
  - src/AzureDesignStudio/Components/TopMenu.razor
  - src/AzureDesignStudio/Components/TopMenu.razor.cs
  - src/AzureDesignStudio/Components/TopMenu.razor.css
  - src/AzureDesignStudio/Models/StencilModel.cs
  - src/AzureDesignStudio/Pages/Index.razor
  - src/AzureDesignStudio/Pages/Index.razor.css
  - src/AzureDesignStudio/Program.cs
  - src/AzureDesignStudio/Services/AdsCommandService.cs
  - src/AzureDesignStudio/Shared/MainLayout.razor
  - src/AzureDesignStudio/wwwroot/ads-stencils.json
  - src/AzureDesignStudio/wwwroot/assets/azure/subnet.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/ai.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/containers.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/general.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/integration.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/security.svg
  - src/AzureDesignStudio/wwwroot/css/ads-tokens.css
  - src/AzureDesignStudio/wwwroot/index.html
  - src/AzureDesignStudio/wwwroot/js/ads.js
covered_digest: "v3:sha256:429a4de0b22f9b211daab13eaca59def432e60af4208c6c0843038e1536656cc"
behavior_unverified: 0
overrides_applied: 0
re_verification:
  previous_status: passed
  previous_score: 5/5
  gaps_closed: []
  gaps_remaining: []
  regressions: []
---

# Phase 01: Land and Stabilize Verification Report

**Phase Goal:** The milestone's starting point stops living on one machine: the working slice lands in reviewable commits, tracked artifacts stop lying, the stale pointer zone is fixed, and the publisher identity is decided before any packaging work begins.
**Verified:** 2026-10-31T00:00:00Z
**Status:** passed
**Re-verification:** Yes — fingerprint refresh after `stale` (previous status: passed, 5/5), scoped to the landed commits

## Re-verification Scope and Staleness

**Scope (user decision):** the verdict is for the three LANDED commits `078a9d7` / `ea8ac77` / `3315b94` (read via `git show`/`git ls-tree`/ancestry, not the working tree). Uncommitted working-tree WIP and the Phase 2 edit to `AGENTS.md` are out of scope.

**Why the prior report went `stale`:** (1) `AGENTS.md` was legitimately edited by Phase 2 commit `acc2027` (2026-10-28: regenerated stamp, CI/build-authority/pitfall lines; +9/−6); (2) seven covered source files carry uncommitted user WIP (`VirtualNetworkModel.cs`, `SubnetForm.razor`, `DiagramPanel.razor.cs`, `Stencil.razor.css`, `ads-tokens.css`, `ads.js`, plus `SubnetPlacementTests.cs`). The `covered_digest` below therefore fingerprints the *current working tree*; the verdict is for the landed commits. The WIP is not Phase 1 work.

**Regression check — none found.**
- `acc2027`'s AGENTS.md diff does not touch the SC#5 claims: HEAD `AGENTS.md:9` still says "Entra ID work+personal accounts … 11 projects in src/", `:69` still "Entra ID work+personal"; `README.md:38,48,76,223` unchanged; `CONCERNS.md:9,15` and `docs/architecture.md` still carry "11 project(s)"; zero `B2C`/`(7|8) projects` in README/AGENTS at HEAD.
- `git ls-tree 3315b94`: 0 `*.dll`/`.omo/`/`.swarm/` paths, 11 csproj. `git ls-files` at HEAD: same (no dll/.omo/.swarm; 11 csproj; 55 `.planning` files).
- `.gitignore` at `3315b94` lines 364–367 carry `/*.dll`, `.omo/`, `.swarm/`, `.planning/.omp-ui-state.json`.
- Ancestry: all three land commits and converged head `0aa2a54` are ancestors of `origin/main` (`578949c`, which also contains Phase 2 planning). HEAD `fa62bb9` is 5 Phase 2 commits ahead of origin (unpushed, Phase 2 scope; not a Phase 1 concern).
- Publisher decision: `CN=Jakub Talaj` appears once in `.planning/PROJECT.md` and once in `docs/entra-setup.md` at HEAD, Artifact Signing route once in entra-setup; `git diff e9078d1 3315b94 -- Package.appxmanifest` empty.
- SC#4: `ads.js` at `ea8ac77` still resolves the zone at event time via `liveZone()` (lines 146–243) and reads `__adsRef` off the live node; element guard (line ~134) precedes document guard.
- Re-run in a throwaway worktree at `3315b94` (removed afterwards): `dotnet test AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` → `Passed: 755, Failed: 0`, matching the prior 755. 753/755 at the earlier SHAs were not re-run; they remain the baseline recorded evidence (trees unchanged since land).
- Minor baseline correction: the core commit has 20 name-status entries (15 A/M + 5 D), not "16 paths"; the topic and hygiene content is unchanged and single-topic.

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SC#1 — A fresh clone contains only tracked source: no root `*.dll`, no `.omo/`/`.swarm/`, `.planning/` tracked, 11 csproj, every cited `docs/*.md` tracked | ✓ VERIFIED | Cloned the retained bundle to a temp dir: `CLONE_HEAD=0aa2a54…`; `git status --porcelain` empty; `git ls-files \| grep -E '\.dll$\|^\.(omo\|swarm)/'` no output; 39 `.planning` files; 11 csproj; 1023 tracked files; 8/8 README-cited docs present via `git ls-files --error-unmatch` |
| 2 | SC#2 — `origin/main` equals the converged head, the three land commits are ancestors, no non-`.planning` path is unpushed, the recovery bundle restores its recorded head | ✓ VERIFIED | `origin/main` = `0aa2a54e065af2b9fa4abb8770fd820164e58989` = `CONVERGE_SHA`; `merge-base --is-ancestor` 0 for all three; `rev-list --count origin/main..HEAD -- . ':!.planning/'` = 0; bundle verify "complete history" and clone HEAD = `0aa2a54…` |
| 3 | SC#3 — Three reviewable land commits (core/shell/docs), single-topic path sets, each builds and keeps the suite green; 753 at core + 2 stencil facts = 755 | ✓ VERIFIED | `078a9d7` (20 entries: 15 A/M + 5 D, 753), `ea8ac77` (35 paths, 755), `3315b94` (12 paths, 755). Independent worktrees at each SHA: build 0 errors each; `Passed: 753/755/755`. `StencilTests.cs` adds exactly two `[Fact]`s |
| 4 | SC#4 — Pointer drop reaches the live canvas after wrapper replacement, no console error; zone resolved at event time, element guard before document guard | ✓ VERIFIED | `ads.js:134` element guard, `:138` document guard; `liveZone()` at `:146-148` used by `inZone`/`clear`/move/`finish`; ref read only off `dropZone` at `:243-244`; no `.catch`/`try`. Harness BEFORE `calls:[]`; AFTER `[["b",["HandleDropAtAsync",68,86,"virtual-network"]]]`, zero on detached `old`/`a`; in-app drop grew canvas descendants 2→11 with no drop-phase console entry |
| 5 | SC#5 — README/AGENTS report 11 projects and Entra ID work+personal; CONCERNS/architecture carry the measured count; publisher `CN=Jakub Talaj` + Azure Artifact Signing recorded once in PROJECT.md and docs/entra-setup.md; manifest untouched | ✓ VERIFIED | `11 project` in README:48, AGENTS:9, CONCERNS:9, architecture:15; zero `(7\|8) projects`; zero `B2C`; Entra ID work+personal at README:38, AGENTS:9/69, architecture:43/225; CONCERNS carries the count only and asserts no auth model (see plan-truth note); publisher string and route each exactly once in both authorities; `git diff e9078d1 3315b94 -- Package.appxmanifest` exit 0 |

**Score:** 5/5 truths verified (0 present-but-behavior-unverified)

### Must-Haves

| # | Must-have | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Core commit removes the five tracked non-source artefacts forward-only | ✓ VERIFIED | `078a9d7` deletes `bd2.dll`, `bd_served.dll`, `.omo/run-continuation/…`, `.swarm/plan.md`, `.swarm/spec.md`; no parent rewrite |
| 2 | `.gitignore` gains `/*.dll`, `.omo/`, `.swarm/`, `.planning/.omp-ui-state.json`; `.planning/` stays tracked; `src/**/*.dll` visible | ✓ VERIFIED | Lines 364–367; `git check-ignore` matches all four; `src/AzureDesignStudio/foo.dll` not ignored; 40 local / 39 clone `.planning` files tracked |
| 3 | Core commit restores, builds full solution incl. Desktop, suite `Passed: 753` | ✓ VERIFIED | Independent worktree `078a9d7`: `Build succeeded … 0 Error(s)`; `Passed: 753, Failed: 0` |
| 4 | Shell commit is exactly the 35-path shell topic, `others.svg` restored, suite 755 | ✓ VERIFIED | `git show --name-status ea8ac77` = 35 paths, no deletions, `others.svg` tracked and still the `TopologyView.razor:316` fallback; worktree `Passed: 755, Failed: 0` |
| 5 | Docs commit is exactly 12 documentation paths, no source; suite 755 | ✓ VERIFIED | `git show --name-status 3315b94` = 12 paths (README, AGENTS, CONCERNS, PROJECT, 8 docs); worktree `Passed: 755, Failed: 0` |
| 6 | No push/recovery path uses force, lease, rebase, or history rewrite | ✓ VERIFIED | Push output is `e0e3080..e9078d1` and `3127ba3..0aa2a54` (fast-forward, no `+`/forced form); reflog shows plain `commit:` entries for all three land commits |
| 7 | Two dated plan records are banner-marked, not rewritten | ✓ VERIFIED | `git show --numstat 3315b94 -- docs/v1-plan.md docs/azure-refresh-plan.md` = `1 0` and `1 0` |
| 8 | Publisher decision recorded identically in PROJECT.md and docs/entra-setup.md; manifest untouched | ✓ VERIFIED | `grep -c` = 1 for each string in each file; manifest diff across the land = 0 and still carries the placeholder `CN=Azure Design Studio` |
| 9 | No credential material, harness, scratch, or build output in the land | ✓ VERIFIED | Value-shaped scan over all three diffs empty; shell has no explicit credential-string match; no `package.json`/JS test toolchain/harness tracked; no `TBD`/`FIXME`/`XXX` in any land-changed file |
| 10 | Recorded worktree proofs correspond to the pushed commit content | ✓ VERIFIED | Child workspace commits `a2b934b`/`8f5b7f1`/`ba4da2e` still exist under `.omp/wt`; `a2b934b^{tree} == 078a9d7^{tree}`; `8f5b7f1..ea8ac77` and `ba4da2e..3315b94` differ only by a `.planning` SUMMARY file added after the proof |

**Plan-truth note (01-04 truth #1).** The plan's wording puts `CONCERNS.md` in the auth-model clause. The committed CONCERNS edit changes only the count — exactly what plan 01-04 Task 1's action and acceptance require ("change only the project count in the scope line; that map is a point-in-time record"). The file makes no auth-model assertion, so it holds no stale B2C/AAD model claim; its only provider references are "Add Entra app roles" in a recommendation and "Azure AD ClientId" in defect prose. The ROADMAP SC#5 contract (README + AGENTS report the model; all four carry the measured count) is met. This is a broader-than-enforced plan sentence, not a contradiction or a lying artifact.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `078a9d7` core commit | 15 added/modified paths + 5 deletions (20 entries; baseline said "16 paths") | ✓ VERIFIED | 10 Core files, 2 Core.Tests, `.gitignore`, 5 deletions |
| `ea8ac77` shell commit | 35-path shell topic | ✓ VERIFIED | WASM components, catalog, 7 assets, Desktop host, pointer fix, `StencilTests` |
| `3315b94` docs commit | 12-path docs topic | ✓ VERIFIED | README/AGENTS/CONCERNS/architecture/PROJECT/entra-setup + 3 newly tracked docs + 2 banners |
| `src/AzureDesignStudio/wwwroot/js/ads.js` | Remount-safe pointer zone | ✓ VERIFIED | Element-scoped guard + event-time re-resolution; no swallowed errors |
| `.planning/PROJECT.md` | Publisher Key Decision | ✓ VERIFIED | `CN=Jakub Talaj` via Azure Artifact Signing, once |
| `docs/entra-setup.md` | Mirrored publisher decision | ✓ VERIFIED | Same subject and route, once |
| `../AzureDesignStudio-0aa2a54-20261021.bundle` | Recovery artefact at converged head | ✓ VERIFIED | 99,632,772 bytes; `bundle verify` complete; clone HEAD `0aa2a54…` |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `DiagramPanel.razor.cs:46-49` | `AdsDropZone.init` then `AdsStencilDrag.init('.ads-diagram-wrapper')` | `JS.InvokeVoidAsync` on `OnAfterRenderAsync` | ✓ WIRED | Both calls present in order |
| `AdsDropZone.init` | `zone.__adsRef` on the live node | assignment before the per-element guard | ✓ WIRED | `ads.js:63` writes the newest `DotNetObjectReference` |
| `AdsStencilDrag` move/finish | live `.ads-diagram-wrapper` and its `__adsRef` | `document.querySelector(selector)` at event time | ✓ WIRED | `:146-148`, reads at `:153,163,210,227,242-243` |
| New shell components | app shell | Razor usage | ✓ WIRED | `CommandPalette`/`StatusBar`/`ThemeToggle` in `MainLayout.razor`; `DesignInspector` in `Index.razor`; `AdsCommandService` registered in both hosts |
| README/AGENTS doc links | tracked docs | `git ls-files --error-unmatch` | ✓ WIRED | 8/8 present in the fresh clone |

### Data-Flow Trace (Level 4)

Not applicable to the land/repair deliverables. The only dynamic path in scope is SC#4, traced above: pointer events → event-time zone resolution → `dropZone.__adsRef.invokeMethodAsync('HandleDropAtAsync', …)`. The harness transcript records the real invocation on the live node and zero on detached nodes.

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Core commit is green at its own tree | `dotnet build src/AzureDesignStudio.sln`; `dotnet test … --filter "Category!=LiveAzure"` in a worktree at `078a9d7` | build 0 errors; `Passed: 753, Failed: 0` | ✓ PASS |
| Shell commit is green at its own tree | same at `ea8ac77` | build 0 errors; `Passed: 755, Failed: 0` | ✓ PASS |
| Docs commit is green at its own tree | same at `3315b94` | build 0 errors; `Passed: 755, Failed: 0` | ✓ PASS |
| Fresh clone has no lying artefacts | clone bundle; `git ls-files \| grep -E '\.dll$\|^\.(omo\|swarm)/'`; count `.planning`/csproj | no matches; 39; 11 | ✓ PASS |
| Remote convergence | `git rev-parse origin/main`; ancestry; unpushed non-planning count | `0aa2a54…`; 0/0/0; 0 | ✓ PASS |
| Bundle restores its head | `git bundle verify`; `git clone`; `git rev-parse HEAD` | complete history; `0aa2a54…` | ✓ PASS |

### Probe Execution

No `scripts/*/tests/probe-*.sh` exists in the repository. The phase's only declared probe is the plan-01-02 headless-browser harness, which D-15 deliberately kept outside the repository and deleted after use. Its BEFORE/AFTER transcript was inspected in `01-02-SUMMARY.md`: BEFORE `"calls":[]`; AFTER exactly one call on live node `b`, none on `old`/`a`, zero-zone cycle 0 calls / no error. The harness was not rerun (it no longer exists); the code path it exercises was verified by inspection, and the real-app drop was independently recorded in `01-07-SUMMARY.md`.

### Decision Coverage

All 21 trackable CONTEXT.md decisions are honored by shipped artifacts (`check.decision-coverage-verify`: `total: 21`, `honored: 21`, `not_honored: []`).

### Test Quality Audit

| Test File | Linked Req | Active | Skipped | Circular | Assertion Level | Verdict |
|-----------|-----------|--------|---------|----------|-----------------|---------|
| `LinkTypeTests.cs` | SC#3 | 6 facts/theories | 0 | No | Value/behavioral | ✓ |
| `SubnetPlacementTests.cs` | SC#3 | 5 facts/theories | 0 | No | Value/behavioral | ✓ |
| `StencilTests.cs` (2 added) | SC#3/SC#5 | 2 new facts | 0 | No | Value (category set; subset) | ✓ |

**Disabled tests on requirements:** 0. **Circular patterns detected:** 0. **Insufficient assertions:** 0.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| None (enabling gate) | — | ROADMAP declares no v1 requirement for Phase 1; BUILD-01 is Phase 2 | ✓ N/A | `REQUIREMENTS.md` traceability maps BUILD-01 to Phase 2; no orphaned Phase 1 IDs |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| — | — | No `TBD`/`FIXME`/`XXX`, stub returns, placeholder components, skipped tests, or circular fixtures in any land-changed file | — | None |
| `.planning/ROADMAP.md` | 80–238 | `**Plans**: TBD` for later, unplanned phases | ℹ️ Info | Structural placeholders for Phases 2–11, not debt in this phase's deliverables; not a land-changed file |

## Deviations Weighed

**(a) The literal credential regex matched `ClientSecret` identifier prose.** The plan-01-05 gate produced 5 matches, all `ClientSecret`/`ClientSecretCredential` identifiers in `docs/v3-plan.md`, `docs/assessment-2026-10-21.md`, `docs/next-phases-plan.md`; the value-shaped scan over the same diff is empty, and `01-05-SUMMARY.md` records `Self-Check: FAILED` for that literal gate. **Verdict: weakens the literal gate, satisfies the must-have.** The plan-01-03 truth is "no credential-shaped string", and no credential value is present. This is a false-positive regex, not secret material.

**(b) Plans quote stale planning-time counts (126/128 unpushed, 56/60/65 dirty paths) while the measured values were 133 and 57.** `git rev-list --count e0e3080..e9078d1` independently returns 133, matching the measured pre-land count; the executors re-measured rather than copying the plan numbers. The dirty-path number is historical and no longer reconstructible; the records themselves are inconsistent (`01-01-SUMMARY.md` says 60, the orchestrator context says 57). **Verdict: irrelevant to the must-haves** — the deliverables are the commits, proofs, and remote state, and none depends on the stale planning figure.

**(c) The in-app remount probe returned `sameNode: true`.** Collapse/expand does not recreate `.ads-diagram-wrapper`, so the real app could not demonstrate the remount path; `01-07-SUMMARY.md` records this explicitly and names the 01-02 harness as the primary proof. The real-app pointer drop was observed (canvas descendants 2→11; 9 pre-existing Application Insights 400s before and after, zero drop-phase entries). **Verdict: satisfies SC#4 through the designated deterministic harness; weakens only the in-app-remount demonstration**, which the phase never claimed. The committed Playwright E2E remains Phase 8 / TEST-04 scope (D-15).

**(d) Four untracked `.planning/.omp-*.json` / `milestone.lock` files exist.** `git status --porcelain` is therefore not literally empty, although `git status --porcelain --untracked-files=no` is empty and a fresh clone is clean. These are live orchestrator state files, deliberately untracked; the phase-gate closing step owns committing residual `.planning` paths at phase close. **Verdict: weakens the literal "git status is clean" clause, irrelevant to the ticket's SC#2 conditions** (remote head, ancestry, non-`.planning` unpushed = 0) and to the cloned deliverable.

**Note on proof SHAs.** The summaries record worktree proofs at the isolated-workspace SHAs `a2b934b`/`8f5b7f1`/`ba4da2e`, while the pushed commits are `078a9d7`/`ea8ac77`/`3315b94`. The child commits still exist under `.omp/wt`; tree comparison shows the core trees are identical and the shell/docs trees differ only by a `.planning/*-SUMMARY.md` file that landed after the proof. The proofs therefore cover the pushed source trees. The suites were also independently re-run at all three pushed SHAs (753/755/755) for this verification.

## Human Verification

None required for phase acceptance. The phase is an infrastructure/landing gate with no user-facing surface. The signed-in real-app remount smoke is not reachable in this phase (collapse/expand does not recreate the wrapper), and the committed E2E replacement is explicitly owned by Phase 8 (TEST-04); the deterministic 01-02 harness transcript and the observed real-app drop cover SC#4 for this phase.

## Fingerprint

```json
{
  "covered_files": [
    ".gitignore",
    ".planning/PROJECT.md",
    ".planning/codebase/CONCERNS.md",
    ".planning/phases/01-land-and-stabilize/01-01-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-01-SUMMARY.md",
    ".planning/phases/01-land-and-stabilize/01-02-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-02-SUMMARY.md",
    ".planning/phases/01-land-and-stabilize/01-03-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-03-SUMMARY.md",
    ".planning/phases/01-land-and-stabilize/01-04-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-04-SUMMARY.md",
    ".planning/phases/01-land-and-stabilize/01-05-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-05-SUMMARY.md",
    ".planning/phases/01-land-and-stabilize/01-06-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-06-SUMMARY.md",
    ".planning/phases/01-land-and-stabilize/01-07-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-07-SUMMARY.md",
    ".planning/phases/01-land-and-stabilize/01-08-PLAN.md",
    ".planning/phases/01-land-and-stabilize/01-08-SUMMARY.md",
    "AGENTS.md",
    "README.md",
    "docs/architecture.md",
    "docs/assessment-2026-10-21.md",
    "docs/azure-refresh-plan.md",
    "docs/entra-setup.md",
    "docs/next-phases-plan.md",
    "docs/top-100-ui-improvements.md",
    "docs/v1-plan.md",
    "docs/v3-plan.md",
    "src/AzureDesignStudio.Core.Tests/LinkTypeTests.cs",
    "src/AzureDesignStudio.Core.Tests/StencilTests.cs",
    "src/AzureDesignStudio.Core.Tests/SubnetPlacementTests.cs",
    "src/AzureDesignStudio.Core/Common/LinkTypeClassifier.cs",
    "src/AzureDesignStudio.Core/Components/AzureGroupComponent.razor.css",
    "src/AzureDesignStudio.Core/Components/AzureNodeComponent.razor.css",
    "src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs",
    "src/AzureDesignStudio.Core/DataModelFactory.cs",
    "src/AzureDesignStudio.Core/Network/AzureFirewallForm.razor",
    "src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs",
    "src/AzureDesignStudio.Core/Network/BastionsForm.razor",
    "src/AzureDesignStudio.Core/Network/BastionsModel.cs",
    "src/AzureDesignStudio.Core/Network/SubnetForm.razor",
    "src/AzureDesignStudio.Core/Network/SubnetModel.cs",
    "src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs",
    "src/AzureDesignStudio.Desktop/MauiProgram.cs",
    "src/AzureDesignStudio.Desktop/wwwroot/app.html",
    "src/AzureDesignStudio/Components/CommandPalette.razor",
    "src/AzureDesignStudio/Components/DesignInspector.razor",
    "src/AzureDesignStudio/Components/DiagramNavigator.razor",
    "src/AzureDesignStudio/Components/DiagramNavigator.razor.css",
    "src/AzureDesignStudio/Components/DiagramPanel.razor",
    "src/AzureDesignStudio/Components/DiagramPanel.razor.cs",
    "src/AzureDesignStudio/Components/DiagramPanel.razor.css",
    "src/AzureDesignStudio/Components/StatusBar.razor",
    "src/AzureDesignStudio/Components/Stencil.razor",
    "src/AzureDesignStudio/Components/Stencil.razor.css",
    "src/AzureDesignStudio/Components/StencilPanel.razor",
    "src/AzureDesignStudio/Components/StencilPanel.razor.css",
    "src/AzureDesignStudio/Components/ThemeToggle.razor",
    "src/AzureDesignStudio/Components/TopMenu.razor",
    "src/AzureDesignStudio/Components/TopMenu.razor.cs",
    "src/AzureDesignStudio/Components/TopMenu.razor.css",
    "src/AzureDesignStudio/Models/StencilModel.cs",
    "src/AzureDesignStudio/Pages/Index.razor",
    "src/AzureDesignStudio/Pages/Index.razor.css",
    "src/AzureDesignStudio/Program.cs",
    "src/AzureDesignStudio/Services/AdsCommandService.cs",
    "src/AzureDesignStudio/Shared/MainLayout.razor",
    "src/AzureDesignStudio/wwwroot/ads-stencils.json",
    "src/AzureDesignStudio/wwwroot/assets/azure/subnet.svg",
    "src/AzureDesignStudio/wwwroot/assets/icons/ai.svg",
    "src/AzureDesignStudio/wwwroot/assets/icons/containers.svg",
    "src/AzureDesignStudio/wwwroot/assets/icons/general.svg",
    "src/AzureDesignStudio/wwwroot/assets/icons/integration.svg",
    "src/AzureDesignStudio/wwwroot/assets/icons/security.svg",
    "src/AzureDesignStudio/wwwroot/css/ads-tokens.css",
    "src/AzureDesignStudio/wwwroot/index.html",
    "src/AzureDesignStudio/wwwroot/js/ads.js"
  ],
  "covered_digest": "v3:sha256:429a4de0b22f9b211daab13eaca59def432e60af4208c6c0843038e1536656cc"
}
```

## Self-Check: PASSED

All five ROADMAP success criteria and ten must-haves re-hold against the landed commits; no regression, no gap. Digest covers the working tree; verdict covers the landed commits.

---

_Verified: 2026-10-31T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
