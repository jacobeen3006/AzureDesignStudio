---
phase: "01"
slug: "land-and-stabilize"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-10-21"
---

# Phase 01 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `01-RESEARCH.md` § Validation Architecture. `workflow.nyquist_validation: true`, `workflow.human_verify_mode: end-of-phase` — manual proofs are collected at the phase gate, not per wave.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit 2.9.2 (`Microsoft.NET.Test.Sdk` 17.12.0, `xunit.runner.visualstudio` 2.8.2) |
| **Config file** | none — single `src/AzureDesignStudio.Core.Tests` csproj, no `xunit.runner.json`, no `.runsettings` |
| **Quick run command** | `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` |
| **Full suite command** | `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` (the LiveAzure category needs `az login` and is out of scope) |
| **Estimated runtime** | ~10–30 s after build; wall clock dominated by `dotnet build` |
| **Expected case count** | **753** at the core commit, **755** at shell and docs commits, 764 total including LiveAzure |

**No new test framework may be introduced this phase** (D-15). There is no JS toolchain — no node, no Jest/Vitest, no Playwright until Phase 8 (TEST-04). The pointer-zone proof is a throwaway harness page, not a committed test.

---

## Sampling Rate

- **After every task commit:** none — this phase commits three times, not per task. Task-level feedback is file inspection (`git status`, `git diff --stat`), not the suite.
- **After every plan wave:** `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` (expect 753 pre-shell, 755 from shell onward).
- **Per land commit (before push):** `git worktree add` at the SHA → `dotnet build src/AzureDesignStudio.sln` (Desktop included) → full offline suite → `git worktree remove --force`.
- **Per push:** `git ls-remote origin refs/heads/main` must equal `git rev-parse HEAD` **at the measurement instant** (D-12); because `commit_docs` is true, the durable per-push invariant is `git rev-list --count origin/main..HEAD -- . ':!.planning/'` = 0, asserted alongside it.
- **Phase close (plan 01-08):** the convergence push includes the last metadata commit it depends on, the bundle is re-cut at `CONVERGE_SHA` and clone-verified, and the phase gate's single closing push (`git push origin main`, then `ls-remote` == `rev-parse HEAD`) is the only place SC#2's literal equality is claimed.
- **Before `/gsd:verify-work`:** the post-push offline suite run must be green at the final head.
- **Max feedback latency:** ~120 s per worktree cycle (build-bound).

---

## Per-Task Verification Map

Task IDs are assigned by the planner; this map is keyed to the success criteria the tasks must satisfy. Fill real task IDs at plan time and confirm each `File Exists` claim.

| Task ID | Plan | Wave | Success Criterion | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | 02 | 2 | SC#4 — pointer zone survives remount | T-01-05…08 | N/A (client-side drag guard) | JS harness (throwaway) | harness page outside the repo: replace zone node twice → re-init → synthetic pointerdown/move/up → assert `__adsRef.invokeMethodAsync` fired once | ❌ W0 — harness authored in a temp dir, never tracked (D-15) | ⬜ pending |
| TBD | 02 | 2 | SC#4 — "before" evidence | T-01-05…08 | N/A | manual transcript | run the harness against the **unfixed** working-tree `ads.js` first; expect `"calls":[]` | ❌ W0 — must be captured before the fix lands | ⬜ pending |
| TBD | 01 | 1 | SC#3 — core commit restores + builds + suite green | T-01-03 | N/A | build + suite | `git worktree add …; dotnet build src/AzureDesignStudio.sln; dotnet test … --filter "Category!=LiveAzure"` expecting `Passed: 753` (753 + 2 stencil facts in shell = 755) | ✅ worktree is the harness | ⬜ pending |
| TBD | 03 | 3 | SC#3 — shell commit restores + builds + suite green | T-01-12 | N/A | build + suite | same, expecting `Passed: 755` | ✅ | ⬜ pending |
| TBD | 05 | 5 | SC#3 — docs commit restores + builds + suite green | T-01-20 | N/A | build + suite | same, expecting `Passed: 755` | ✅ | ⬜ pending |
| TBD | 01/03/05 | 1,3,5 | SC#3 — one topic per commit | T-01-04, T-01-11, T-01-17 | N/A | command audit | per commit: `git show --name-only`, path-set equality, zero deletions where required | ✅ | ⬜ pending |
| TBD | 01 | 1 | SC#1 — tracked tree stops lying | T-01-02 | N/A | command audit | `git ls-files` captured then grepped for `\.dll$\|^\.(omo\|swarm)/` (empty); `git ls-files .planning \| wc -l` ≥ 22; `git status --porcelain` captured and empty | ✅ audit only — D-08 forbids a committed script here | ⬜ pending |
| TBD | 05/07 | 5,7 | SC#1 — cited docs resolve | T-01-18, T-01-25 | N/A | command audit | citation extraction is non-empty and every `docs/*.md` path named by README/AGENTS passes `git ls-files --error-unmatch` | ✅ | ⬜ pending |
| TBD | 04/05 | 4,5 | SC#5 — docs truth | T-01-14, T-01-18 | N/A | command audit | retired-provider grep exits 1 across the four current-truth files; stale `(7\|8) projects?` grep exits 1; `11 project` present in each | ✅ | ⬜ pending |
| TBD | 04/05 | 4,5 | SC#5 — publisher recorded | T-01-13, T-01-19 | N/A | command audit | `CN=Jakub Talaj` and `Azure Artifact Signing` each exactly once in `PROJECT.md` and `docs/entra-setup.md`; manifest `git diff --quiet` | ✅ | ⬜ pending |
| TBD | 06 | 6 | SC#2 — pre-land tip and land published | T-01-21, T-01-24 | N/A | command audit | `git push origin "$PRE:refs/heads/main"` then `ls-remote` == `$PRE`; second push then `ls-remote` == `HEAD` at the measurement instant | ✅ | ⬜ pending |
| TBD | 06/07 | 6,7 | SC#2 — bundle restores its recorded head | T-01-23, T-01-25 | N/A | command audit | `git bundle create … --all` at `BUNDLE_SHA`; clone; compare clone `rev-parse HEAD` to `BUNDLE_SHA`; `rev-list --count "$BUNDLE_SHA"..HEAD -- . ':!.planning/'` = 0 | ✅ | ⬜ pending |
| TBD | 08 | 8 | SC#2 — convergence and literal head equality | T-01-29, T-01-30, T-01-31 | N/A | command audit + human check | push, then `ls-remote` == `CONVERGE_SHA`; bundle re-cut and clone-verified; `rev-list --count origin/main..HEAD -- . ':!.planning/'` = 0; gate command recorded and run at phase close | ✅ | ⬜ pending |
| TBD | 07 | 7 | No regression at final head | T-01-20 | N/A | suite | full offline suite at the local head — `Passed: 755`, tree clean before and after | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] **Throwaway JS harness page** — authored under `C:/Users/jakub/AppData/Local/Temp/ads-land-harness/` (outside the repository), loading the repo module over `file:///`, and deleted in plan 01-02 Task 3 (D-15). It never enters `git ls-files` and is not a Wave 0 file in the tree. This is the only automated SC#4 evidence, and it must be authored before the fix because the "before" run needs the unfixed file.
- [ ] **"Before" transcript** captured against the unfixed working-tree `ads.js` — cannot be regenerated after the fix lands.
- [ ] **Worktree build/test cycle commands** recorded verbatim in the plan so the phase summary can quote real output from three sequential `worktree add → build → test → remove --force` cycles.
- [ ] **No new .NET test files for the JS defect** (D-15). `LinkTypeTests.cs` and `SubnetPlacementTests.cs` already exist in the working tree and travel with the core commit — they are part of the 753 at core and 755 from shell onward.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Stencil drop works in the real app after a drawer remount, zero console errors | SC#4 | Both `appsettings.json` files carry a placeholder `ClientId` and the router is behind `AuthorizeRouteView`, so a browser smoke needs interactive Entra sign-in — not automatable in this phase (research Q3) | `dotnet run --project src/AzureDesignStudio`, sign in, collapse/expand the rail, pointer-drag a stencil onto the canvas. Expect the node plus **zero** DevTools console errors. If sign-in is unavailable, record the harness transcript as the primary proof and log the app smoke as a deferred manual check at the phase gate. |
| Three commits read as one topic each | SC#3 | Judgement about reviewability, not an assertion | `git show --stat` and `git show --name-only` for each of the three SHAs; confirm core = domain + tests + hygiene, shell = WASM/Desktop/stencils/assets, docs = prose only. |
| Publisher decision recorded | SC#5 | The decision itself is a human commitment (D-19/D-20) | Confirm `PROJECT.md` Key Decisions gains a row with `CN=Jakub Talaj` + Azure Artifact Signing, and `docs/entra-setup.md` mirrors it; the manifest `Publisher` is knowingly left to Phase 11 (D-21). |
| `origin/main` literally equals the local head, then the recovery bundle is removed | SC#2 | `commit_docs` is true: execute-phase writes `docs(phase-1): complete phase execution` after every plan task, so no task can be the last write. Plan 01-08 records the step and its PASS condition. | After the completion commit exists, run `git push origin main`, confirm `git ls-remote origin refs/heads/main \| cut -f1` equals `git rev-parse HEAD`, then `rm -f` the converged bundle. A mismatch or rejection is reported, never forced (D-12). The durable invariant — `git rev-list --count origin/main..HEAD -- . ':!.planning/'` = 0 — is what the automated checks assert. |

*All remaining phase behaviors have automated (command-audit or suite) verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
