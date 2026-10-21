# Phase 01: Land and Stabilize - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-21
**Phase:** 01-Land and Stabilize
**Areas discussed:** Commit slicing, Untrack policy, Remote landing, Pointer-zone fix, Docs + publisher

---

## Commit slicing

### Commit count and hygiene placement

| Option | Description | Selected |
|--------|-------------|----------|
| Three: core / shell / docs | Honors SC#3 as written; blob/scratch removals ride in commit 1 with the domain change | ✓ |
| Three: hygiene in docs | Same three code commits, DLL/scratch removals go in the docs commit; docs diff then mixes binary removals | |
| Four: hygiene first | Cleanest separation, but contradicts SC#3's "three reviewable commits" | |
| Per-surface (~8 commits) | One commit per Round 2 surface; smallest diffs, most intermediate commits that do not build | |

**User's choice:** Three: core / shell / docs (hygiene inside core)
**Notes:** Surfaced before the ask that a standalone hygiene commit would amend SC#3; the user kept SC#3 literal.

### Per-commit green proof

| Option | Description | Selected |
|--------|-------------|----------|
| Worktree per commit | `git worktree add` at each SHA, full solution build, offline suite expecting 755 passed | ✓ |
| Clone per commit from bundle | Restore each commit out of the out-of-band bundle; stronger, slower | |
| Tip only | Build/test final head only; asserts SC#3 rather than proving it | |

**User's choice:** Worktree per commit
**Notes:** `dotnet build src/AzureDesignStudio.sln` includes the Windows-only Desktop project, so only this machine can run the proof.

### Catalog/test boundary

| Option | Description | Selected |
|--------|-------------|----------|
| Test rides with subject | Core = Core/** + new Core tests + hygiene; shell = WASM/Desktop + `ads-stencils.json` + 7 SVGs + `StencilTests.cs` | ✓ |
| Catalog rides with core | Pull `ads-stencils.json` into commit 1 so `Core.Tests` stays green; palette data lands before the code that reads it | |
| Split the test edit | Commit 1 keeps categories 0–5, commit 2 flips to 0–9; always green but the intermediate never existed | |

**User's choice:** Test rides with subject
**Notes:** Verified constraint — `Core.Tests.csproj:34` links `wwwroot/ads-stencils.json` as Content and the new `EveryCategoryHasAtLeastOneStencil` asserts categories 0–9 while HEAD has only 0–5, so a path-glob split leaves commit 1 red.

### `others.svg`

| Option | Description | Selected |
|--------|-------------|----------|
| Restore the file | `git checkout HEAD -- …/others.svg` in the shell commit; fallback keeps working | ✓ |
| Re-point fallback, keep deletion | Point `TopologyView.razor:316` at an existing icon; changes Desktop rendering for unknown types | |
| Ship deletion, log defect | Keep the deletion, defer the dangling reference to Phase 7 | |

**User's choice:** Restore the file
**Notes:** `TopologyView.razor:316` (unmodified, Desktop) still returns `assets/icons/others.svg` as its fallback, so the deletion was a dangling reference rather than a clean retirement.

**Continue check:** Next area.

---

## Untrack policy

### History handling

| Option | Description | Selected |
|--------|-------------|----------|
| Forward-only `git rm` | Tip removal; blobs stay in the 126 local commits' history; no force-push | ✓ |
| Rewrite local range first | `git filter-repo` over the 126 unpushed commits so the blobs never publish; every SHA changes | |
| Rewrite incl. public history | Purge blobs and `.omo/.swarm` from all history; force-push over public `origin/main` | |

**User's choice:** Forward-only `git rm`
**Notes:** Facts that shaped it — `.omo` and `.swarm` are already trees in public `origin/main`; `bd2.dll`/`bd_served.dll` appear only in the 126 unpushed commits; 0 tracked `bin/obj`; 1 tag (`v0.3.8`, already pushed); 0 stashes; no bundle existed.

### Scratch content

| Option | Description | Selected |
|--------|-------------|----------|
| Delete all three | `.swarm/plan.md`, `.swarm/spec.md`, `.omo/run-continuation/*.json` are dead; content survives in `docs/azure-refresh-plan.md` | ✓ |
| Archive both to `docs/archive/` | Move plan + spec under `docs/archive/` before untracking | |
| Keep spec only | Spec is more reusable; the plan is a completed wave list | |

**User's choice:** Delete all three
**Notes:** `plan.md` targets .NET 8 + a Blazor.Diagrams submodule the repo no longer has; the `.omo` file is a July 2026 background-task session record.

### Ignore rules

| Option | Description | Selected |
|--------|-------------|----------|
| All four groups | Anchored `/*.dll`, `.omo/`, `.swarm/`, `.planning/.omp-ui-state.json` | ✓ |
| Dirs only | Ignore `.omo/` and `.swarm/` only; no DLL rule | |
| No ignore change | Rely on the one-time `git rm` only | |

**User's choice:** All four groups

### SC#1 verification

| Option | Description | Selected |
|--------|-------------|----------|
| Recorded command audit | Plan records the exact `git ls-files`/`git status` checks; executor pastes output; Phase 2 automates in CI | ✓ |
| Checked-in script | `scripts/verify-tracked.ps1` asserting the four conditions | |
| Fresh clone from origin | Clone the pushed repo and inspect what arrived | |

**User's choice:** Recorded command audit

**Continue check:** Next area.

---

## Remote landing

### Push topology

| Option | Description | Selected |
|--------|-------------|----------|
| Direct push to main | Matches SC#2; keeps 126 SHAs auditable; no branch-protection config in-repo | ✓ |
| PR for the 3 land commits | Review as a unit; but no PR CI exists until Phase 2 | |
| Integration branch, then ff main | Verify on `land/stabilize`, fast-forward main after | |

**User's choice:** Direct push to main

### Push sequencing

| Option | Description | Selected |
|--------|-------------|----------|
| Two pushes: pre-existing, then land | 126 first, then the 3 land commits; failures isolated | ✓ |
| One push after full proof | Atomic from the remote's view; a failed 129-commit push says nothing about the cause | |
| Push per land commit | Remote tip always verified; more pushes, intermediate heads unvalidated as releases | |

**User's choice:** Two pushes: pre-existing, then land

### Bundle

| Option | Description | Selected |
|--------|-------------|----------|
| Sibling file, clone-verified | `../AzureDesignStudio-<sha7>-<date>.bundle --all`, proven by cloning and comparing HEAD, deleted at phase close | ✓ |
| Outside the project dir | `%LOCALAPPDATA%` backup path; safer from `git add -A`, harder to find | |
| No bundle | Rely on worktree proof + remote copy; would amend SC#2 | |

**User's choice:** Sibling file, clone-verified

### Push rejection

| Option | Description | Selected |
|--------|-------------|----------|
| Verify, stop on rejection | `ls-remote` must equal `rev-parse HEAD`; stop and report on rejection; never force | ✓ |
| Auto-rebase and retry | Fetch, rebase, retry; a silent rebase over 126 commits invalidates the per-commit proof | |
| Force-with-lease | Treat rejection as a stale-remote signal | |

**User's choice:** Verify, stop on rejection

**Continue check:** Next area.

---

## Pointer-zone fix

### Fix shape

| Option | Description | Selected |
|--------|-------------|----------|
| Element guard + re-resolve | `zone.__adsPointerInit` guard, document listeners once, re-resolve by selector at event time, read `__adsRef` off the live node | ✓ |
| Pure document delegation | Document-only listeners; no per-element flags; diverges from ROADMAP's note | |
| Re-init from Razor lifecycle | Blazor calls init/dispose per mount; puts correctness on every component author | |

**User's choice:** Element guard + re-resolve

### SC#4 proof

| Option | Description | Selected |
|--------|-------------|----------|
| Real app + throwaway harness | Manual before/after pass on the running app plus a deterministic harness page replacing the zone node | ✓ |
| Real app only | Manual pass only; remount triggered by hand | |
| Automate with Playwright now | Script the sweep; TEST-04 / Phase 8 scope, sign-in makes it expensive today | |
| Defer to Phase 8 | Code review only; leaves SC#4 asserted rather than proven | |

**User's choice:** Real app + throwaway harness

### Harness permanence

| Option | Description | Selected |
|--------|-------------|----------|
| Throwaway only | Delete after the smoke; Phase 8 owns the committed E2E | ✓ |
| Commit the harness | Track under `tools/js-harness/`; introduces JS test infrastructure Phase 8 will replace | |
| xUnit text assertion | Assert the guard identifier in JS source; cannot fail for the original reason | |

**User's choice:** Throwaway only

### Fix width

| Option | Description | Selected |
|--------|-------------|----------|
| AdsStencilDrag only | Guard + re-resolution only; `AdsCommandService`, `AdsDropZone`, theme/shortcuts untouched | ✓ |
| Also swallow disposed-ref rejection | Add `.catch` on `invokeMethodAsync`; would mask real target exceptions | |
| Unify both drag paths | Shared `resolveZone` for `AdsDropZone` and `AdsStencilDrag`; touches working code | |

**User's choice:** AdsStencilDrag only
**Notes:** With re-resolution, a missing zone means nothing is invoked, so no unreported rejection path remains.

**Continue check:** Next area.

---

## Docs + publisher

### Docs scope

| Option | Description | Selected |
|--------|-------------|----------|
| README + AGENTS only | Exactly SC#5; historical plan docs untouched | |
| Also fix CONCERNS.md count | Adds `.planning/codebase/CONCERNS.md:9` | |
| Full docs reconcile | README, AGENTS, CONCERNS, `docs/architecture.md`, `docs/v1-plan.md`, `docs/azure-refresh-plan.md` | ✓ |

**User's choice:** Full docs reconcile
**Notes:** 11 `.csproj` verified under `src/`; README's tree lists only 8 (missing Discovery, Workspace, Mcp); `AGENTS.md:69` already states Entra ID work+personal and can source the wording. Zero missing `docs/*.md` citations today.

### Signing route

| Option | Description | Selected |
|--------|-------------|----------|
| Azure Artifact Signing | Managed signing, no key custody, already named in Phase 11; external identity-validation lead time | ✓ |
| OV certificate (PFX in CI) | Public CA cert, PFX + password as CI secrets; custody and expiry work | |
| Controlled-pilot self-signed PFX | Fastest to an installable MSIX; no public trust, cannot satisfy "verifies with a timestamp" | |

**User's choice:** Azure Artifact Signing

### Publisher name

| Option | Description | Selected |
|--------|-------------|----------|
| Registered legal entity | Legal entity name as the certificate subject | |
| Individual (sole developer) | Personal legal name as subject | ✓ |
| Record route, name TBD | Close the phase with SC#5's name unmet | |

**User's choice:** Individual (sole developer)

### Subject string

| Option | Description | Selected |
|--------|-------------|----------|
| `CN=Jakub Talaj` | The repo's git identity for 128 of 245 commits; `PublisherDisplayName` can stay "Azure Design Studio" | ✓ |
| `CN=Jakub Talaj, O=…` | Name plus organization, as a registered-entity validation issues | |
| Different subject | Type the exact expected subject | |

**User's choice:** `CN=Jakub Talaj`

### Manifest placeholder

| Option | Description | Selected |
|--------|-------------|----------|
| Record only, Phase 11 edits | Leave `Package.appxmanifest:9` (`Publisher="CN=Azure Design Studio"`) untouched; Phase 11 owns the publisher authority | ✓ |
| Fix the placeholder now | Change `Publisher` to `CN=Jakub Talaj`; unverifiable until a certificate exists | |
| Fix now + mark provisional | Change it and note the value is provisional | |

**User's choice:** Record only, Phase 11 edits

**Final check:** "I'm ready for context" (no additional areas).

---

## Claude's Discretion

- Commit subject wording and whether land commits carry a scope (`land(core): …`), following repo convention (bare `type:` with scoped `docs(01):` forms from GSD).
- Exact per-commit path inventory, within D-03's boundary and D-02's green proof.
- Harness filename/location and the temp-clone path used to verify the bundle.
- Order of operations inside the core commit (hygiene removal vs code staging).
- Whether the two pushes are scripted or run by hand.

## Deferred Ideas

- Retire `assets/icons/others.svg` and re-point `TopologyView.razor`'s `_ =>` fallback (kept out to hold the land behavior-neutral).
- Remove the divergent `src/Configurations/ads-stencils.json` catalog copy.
- Prune or document stale `origin/dev` and `origin/Hackathon22`.
- `AdsCommandService` / registry owner lifetime — SHELL-02, Phase 7.
- Committed pointer-zone regression test — TEST-04 Playwright E2E, Phase 8.
