# Azure Design Studio — Assessment, Fixes, and Feature Proposals

> **Date:** 2026-10-21 · **Author:** agent assessment (read-only pass)
> **Baseline:** `main` @ `04dfaa2`, **ahead 119** of `origin/main`, **36 modified + 17 untracked** paths.
> **Relationship to other docs:** `docs/v3-plan.md` (2026-10-20) remains the authority for v3 sequencing (N1–N50). This document does **not** re-plan it. It (a) re-verifies the v3 baseline against the live tree, (b) adds seven findings the v3 audit missed, (c) assesses the uncommitted Round-2 shell slice, and (d) proposes five features outside N1–N50.
> **Every claim below was checked with a command in this session.** Nothing in the repository was modified by this assessment.

---

## 1. Verified state

| Check | Command shape | Result |
|---|---|---|
| CI build set | `dotnet build {Server, WASM, Mcp, Core.Tests} -c Release` | **all succeed — 0 Warning(s), 0 Error(s) each** |
| Offline tests | `dotnet test Core.Tests -c Release --filter "Category!=LiveAzure"` | **Passed: 755, Failed: 0, Skipped: 0** (~1 s) |
| Test volume | `grep -c "[Fact]|[Theory]"` | **625 attributes across 68 test files, one project** |
| Projects | `ls src/*/*.csproj` | **11** |
| Docs accuracy | `grep "8 project"` | **README.md:48** "Solution Structure (8 projects)", **AGENTS.md:9** "8 projects in src/" |
| Build-output hygiene | `git ls-files \| grep -c "/(obj\|bin)/"` | **0** (clean) |
| Desktop host | `dotnet build src/AzureDesignStudio.Desktop -c Release` | **in flight at time of writing** — see §6 |

The repository is **green where it is measured**. The problem is what it does not measure (§3, F6).

---

## 2. v3-plan.md baseline — re-verified, still true

Spot-checked against the working tree rather than trusted from the doc:

| v3 claim | Live evidence | Status |
|---|---|---|
| N9 stored subscription secret | 17 `ClientSecret` matches under `Server/Models` + `Server/Services`; **8** `ClientSecretCredential` construction sites | **true** |
| N10 global guardrail policy | `Server/Services/DeployService.cs:1261` → `private const int GuardrailRowId = 1;` | **true** |
| N11 fragile identity | `new Guid(userIdClaim)` call sites unchanged | **true** |
| N13/N14 hardening absent | **zero** matches for `Content-Security-Policy`, `AddRateLimiter`, `MapHealthChecks`, `MaxReceiveMessageSize`, `UseCompression` in any `.cs` | **true** |
| N17 no migrations | no `Migrations/` directory anywhere under `src/`; **7** `EnsureCreated` occurrences | **true** |
| N46 AutoMapper | `Core.csproj:20` + `AzureDesignStudio.csproj:25` → AutoMapper **12.0.1**, plus deprecated `…Extensions.Microsoft.DependencyInjection` 12.0.0 at `:26`, with an **accepted DoS advisory comment citing GHSA-rvv3-g6hj-g44x** | **true** |
| N48 drawer size | `Desktop/Components/TenantScanDrawerTemplate.razor` = **2,481 lines** | **true** |
| N47 no build policy | no `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig` | **true** |
| N1 Desktop not in CI | `build.yml` runs 4 projects on `ubuntu-latest` + test + Docker + CodeQL; **the string `Desktop` appears in no workflow file** | **true** |

**Conclusion: do not re-plan v3.** Phase 0 (Trust Gate) ordering stands.

---

## 3. New findings (not in v3-plan)

### F1 — Two committed obfuscated binaries at repo root `[P0][S]`

`bd2.dll` and `bd_served.dll` are **tracked in git**, at the repository root, and are **byte-identical to each other**:

```
64,277 bytes each
sha256 fd7b9ff5ab1e2fac207c059028c82c4dae03d125194e33ca241855a069d1f65f   (both files)
```

- They are **not PE files** (no `MZ` header). They are **WebCIL containers** wrapping a .NET assembly — embedded strings include `webcil`, `webcilVersion`, `webcilSize`, `getWebcilPayload`, `v4.0.30319`, and Razor `BuildRenderTree` symbols.
- Added by commit `0b4d7c6` *"platform: replace Blazor.Diagrams submodule with Z.Blazor.Diagrams 3.0.4.1"* — i.e. leftovers of the **old obfuscated Blazor.Diagrams fork** that the NuGet migration removed.
- **Zero references** anywhere in `src/`, `.github/`, or `docs/` (the only grep hits were `bin/obj` noise and a coincidental checksum substring).
- `.gitignore` has **no rule for root-level `.dll`** (only `dlldata.c` at line 57).

Why it matters: an opaque, deliberately obfuscated blob in a public repo root is a supply-chain scanner magnet, it invites "what is this?" from every new contributor, and it keeps a question about the old fork's licensing open. AGENTS.md already states diagramming is NuGet-only with no submodule.

**Fix:** `git rm bd2.dll bd_served.dll`, add a root `/*.dll` ignore rule, note the removal in `CHANGELOG.md`. Verify with `git ls-files | grep -i '\.dll$'` returning nothing.

### F2 — Agent workspace scratch is committed `[S]`

**10 tracked files**: `.omo/run-continuation/ses_0dbd65604ffe8SHcgoBM9S6Jw2.json` and `.planning/codebase/{ARCHITECTURE,CONCERNS,CONVENTIONS,INTEGRATIONS,STACK,STRUCTURE,TESTING}.md`.

These are another tool's session state, not project knowledge — and they will silently rot against the real tree. `.gitignore` covers neither `.omo/` nor `.planning/`.

**Fix:** `git rm -r --cached .omo .planning` + ignore rules. Promote anything genuinely durable into `AGENTS.md` (which is already the project's curated equivalent) rather than keeping a second, unowned copy.

### F3 — Pointer-drag binds to a stale zone element `[bug][S]`

`wwwroot/js/ads.js` ships two stencil-drag paths with **different remount safety**:

- `AdsDropZone.init` (`ads.js:56-116`) is **correct**: it re-adopts the newest `DotNetObjectReference` on every init (`:63-65`, with a comment explaining that a remount disposes the previous ref) and stores its "already bound" flag **on the zone element** (`:66-69`), so a recreated element rebinds cleanly.
- `AdsStencilDrag.init` (`ads.js:124-130`) guards on **`window.__adsStencilPointerInit`** while closing over `zone` from the *first* call, and `inZone()` (`:134-137`) measures that captured node on every pointer move.

Consequence: if the `.ads-diagram-wrapper` element is ever recreated by Blazor while the `window` flag persists, pointer/touch drag keeps measuring a **detached** element, reports the pointer as always outside the zone, and **fails silently** — no error, just dropped drags. This is exactly the class of bug the sibling module was written to avoid.

`AdsShortcuts.init` (`:280-294`) is fine — it re-adopts `window.__adsShortcutRef` at `:282` *before* its init guard at `:283`.

**Fix:** make the pointer path match the native path — store the guard flag on the element (`zone.__adsPointerInit`) and re-resolve `document.querySelector(selector)` inside the handlers instead of closing over `zone`.

### F4 — Singleton command registry outlives its owner `[bug][S]`

- `Program.cs:72` → `builder.Services.AddSingleton<AzureDesignStudio.Services.AdsCommandService>();`
- `TopMenu.razor.cs:43` → `commandService.RegisterRange(commands)` where each `AdsCommand` carries a `Func<Task>` over a **TopMenu instance method** (`OpenSaveDrawer`, `OpenExportDrawer`, …), plus a feature-gated `scan` command at `:41` captured from `Services!`.
- **`TopMenu` implements no `Dispose`** (no `@implements IDisposable` in either the `.razor` or `.razor.cs`).

The singleton therefore retains closures over a component that can be disposed. In today's single-page WASM app the damage is mostly masked because a fresh TopMenu re-registers the same keys and overwrites them; the real exposures are (a) the **feature-gated `scan` entry**, which survives a host-feature change pointing at a dead component, and (b) any future route/auth-state change that disposes the layout. Invoking a stale command hits a disposed component and throws.

`AdsCommandService` also has no way to undo registration — the API is `RegisterRange` with no counterpart.

**Fix:** owner-scoped registration — `RegisterRange(object owner, IEnumerable<AdsCommand>)` + `RemoveOwner(object owner)`, called from `TopMenu.Dispose`. Alternatively make the service scoped and assert one owner per key.

### F5 — 119 unpushed commits and 53 uncommitted paths `[P0]`

Everything in `docs/v3-plan.md`, the entire Round-2 shell slice (§4), and the last 119 commits exist **only on this machine**. The slice is verified green *right now* (build + 755 tests), which makes this the cheapest possible moment to commit and push. Disk failure loses months.

**Fix:** commit the slice in reviewable chunks, push, and keep a `git bundle` as an out-of-band backup.

### F6 — The most-modified host has no gate `[P0][M]`

`AzureDesignStudio.Desktop` is modified in the working tree (`MauiProgram.cs`, `wwwroot/app.html`), is the largest host (~4,100 LOC plus the 2,481-line drawer), and appears in **no PR workflow**. There is currently **no automated way to know whether the working tree's Desktop code compiles at all** — see §6. This is v3 item N1; the working tree raises its urgency because the unverified delta is live, not hypothetical.

### F7 — The new shell has zero test coverage `[S]`

No file under `Core.Tests/` references `CommandPalette`, `DesignInspector`, `StatusBar`, `ThemeToggle`, or `AdsCommandService`. There is no bUnit project at all (v3 N4), and all 625 test attributes sit in one project.

Two of the five are pure logic that needs no UI framework to test: `AdsCommandService` (register/overwrite/order/lookup) and `CommandPalette.Filter` (`CommandPalette.razor:132-145`, substring match over label/description/group/key plus `active` index clamping). `LinkTypeClassifier` **does** have tests (`LinkTypeTests.cs`, 122 lines) — that is the pattern to follow.

---

## 4. Round-2 shell slice — quality assessment

Uncommitted inventory (`wc -l`):

| File | Lines | Notes |
|---|---|---|
| `wwwroot/css/ads-tokens.css` | 972 | design tokens |
| `Components/CommandPalette.razor` | 181 | Ctrl/Cmd+K palette |
| `Components/DesignInspector.razor` | 176 | inspector rail |
| `Components/StatusBar.razor` | 80 | live node/link/group/selection counts |
| `Components/ThemeToggle.razor` | 54 | light → dark → system |
| `Services/AdsCommandService.cs` | 40 | command registry |
| `Core/Common/LinkTypeClassifier.cs` | 69 | link semantics from endpoint types |
| `Core.Tests/LinkTypeTests.cs` / `SubnetPlacementTests.cs` | 122 / 91 | new, passing |
| `Core/Network/SubnetForm.razor` | 32 | subnet form |
| `wwwroot/js/ads.js` | +209 / −4 | drop zones, pointer drag, theme, shortcuts |

Wiring is complete and correct: `Index.razor:38` (DesignInspector), `MainLayout.razor:27` (ThemeToggle), `:35` (StatusBar), `:39` (CommandPalette).

**What is right, and worth calling out:**

- All three stateful components `@implements IDisposable` and **unsubscribe symmetrically** (`CommandPalette.razor:175-180`, `StatusBar.razor:36-42`, `DesignInspector.razor:121-124` subscriptions with matching teardown).
- `AdsDropZone` and `AdsShortcuts` both **re-adopt the newest `DotNetObjectReference`** and document why — the correct Blazor interop discipline.
- `ThemeToggle` catches `JSDisconnectedException` rather than swallowing all exceptions.
- `ads.js:97-101` keeps the **capture-phase** `dragover`/`dragenter` handling with a comment explaining the observed Chrome behaviour it works around. Comments record *why*, not *what*.
- `LinkTypeClassifier` regenerates labels from endpoints, so saved designs need no schema change — no migration risk.
- The deleted `wwwroot/assets/icons/others.svg` has **zero remaining references** (stencils JSON carries none).

Net: this slice reads like careful engineering. F3 and F4 are the two exceptions, and both are small.

---

## 5. Improvements

1. **Pin the SDK.** No `global.json` — the build floats on whatever SDK is installed (10.0.401 here). One file removes a class of "works on my machine" and makes a warnings-as-errors policy enforceable.
2. **CI, cheapest first.** (a) `windows-latest` job that builds Desktop + shared projects (N1); (b) `dotnet list package --vulnerable --include-transitive` as a hard gate — relevant *today* because of the accepted AutoMapper advisory; (c) collect the `coverlet` data the test project already references; (d) `dotnet format --verify-changed`.
3. **Sequence Trust Gate by size, not by section.** The six **S-sized** P0s — N11 identity helper, N12 local crypto provider, N14 limits/health, N26 payload cap, N10 guardrail scoping, and N17+N18 (migrations and WAL, landed together) — retire a disproportionate share of the risk before any L-sized item starts. N11 matters most immediately: `new Guid(claim)` on a missing claim is a 500 where a 401 is correct.
4. **Docs truth (N50).** Fix 8 → 11 in `README.md:48` and `AGENTS.md:9`; add the new `AdsCommandService` shell seam to AGENTS.md's WHERE TO LOOK; and **commit `docs/v3-plan.md`** — it is currently the only copy of the roadmap (F5).

---

## 6. Open verification

`dotnet build src/AzureDesignStudio.Desktop -c Release` was **still compiling after 11+ minutes** (started 09:32:49, still running at 700 s; MAUI workload restore dominates) on a warm local cache with the .NET 10.0.401 SDK. Its result is the direct evidence for F6: either the modified Desktop host compiles today (risk latent) or it does not (risk live). Record the outcome here when known:

```
Desktop Release build: ______________________  (date / warnings / errors / duration)
```

**Budget implication for N1:** if a warm local Release build exceeds 11 min, the v3-plan acceptance criterion of "job ≤ 20 min" is optimistic. Plan for MAUI-workload + NuGet caching as a prerequisite, or ship Desktop on `windows-latest` as a **non-blocking** check first (visible red, not merged red) and tighten it to blocking once the cached job proves a stable duration.

---

## 7. Feature proposals outside N1–N50

v3 is thorough, so these are deliberately additive — none duplicate an existing N item.

1. **Canvas architecture linter (best value/effort ratio).** `LinkTypeClassifier.cs` now names every connection from its endpoints — enough to run deterministic, **design-time** Well-Architected rules over the diagram: public IP wired straight to a VM, NSG drawn but not associated to a subnet, firewall with no route table, private endpoint with no DNS zone, hub-spoke with no peering. Output feeds the existing validation drawer **and a new read-only MCP tool**. This is *not* N33: no ARM calls, fully offline, unit-testable with today's fixtures, and it catches the mistakes users actually draw.
2. **Reference-architecture gallery.** Curated starter topologies (hub-spoke, AKS egress, landing-zone MVP) that drop onto the canvas as real typed nodes, reusing the stencil and adoption machinery. This is what earns the second hour of use, and it doubles as golden fixtures for item 1.
3. **MCP "propose" tier.** N41 deliberately keeps every MCP tool read-only. But ADS already owns the safest primitive in the product: **plan → approval → what-if gate**. Exposing write tools that can *only* create a plan pending human approval — never apply — turns ADS into an agent-operable execution surface with the guardrail already in place. Differentiator, not duplication.
4. **Accessibility gate inside the E2E suite.** When N2's Playwright harness exists, add axe-core plus a keyboard-only palette walkthrough. The palette is new, keyboard-driven, and has no focus-trap or ARIA test.
5. **Multi-design tabs.** Palette, inspector rail, and status bar are in place; tabs complete the shell and let users compare designs without losing canvas state.

---

## 8. Suggested order

| # | Action | Size | Why first |
|---|---|---|---|
| 1 | Commit the slice in reviewable chunks + push + `git bundle` | S | Everything below is at risk until this happens (F5) |
| 2 | F1 remove both DLLs, F2 untrack `.omo`/`.planning`, add ignore rules | S | Pure subtraction, zero design decisions |
| 3 | F3 zone-scoped pointer guard, F4 owner-scoped command registry | S | Two small lifetime bugs while the code is fresh |
| 4 | `global.json` + Windows CI job + vulnerable-package gate (N1) | S/M | Closes the "modified host nobody compiles" gap (F6) |
| 5 | Trust-Gate S-sized P0s: N11, N12, N14, N26, N10, N17+N18 | S each | Removes failure classes no feature work fixes |
| 6 | bUnit project + tests for the five shell components (N4/F7) | M | Protects the surface just built |
| 7 | Canvas architecture linter (proposal 1) | M | First net-new capability worth shipping |

Items 1–4 are roughly one day of work and retire every finding in §3.

---

## 9. Reproduction commands

```bash
cd C:/Users/jakub/Desktop/Projects/AzureDesignStudio

# Build (CI set) + tests
dotnet build src/AzureDesignStudio.Server   -c Release
dotnet build src/AzureDesignStudio          -c Release
dotnet build src/AzureDesignStudio.Mcp      -c Release
dotnet build src/AzureDesignStudio.Core.Tests -c Release
dotnet test  src/AzureDesignStudio.Core.Tests -c Release --no-build --filter "Category!=LiveAzure"
dotnet build src/AzureDesignStudio.Desktop  -c Release   # never run by CI today

# Findings
git ls-files | grep -iE '\.dll$'                       # F1
git ls-files | grep -E '^\.(omo|planning|swarm)'       # F2
sed -n '56,140p' src/AzureDesignStudio/wwwroot/js/ads.js  # F3
grep -n AdsCommandService src/AzureDesignStudio/Program.cs  # F4
git status -sb | head -1                                # F5
grep -rn Desktop .github/workflows/                     # F6 (no matches)
grep -cE '\[Fact\]|\[Theory\]' src/AzureDesignStudio.Core.Tests/*.cs  # F7
```

*End of assessment.*
