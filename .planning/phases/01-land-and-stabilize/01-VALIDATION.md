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
- **Per push:** `git ls-remote origin refs/heads/main` must equal `git rev-parse HEAD` (D-12).
- **Before `/gsd:verify-work`:** the post-push offline suite run must be green at the final head.
- **Max feedback latency:** ~120 s per worktree cycle (build-bound).

---

## Per-Task Verification Map

Task IDs are assigned by the planner; this map is keyed to the success criteria the tasks must satisfy. Fill real task IDs at plan time and confirm each `File Exists` claim.

| Task ID | Plan | Wave | Success Criterion | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | 01 | 1 | SC#4 — pointer zone survives remount | — | N/A (client-side drag guard) | JS harness (throwaway) | harness page: replace zone node → re-init → synthetic pointerdown/move/up → assert `__adsRef.invokeMethodAsync` fired once | ❌ W0 — harness must be authored, then deleted (D-15) | ⬜ pending |
| TBD | 01 | 1 | SC#4 — "before" evidence | — | N/A | manual transcript | run harness against the **unfixed** working-tree `ads.js` first | ❌ W0 — must be captured before the fix lands | ⬜ pending |
| TBD | 01 | 2 | SC#3 — core commit restores + builds + suite green | — | N/A | build + suite | `git worktree add …; dotnet build src/AzureDesignStudio.sln; dotnet test … --filter "Category!=LiveAzure"` expecting `Passed: 753` | ✅ worktree is the harness | ⬜ pending |
| TBD | 01 | 2 | SC#3 — shell commit restores + builds + suite green | — | N/A | build + suite | same, expecting `Passed: 755` | ✅ | ⬜ pending |
| TBD | 01 | 2 | SC#3 — docs commit restores + builds + suite green | — | N/A | build + suite | same, expecting `Passed: 755` | ✅ | ⬜ pending |
| TBD | 01 | 2 | SC#3 — one topic per commit | — | N/A | manual diff review | `git show --stat` + `git show --name-only` ×3 | ✅ | ⬜ pending |
| TBD | 01 | 2 | SC#1 — tracked tree stops lying | — | N/A | command audit | `git ls-files \| grep -E '\.dll$\|^\.(omo\|swarm)/'` empty; `git ls-files .planning \| wc -l` ≥ 21; `git status --porcelain` empty | ✅ audit only — D-08 forbids a committed script here | ⬜ pending |
| TBD | 01 | 2 | SC#1 — cited docs resolve | — | N/A | command audit | citation loop: every `docs/*.md` path named by README/AGENTS passes `git ls-files --error-unmatch` | ✅ | ⬜ pending |
| TBD | 01 | 2 | SC#5 — docs truth | — | N/A | command audit | `grep -c "8 project" README.md AGENTS.md` → 0; `grep -n "11 project"` present; `grep -rn "B2C" AGENTS.md` → 0 | ✅ | ⬜ pending |
| TBD | 01 | 2 | SC#5 — publisher recorded | — | N/A | command audit | `grep -n "CN=Jakub Talaj" .planning/PROJECT.md docs/entra-setup.md`; `grep -n "Artifact Signing"` — 2 hits each | ✅ | ⬜ pending |
| TBD | 01 | 3 | SC#2 — remote head equals local head | — | N/A | command audit | `git ls-remote origin refs/heads/main` == `git rev-parse HEAD`, after each push | ✅ | ⬜ pending |
| TBD | 01 | 3 | SC#2 — bundle restores | — | N/A | command audit | `git bundle create ../AzureDesignStudio-<sha7>-<date>.bundle --all`; `git clone` into temp; compare `rev-parse HEAD` | ✅ | ⬜ pending |
| TBD | 01 | 3 | No regression at final head | — | N/A | suite | full offline suite once more after push | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] **Throwaway JS harness page** — suggested path `src/AzureDesignStudio/wwwroot/__land-harness.html`, deleted before the shell commit (D-15). This is the only automated SC#4 evidence; it must be authored before the fix, because the "before" run needs the unfixed file.
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
