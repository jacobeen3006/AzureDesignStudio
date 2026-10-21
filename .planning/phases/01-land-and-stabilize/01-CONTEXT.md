# Phase 01: Land and Stabilize - Context

**Gathered:** 2026-10-21
**Status:** Ready for planning

<domain>
## Phase Boundary

Get the milestone's starting point off one machine. The uncommitted slice (56 dirty paths, 126 unpushed commits) lands in three reviewable commits on `origin/main`; tracked artifacts stop lying (root DLL blobs, agent scratch); the stale pointer zone in `ads.js` is fixed; docs state current truth; and the publisher identity plus signing route is decided and recorded.

No v1 requirement ships here — this is the enabling gate for BUILD-01 and protects every later diff. Scope is **landing and repair**, not new capability: the command registry's owner lifetime (SHELL-02), palette/inspector completion (SHELL-01/03/04), CI, migrations, and signing execution all belong to later phases.

</domain>

<decisions>
## Implementation Decisions

### Commit boundary
- **D-01:** The land is exactly three commits — core / shell / docs — with the tracked-artifact removals (`bd2.dll`, `bd_served.dll`, `.omo/`, `.swarm/`) riding inside the **core** commit. Keeps SC#3's "three reviewable commits" literal. — **Reversibility:** costly — once pushed, re-slicing means rewriting public history on `origin/main`.
- **D-02:** Each commit is verified green *before* push via a throwaway worktree: `git worktree add` at the SHA, `dotnet build src/AzureDesignStudio.sln` (Desktop included, Windows-only so only this machine can prove it), `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` expecting 755 passed, then `git worktree remove`.
- **D-03:** Tests travel with their subject. Core commit = `src/AzureDesignStudio.Core/**` + the new Core tests (`LinkTypeTests.cs`, `SubnetPlacementTests.cs`) + hygiene. Shell commit = `src/AzureDesignStudio/**` (WASM), `src/AzureDesignStudio.Desktop/**`, `wwwroot/ads-stencils.json`, the seven new SVG assets, **and** `src/AzureDesignStudio.Core.Tests/StencilTests.cs`. Reason (verified): `AzureDesignStudio.Core.Tests.csproj:34` links `..\AzureDesignStudio\wwwroot\ads-stencils.json` as Content, and the new `EveryCategoryHasAtLeastOneStencil` asserts categories 0–9 while the HEAD catalog only has 0–5 — a path-glob split leaves commit 1 red. — **Reversibility:** costly — same reason as D-01.
- **D-04:** `src/AzureDesignStudio/wwwroot/assets/icons/others.svg` is **restored** (`git checkout HEAD -- …`) in the shell commit, not deleted. `TopologyView.razor:316` still returns `assets/icons/others.svg` as its fallback for unknown resource types, so the working-tree deletion was a dangling reference. Retiring the icon is deferred, not done here.

### Repository hygiene
- **D-05:** Removals are **forward-only** (`git rm` at the tip). No history rewrite. — **Reversibility:** one-way — `.omo/` and `.swarm/` are already in public `origin/main`, and purging the two root blobs from the 126 local commits later requires a force-push over public history that invalidates every existing clone. The tip satisfies SC#1; history does not need to.
- **D-06:** `.swarm/plan.md` (227 lines, a ".NET 8 refresh" plan the repo has outgrown — it references a Blazor.Diagrams submodule that no longer exists), `.swarm/spec.md` (95 lines, superseded by tracked `docs/azure-refresh-plan.md`), and `.omo/run-continuation/ses_*.json` (July 2026 background-task session record) are **deleted outright** — no archiving. Nothing is lost that isn't already in `docs/`.
- **D-07:** `.gitignore` gains four rules: anchored `/*.dll`, `.omo/`, `.swarm/`, and `.planning/.omp-ui-state.json` (currently untracked and dirty). `.planning/` otherwise stays tracked (SC#1).
- **D-08:** SC#1 is proven by a **recorded command audit** whose output goes in the phase summary — not a checked-in script (Phase 2 owns CI): `git ls-files | grep -E '\.dll$|^\.(omo|swarm)/'` empty, `git ls-files .planning | wc -l` non-zero, `git status --porcelain` empty, and every `docs/*.md` path cited by README/AGENTS resolving (verified today: zero missing citations).

### Remote landing
- **D-09:** Direct push to `origin/main` — no PR, no integration branch. Matches SC#2 verbatim ("`origin/main` points at the local head"), keeps all 126 commit SHAs auditable, and there is no branch-protection evidence in-repo (no CODEOWNERS, no PR template, no required-checks config).
- **D-10:** Two pushes: the 126 pre-existing commits first, then the 3 land commits after the worktree proof. A failure is then isolated to the thing that failed.
- **D-11:** Out-of-band bundle at the **project parent** (outside the tree, never trackable): `git bundle create ../AzureDesignStudio-<sha7>-<date>.bundle --all`, proven by `git clone`-ing it into a temp dir and comparing `rev-parse HEAD` to the local head. Deleted when the phase closes — recovery insurance, not an artifact. `origin/dev` and `origin/Hackathon22` are left alone; tag `v0.3.8` is already pushed and does not move.
- **D-12:** Push failure handling: after each push, `git ls-remote origin refs/heads/main` must equal `git rev-parse HEAD`; on any rejection (auth, protection, non-fast-forward) **stop and report**. Never `--force` or `--force-with-lease`; no auto-rebase over 126 commits.

### Pointer-zone fix
- **D-13:** Fix shape is element-scoped guard + event-time re-resolution, confined to `window.AdsStencilDrag` in `src/AzureDesignStudio/wwwroot/js/ads.js`: guard becomes `zone.__adsPointerInit`, document listeners attach once behind a separate `window.__adsPointerDocInit`, and `inZone`/`clear`/`finish`/the move handlers re-resolve via `document.querySelector('.ads-diagram-wrapper')` at event time and read `__adsRef` off the **live** node. Handles any number of remounts; keeps existing drag semantics. If the zone is absent at event time, nothing is invoked (no stale `DotNetObjectReference`, therefore no console error).
- **D-14:** Evidence for SC#4 is two proofs: (1) real app — `dotnet run --project src/AzureDesignStudio`, sign in, remount the rail (collapse/expand), pointer-drag a stencil onto the canvas, expect the node plus **zero** DevTools console errors; the same steps on the pre-fix commit must show the failure. (2) a throwaway harness HTML page in `wwwroot` temp that loads `ads.js`, calls `init`, replaces the zone node, calls `init` again, dispatches synthetic pointerdown/move/up and asserts `__adsRef.invokeMethodAsync` fired — deterministic, no auth.
- **D-15:** The harness is **not committed** — deleted after the smoke, steps and output recorded in the phase summary. TEST-04 (Phase 8) owns the committed Playwright E2E covering stencil drop and zero console exceptions; the repo has no JS test toolchain today and this phase will not create one.
- **D-16:** Scope is `AdsStencilDrag` only. Untouched: `AdsCommandService`/registry owner lifetime (SHELL-02, Phase 7), `AdsDropZone`'s already element-scoped `zone.__adsDrop` guard, `AdsTheme`, `AdsShortcuts`. No `.catch` wrapper on `invokeMethodAsync` — D-13's re-resolution removes the disposed-ref path, so swallowing errors would only mask real target bugs.

### Docs truth
- **D-17:** Full docs reconcile, not just the two names in SC#5. Correct to current truth: `README.md:48` ("8 projects" → 11) and its solution tree (add `Discovery`, `Workspace`, `Mcp`); `AGENTS.md:9` ("AAD B2C" → Entra ID work+personal, "8 projects in src/" → 11); `.planning/codebase/CONCERNS.md:9` ("8 projects"); `docs/architecture.md`; and the historical plan docs that state the count or auth model as current fact (`docs/v1-plan.md`, `docs/azure-refresh-plan.md`). Verified today: 11 `.csproj` under `src/`; `AGENTS.md:69` already states the correct auth model.
- **D-18:** The publisher decision is recorded in `.planning/PROJECT.md` Key Decisions (required by SC#5) and mirrored in the MSIX section of `docs/entra-setup.md` (which already documents the `dotnet publish` packaging command).

### Publisher identity and signing route
- **D-19:** Route is **Azure Artifact Signing** (formerly Trusted Signing) — no private-key custody, no PFX in CI secrets, and already the path ROADMAP Phase 11 names (`azure/trusted-signing-action@v0`). It carries an external lead time (Microsoft identity validation), which is why the decision is recorded here at phase entry. — **Reversibility:** costly — switching to an OV PFX later replaces CI secret handling and introduces certificate custody plus expiry tracking.
- **D-20:** Validation is an **individual** (sole developer) validation. Certificate subject, and therefore the manifest `Publisher` and the PROJECT.md record, is `CN=Jakub Talaj` (the repo's git identity for 128 of 245 commits). `PublisherDisplayName` may remain the friendly "Azure Design Studio".
- **D-21:** The land does **not** edit `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest`. Its placeholder `Publisher="CN=Azure Design Studio"` (line 9) is knowingly left until Phase 11, which owns "one publisher authority" and the CI assertion that `Publisher` equals the certificate subject verbatim.

### Claude's Discretion
- Commit subject wording and whether the land commits use a scope (`land(core): …`) — repo history uses bare conventional types (`docs:`, `chore:`, `feat:`), GSD's own commits use scoped forms like `docs(01):`.
- The exact per-commit path inventory, provided D-03's boundary and D-02's green proof hold.
- Filename and location of the throwaway harness page, and the temp-clone path used to verify the bundle.
- Order of operations inside the core commit (hygiene removal vs code staging).
- Whether the post-push push order is scripted or run by hand.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase contract
- `.planning/ROADMAP.md` § Phase 1: Land and Stabilize — goal, success criteria SC#1–SC#5 (three commits, clean clone, pointer zone, docs/publisher), and the implementation note naming `wwwroot/js/ads.js`, `.omo/`/`.swarm/` untracking and `.planning/` retention.
- `.planning/REQUIREMENTS.md` — this phase delivers no requirement; it is the enabler for BUILD-01 (Phase 2) and the PROJECT.md Active item "Current uncommitted shell/domain/docs slice is committed in reviewable chunks and pushed".
- `.planning/PROJECT.md` — Active items, Constraints (11 projects, Entra ID work+personal, signed MSIX for win-x64), and the Key Decisions table where D-19/D-20 must be written.

### Repo truth and defect record
- `.planning/codebase/CONCERNS.md` — "Dead binaries committed at repository root" (`bd2.dll`, `bd_served.dll`, severity Low), "Two divergent `ads-stencils.json` copies", the `ads.js` pointer defect context, and the docs-drift entries; line 9 itself carries the stale "8 projects".
- `docs/assessment-2026-10-21.md` — the 2026-10-21 defect list: `grep "8 project"` → README.md:48 + AGENTS.md:9, the stale pointer zone, the singleton command registry, the two tracked WebCIL blobs.
- `docs/next-phases-plan.md` — recommended execution order and the "Docs truth (N50 partial)" item.
- `docs/v3-plan.md` — N-number authority and the Trust Gate framing; §337 records the docs-drift evidence.

### Code under change
- `src/AzureDesignStudio/wwwroot/js/ads.js` — `window.AdsStencilDrag` (~lines 121–230) is the defect; the global `window.__adsStencilPointerInit` guard and the closed-over `zone` are the root cause. `window.AdsDropZone` (above it) is the element-scoped precedent to copy.
- `src/AzureDesignStudio/Components/DiagramPanel.razor.cs:48-49` — the only caller: `AdsDropZone.init(dropRef, ".ads-diagram-wrapper")` and `AdsStencilDrag.init(".ads-diagram-wrapper")` from `OnAfterRenderAsync`. This is the remount path that the global guard blocks.
- `src/AzureDesignStudio/wwwroot/ads-stencils.json` — modified catalog (categories 0–9, 29 insertions / 22 deletions); belongs to the shell commit per D-03.
- `src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:34` — the `<Content Include="..\AzureDesignStudio\wwwroot\ads-stencils.json" Link="ads-stencils.json">` that forces D-03's boundary.
- `src/AzureDesignStudio.Core.Tests/StencilTests.cs` — new `EveryCategoryHasAtLeastOneStencil` (asserts `Enumerable.Range(0, 10)`) and the widened `category <= 9` assertion.
- `src/AzureDesignStudio.Desktop/Components/TopologyView.razor:316` — `_ => "assets/icons/others.svg"`, the reason D-04 restores the file.
- `src/AzureDesignStudio/wwwroot/assets/icons/others.svg` — deleted in the working tree, tracked in HEAD.

### Docs to correct (D-17)
- `README.md:48` — `## 🏗️ Solution Structure (8 projects)` plus the 8-entry tree that omits `Discovery`, `Workspace`, `Mcp`.
- `AGENTS.md:9` — "hosted as WASM (AAD B2C) … .NET 10, 8 projects in src/". `AGENTS.md:69` already states Entra ID work+personal and can be the wording source.
- `.planning/codebase/CONCERNS.md:9` — "`src/` — 8 projects".
- `docs/architecture.md`, `docs/v1-plan.md:103`, `docs/azure-refresh-plan.md:14` — the remaining statements of the old count/auth model.

### Packaging and publisher (D-19…D-21)
- `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest:9` — `Identity Name="AzureDesignStudio.Desktop" Publisher="CN=Azure Design Studio" Version="1.0.0.0"` and line 15 `PublisherDisplayName`. Untouched this phase; the Phase 11 authority.
- `docs/entra-setup.md` § MSIX publish (line ~72) — where D-18 mirrors the publisher decision.
- `.planning/ROADMAP.md` § Phase 11: Signed MSIX and Packaged Install — the publisher-mismatch (`0x8007000B`) and untrusted-root (`0x800B0109`) failures this decision exists to prevent.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `window.AdsDropZone` in `ads.js` — already does per-element init (`zone.__adsDrop`) and stores the ref on the element (`zone.__adsRef`); it is the pattern `AdsStencilDrag` must adopt rather than invent.
- `window.AdsTheme` / `window.AdsShortcuts` — existing style precedent for globals in the same file, including `window.__adsShortcutRef` handed in by Blazor; useful for judging what "one global guard" should look like.
- `git worktree` + the existing single test project — enough to prove per-commit green with no new tooling.
- The `Content … Link=` pattern in `Core.Tests.csproj` — how a runtime asset is shared with tests without duplication; also the reason D-03 slices as it does.

### Established Patterns
- Repo commit history uses bare conventional types (`docs:`, `chore:`, `feat:`); GSD writes scoped forms (`docs(01): capture phase context`) and commits through `gsd-tools commit`, which is what the phase's own planning artifacts will use.
- JS interop is initiated from Blazor `OnAfterRenderAsync` only (`DiagramPanel.razor.cs:48-49`); there is no JS dispose path today, which is why the fix must be idempotent per element rather than lifecycle-driven.
- `git rm` + `.gitignore` is the only tooling available for hygiene; no `git-filter-repo` was used or installed, and D-05 keeps it that way.

### Integration Points
- `AdsStencilDrag.init` is called with the selector `.ads-diagram-wrapper`; every re-resolution must use that same selector rather than a cached node, and `__adsRef` continues to be written by `AdsDropZone.init` on each remount.
- The shell commit's `ads-stencils.json` is consumed at runtime by `AdsContext` (WASM), `App.xaml.cs` (Desktop, from `AppContext.BaseDirectory`) and by the linked copy in `Core.Tests` — one file, three readers.
- Phase 11 reads the publisher decision from PROJECT.md and asserts it against the certificate subject; nothing in Phase 1 code links to it.

</code_context>

<specifics>
## Specific Ideas

- The land's reviewability test is concrete: after each commit, `git show --stat` should read as one topic — domain logic, then shell, then docs — and no commit should contain a diff belonging to another's topic.
- "Tracked artifacts stop lying" was treated as a literal test, not a slogan: the two 64 KB blobs are referenced by nothing in the repo, `others.svg` was deleted while still being returned by `TopologyView`'s fallback, and `.swarm/plan.md` describes a .NET 8 migration with a submodule the repo removed — all three are files that assert something false.
- The publisher string has one hard requirement: byte-for-byte identity with the certificate subject, because `Publisher` mismatch is the `0x8007000B` failure named in Phase 11.

</specifics>

<deferred>
## Deferred Ideas

- **Retire `assets/icons/others.svg` and re-point `TopologyView.razor`'s `_ =>` fallback** — deferred out of Phase 1 (D-04) to keep the land behavior-neutral; belongs with the shell/icon work.
- **Remove the divergent `src/Configurations/ads-stencils.json` copy** (CONCERNS: "Two divergent `ads-stencils.json` copies", already drifted) — not in Phase 1's scope; belongs with the catalog/ConfigMap linkage work.
- **Prune or document the stale `origin/dev` and `origin/Hackathon22` branches** — left untouched; no phase currently owns remote branch hygiene.
- **`AdsCommandService` / registry owner lifetime (SHELL-02)** — explicitly out of scope; Phase 7.
- **Committed JS-level regression test for the pointer zone** — deferred to TEST-04's Playwright E2E in Phase 8 (D-15).

</deferred>

---

*Phase: 01-Land and Stabilize*
*Context gathered: 2026-10-21*
