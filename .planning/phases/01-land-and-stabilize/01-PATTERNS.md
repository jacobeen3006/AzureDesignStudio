# Phase 01: Land and Stabilize - Pattern Map

**Mapped:** 2026-10-21
**Files analyzed:** 10 (8 files edited by the land + 2 historical docs under a Q2 recommendation)
**Analogs found:** 10 / 10 (every one is an existing, git-tracked file in this repo)

This phase creates no source files. It repairs one JS module, one ignore file, and six documents, and performs git-only operations (removals, restore, worktree proof, push, bundle). Each section below names the analog **inside this repo** and the exact lines to imitate. All analog paths were checked with `git ls-files` (all tracked); no `.gsd/capabilities/` mirror is referenced.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/AzureDesignStudio/wwwroot/js/ads.js` | module (JS interop global) | event-driven | `window.AdsDropZone`, same file lines 56–116 | exact (same file, same file's own precedent) |
| `.gitignore` | config (declarative rule list) | batch (path matching) | project-specific block, same file lines 353–361 | exact |
| `README.md` | docs (public index) | transform (prose + tree) | own Solution Structure block, lines 48–62 | exact (self-referential) |
| `AGENTS.md` | docs (agent knowledge base) | transform | own CONVENTIONS `Auth` bullet line 69 + own STRUCTURE tree lines 13–45 | exact |
| `.planning/codebase/CONCERNS.md` | docs (analysis record) | transform | own header/Scope block lines 1–9 | exact |
| `.planning/PROJECT.md` | planning doc (decision table) | transform | own Key Decisions table lines 76–83 | exact |
| `docs/entra-setup.md` | docs (numbered procedure) | request-response (step sequence) | own §4 lines 63–85 | exact |
| `docs/architecture.md` | docs (technical reference) | transform | own Solution Structure + Tech Stack tables lines 15–45 | exact |
| `docs/v1-plan.md` (banner only, Q2) | docs (dated historical record) | transform | `docs/azure-refresh-plan.md` lines 1–5 blockquote header | role-match |
| `docs/azure-refresh-plan.md` (banner only, Q2) | docs (dated historical record) | transform | `docs/v1-plan.md` lines 3–5 blockquote header | role-match |

---

## Pattern Assignments

### `src/AzureDesignStudio/wwwroot/js/ads.js` (module, event-driven)

**Scope:** `window.AdsStencilDrag` only (lines 124–228). `AdsDropZone`, `AdsTheme`, `AdsShortcuts` are read-only here (D-16).

**Analog:** `window.AdsDropZone` in the same file, **lines 56–116** — the only module in this file that is already element-scoped and remount-safe.

**Imports pattern:** none. This file is a classic script (`<script src="js/ads.js">`), no modules, no imports; every entry point is a `window.Ads*` global. Keep that shape — do not introduce `export`, `import`, or an IIFE.

**Core pattern to copy — adopt the ref *before* the guard, store the guard *on the element*** (lines 59–69):

```js
const zone = document.querySelector(selector);
if (!zone) {
    return;
}
// Always adopt the newest component reference: a remount disposes the
// previous DotNetObjectReference, but listeners live on the zone element.
zone.__adsRef = dotNetRef;
if (zone.__adsDrop) {
    return;
}
zone.__adsDrop = true;
```

Two load-bearing properties: (a) `__adsRef` is rewritten on every `init` call *before* the early return, so a recreated element always carries the current `DotNetObjectReference`; (b) the guard flag lives on the element, so a new element re-binds while the same element does not double-bind. Compare `DiagramPanel.razor.cs:47-49`, the only caller: `AdsDropZone.init(dropRef, ".ads-diagram-wrapper")` runs first, then `AdsStencilDrag.init(".ads-diagram-wrapper")` — meaning the live ref is already on the node when the pointer module needs it.

**Event-time ref read** (lines 107–114) — the drop listener never caches the ref:

```js
zone.addEventListener('drop', function (e) {
    e.preventDefault();
    e.stopPropagation();
    clearDropState();
    if (zone.__adsRef) {
        zone.__adsRef.invokeMethodAsync('HandleDropAtAsync', e.clientX, e.clientY, self.dragKey || '');
    }
}, true);
```

**Defect being replaced** — the captured node and the window guard (lines 125–137):

```js
init: function (selector) {
    const zone = document.querySelector(selector);
    if (!zone || window.__adsStencilPointerInit) {
        return;
    }
    window.__adsStencilPointerInit = true;
    ...
    const inZone = function (x, y) {
        const r = zone.getBoundingClientRect();   // measures the CAPTURED node
```

Every other `zone` use has the same problem: `clear` at 139–142, `pointermove` at 185 and 199, `finish` at 211–212 (`zone.__adsRef.invokeMethodAsync(...)`). A detached node's rect is all zeros, so the drop silently never fires.

**Deviations from the analog (intentional, D-13/D-16):**

1. **Document-level listeners stay.** `AdsDropZone` attaches element listeners for the drop itself (`zone.addEventListener('dragover' | 'dragenter' | 'dragleave' | 'drop', ..., true)`, lines 90–114) because every drop event originates *inside* the zone — but it already attaches `dragstart`/`dragend` on `document` (lines 78–85), so a document-level seam is an accepted idiom in this file. The stencil pointer drag starts on a tile in the selection rail, **outside** `.ads-diagram-wrapper`, so `AdsStencilDrag` must keep `document.addEventListener('pointerdown' | 'pointermove' | 'pointerup' | 'pointercancel', ..., true)`. Do not "simplify" it to match `AdsDropZone`'s element listeners; that would break the drag origin.
2. **Therefore re-resolution is per event, not per element.** Add `const liveZone = function () { return document.querySelector(selector); };` and read `__adsRef` off that live node inside `inZone`, `clear`, `pointermove`, and `finish`. The captured `selector` is a call-site constant (`".ads-diagram-wrapper"`, `DiagramPanel.razor.cs:49`) and there is exactly one caller, so `document.querySelector` at event time is correct, not expensive.
3. **Two guards, in this order.** `zone.__adsPointerInit` (element) must be set **before** the `window.__adsPointerDocInit` early-return; otherwise a second element is never marked and a third mount re-enters. Copy the drop-zone ordering discipline (mark the element first, then bail on the already-initialised shared resource).
4. **Absent zone at event time = no-op.** `inZone` returns `false` and `finish` skips invocation when `liveZone()` is null. This is the deliberate replacement for D-16's forbidden `.catch` wrapper on `invokeMethodAsync`.
5. **No lifecycle/dispose path.** JS interop is initiated only from `OnAfterRenderAsync` (`DiagramPanel.razor.cs:44-50`); `Dispose` at line 61 disposes the ref but never calls a JS teardown. Idempotence per element is the repo's substitute for lifecycle. Do not add `AdsStencilDrag.dispose`.

**Contrast — why `AdsTheme` / `AdsShortcuts` are NOT the model** (lines 278–294):

```js
window.AdsTheme.init();
...
window.AdsShortcuts = {
    init: function (ref) {
        window.__adsShortcutRef = ref;
        if (window.__adsShortcutsInit) return;
        window.__adsShortcutsInit = true;
        document.addEventListener('keydown', function (e) { ... if (window.__adsShortcutRef) { window.__adsShortcutRef.invokeMethodAsync('OpenFromShortcut'); } });
    }
};
```

`AdsShortcuts` is a **singleton by design** — one keyboard listener for the whole document, with the ref handed in as a global (`window.__adsShortcutRef`) because there is no element to scope it to. Its window guard is correct *for it*. `AdsStencilDrag` is bound to a DOM subtree that Blazor can recreate, so the same guard shape is a bug. Copy the *refhandling* idea (re-adopt the newest ref on each init) but never the window-scoped init flag.

**Comment style to copy:** the block comments above `AdsDropZone` (lines 51–55) and `AdsStencilDrag` (lines 118–123) explain *why the seam exists* and cite the upstream issue (`dotnet/maui#2205`). If the fix needs a comment, it should state why the guard is element-scoped, in that same two-to-five-line voice — not narrate the diff.

---

### `.gitignore` (config, declarative)

**Analog:** the project-specific tail of the same file, **lines 353–361** — the established place for repo-local rules (stock VisualStudio template above, project additions appended below).

```
/src/AzureDesignStudio.Server/appsettings.Development.json
/src/AzureDesignStudio.Server/appsettings.Production.json

# GSD graphify derived artifacts (regenerate with: graphify update .)
graphify-out/
.planning/graphs/graph.json
.planning/graphs/graph.html
.planning/graphs/.last-build-snapshot.json
```

**Pattern to copy:** a blank line, a `#` comment naming the group and its reason, then the rules, appended at **end of file** (newest group last). Rules that must only match the repo root are anchored with a leading `/` (lines 354–355 are the existing anchored precedent).

**Shape to add (D-07), appended after line 361:**

```
# Repository hygiene: root-level build leftovers and agent scratch (never tracked)
/*.dll
.omo/
.swarm/
.planning/.omp-ui-state.json
```

**Deviations (intentional):**

- `/*.dll` **root-anchored**, never bare `*.dll` — a bare rule would hide genuine publish/build output under `src/**` (research pitfall 8).
- `.omo/` and `.swarm/` are unanchored because the removal set is exactly the root directories; `.planning/.omp-ui-state.json` is a single file path, not a directory ignore (`.planning/` stays tracked — SC#1).
- Do **not** fold these into the VisualStudio template section above; the repo already separates project rules from template rules.
- Verified state at mapping time: `.gitignore` is 361 lines, none of the four rules exists, and `git check-ignore` reports nothing for `bd2.dll`, `.omo/run-continuation`, `.swarm/plan.md`, `.planning/.omp-ui-state.json`. `.planning/.omp-ui-state.json` exists on disk (66 bytes) and is untracked (`?? .planning/.omp-ui-state.json` under `--untracked-files=all`).

---

### `README.md` (docs, transform)

**Analog:** the same file's Solution Structure block, **lines 48–62**.

**Heading pattern (line 48):**

```markdown
## 🏗️ Solution Structure (8 projects)
```

**Tree pattern (lines 50–60):**

```
AzureDesignStudio.sln
├── AzureDesignStudio                  — Blazor WASM client (UI)
├── AzureDesignStudio.Server           — ASP.NET Core gRPC backend
├── AzureDesignStudio.Core             — Shared domain logic, diagram models, ARM generation
├── AzureDesignStudio.SharedModels     — Protobuf contracts (gRPC service definitions)
├── AzureDesignStudio.AzureResources   — CLS-compliant Azure resource type definitions
├── AzureDesignStudio.SourceGeneration — Roslyn source generator for DTO/mapping profiles
├── AzureDesignStudio.Core.Tests       — xUnit unit tests
└── AzureDesignStudio.Desktop          — .NET MAUI Windows app (native gRPC, MSAL broker auth, offline design cache)
```

**Pattern to copy:** exactly three additions — `Workspace`, `Discovery`, `Mcp` — in the order `AGENTS.md:13-45` already uses (`Core.Tests` → `Workspace` → `Discovery` → `Mcp` → `Desktop`). `Desktop` must keep the `└──` connector; the three inserted rows take `├──`. Pad each name so the `—` sits in the same column as line 57 (`AzureDesignStudio.SourceGeneration`), and keep the existing one-line role gloss style (four-to-ten words, no trailing period).

**Count:** `8 projects` → `11 projects` (heading line 48). 11 is the verified `.csproj` count under `src/`.

**Deviations (intentional):**

- Do **not** re-sort the existing eight rows; the list is role-ordered, not alphabetical (`SharedModels` before `AzureResources`).
- Do **not** copy the nested `AGENTS.md` tree (lines 13–45) here — README's tree is flat solution-entry format; the AGENTS tree is a directory layout with inline comments. Only the *order and naming* transfer.
- Leave line 62 (`**Target**: .NET 10 …`) untouched — already correct.
- The docs index at lines 274–285 already links `docs/architecture.md`, `docs/entra-setup.md`, and `docs/v3-plan.md`; no link edits are needed for this phase.

---

### `AGENTS.md` (docs, transform)

**Analog A (wording source):** `AGENTS.md:69`, the already-correct `Auth` bullet:

```markdown
- **Auth**: Entra ID work+personal (not B2C) - MSAL redirect in WASM, native MSAL (WAM broker + device-code fallback) in Desktop, JWT + Microsoft.Identity.Web in Server; shared drawers use the `AdsHostAuth` host-override seam
```

**Analog B (structure source):** `AGENTS.md:13-45`, the STRUCTURE tree that already lists all 11 projects including `Workspace`, `Discovery`, `Mcp`, `Desktop` — proof the file is internally inconsistent only in its overview sentence.

**Target — line 9, current text:**

```markdown
Azure Design Studio — Blazor app for visual Azure architecture design, hosted as WASM (AAD B2C) and MAUI Blazor Hybrid desktop (Windows). Diagramming, validation, ARM/Bicep IaC generation, export. .NET 10, 8 projects in src/. Microsoft Global Hackathon 2022 winner.
```

**Pattern to copy:** keep the single-paragraph overview shape and sentence order; change exactly two clauses — `(AAD B2C)` → `(Entra ID work + personal accounts)` and `8 projects in src/` → `11 projects in src/`. Reuse line 69's phrase "work+personal (not B2C)" rather than inventing new auth prose. Example:

```markdown
Azure Design Studio — Blazor app for visual Azure architecture design, hosted as WASM (Entra ID work+personal accounts) and MAUI Blazor Hybrid desktop (Windows). Diagramming, validation, ARM/Bicep IaC generation, export. .NET 10, 11 projects in src/. Microsoft Global Hackathon 2022 winner.
```

**Deviations (intentional):**

- Do **not** touch lines 13–45 (already correct) or line 69 (the authority).
- Do **not** mention "B2C" anywhere in the replacement, not even as "not B2C" — SC#5's grep is `grep -rn "B2C" AGENTS.md` → 0 hits.
- Preserve the em dash in the first sentence and the "Microsoft Global Hackathon 2022 winner." tail.

---

### `.planning/codebase/CONCERNS.md` (docs, analysis record)

**Analog:** its own header block, **lines 1–9**:

```markdown
---
last_mapped_commit: a05ef8c00d2ff2dc2f0d9bea0a220051852405a8
last_mapped_at: 2026-10-21
---
# Codebase Concerns

**Analysis Date:** 2026-10-21

Scope: full repository (`src/` — 8 projects, ~64k lines of C#/Razor across 760 source files). Severity is rated **High** (will cause data loss / security exposure / broken production behavior), **Medium** (degrades reliability or developer velocity; will bite during the next feature), **Low** (hygiene).
```

**Pattern to copy:** a one-token edit inside line 9 — `8 projects` → `11 projects`. Leave the `last_mapped_commit` front matter, the `Analysis Date`, the line-count figure and the whole severity legend intact; this document is a point-in-time analysis and only its factual count is wrong.

**Deviation (intentional):** do not bump `last_mapped_commit` / `last_mapped_at` — the codebase map refresh is not part of this phase, and altering the stamp would misrepresent when the map was produced. If the planner wants a note, put it in the phase summary, not in the front matter.

---

### `.planning/PROJECT.md` (planning doc, decision table)

**Analog:** the Key Decisions table, **lines 76–83**:

```markdown
## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Sequence Trust Gate before new UI surfaces | Security, delivery, and tenancy gaps invalidate feature work; docs/v3-plan.md Phase 0 is the gate | — Pending |
| Signed MSIX as the milestone artifact | Pilot users need a verifiable install; unsigned packaging defers trust questions | — Pending |
| Keep v3 non-goals excluded | Multi-cloud, CRDT, custom policy engine, and WASM scans would each fork the architecture | — Pending |
| Land the existing uncommitted slice before new work | 120 commits and 54 paths exist only on this machine; the tree is green now | — Pending |
```

**Pattern to copy:** three cells, no backticks inside the first cell except code identifiers, rationale states *why the choice is durable* (not what was done), outcome cell starts with `—` when undecided. One new row appended after line 83 (research §SC#5 step 6 gives the wording):

```markdown
| Publisher legal name and signing route: `CN=Jakub Talaj` (individual validation) via Azure Artifact Signing | No private-key custody; matches the Phase 11 `azure/trusted-signing-action@v0` path; identity validation has external lead time | Decided 2026-10-21 — manifest update deferred to Phase 11 (D-21) |
```

**Deviations (intentional):**

- The `CN=Jakub Talaj` string must be **byte-identical** to the future certificate subject; Phase 11 asserts equality and a mismatch is the `0x8007000B` install failure (D-20). Do not add spaces or quotes.
- The outcome cell does **not** use `— Pending`; the decision is made, only its *implementation* is deferred. Keep the "Decided <date> — deferred to Phase 11 (D-21)" shape.
- Two neighbouring bullets in the same file become false once the docs commit lands and need a planner decision in the same commit: line 62 `- **Docs drift:** README/AGENTS still say 8 projects … AAD B2C` and line 59's path counts (`35 modified, 1 deleted, and 18 untracked paths`). Leaving them is the same class of lie this phase exists to remove; updating them is inside the file the phase already edits, so no scope expansion is implied.
- Do not reorder or rewrite the four existing rows.

---

### `docs/entra-setup.md` (docs, numbered procedure)

**Analog:** its own §4, **lines 63–85** — numbered `## N.` sections, PowerShell fence, then a plain prose paragraph naming the manifest and workflow files:

````markdown
## 4. Build & package the desktop app

Prereq: .NET 10 SDK with `dotnet workload install maui-windows`.

```powershell
...
dotnet publish src/AzureDesignStudio.Desktop -c Release -f net10.0-windows10.0.19041.0 `
  -p:WindowsPackageType=MSIX -p:RuntimeIdentifier=win-x64 -p:WindowsAppSDKSelfContained=true
```

The MSIX lands under `bin\Release\...\Publish\win-x64\` (`.msix` / `.appx`,
plus an install PowerShell script). The manifest
(`Platforms/Windows/Package.appxmanifest`) uses
`Publisher="CN=Azure Design Studio"`, so a locally built package must be
**signed** (and the cert trusted) before Windows will install it — see
`.github/workflows/publish.yml` (`MSIX_PFX_BASE64` / `MSIX_PFX_PASSWORD`
secrets sign automatically; without them the artifact is uploaded unsigned).
For Store distribution change Identity/Publisher to your Store identity via
Partner Center packaging.
````

**Pattern to copy:** append one paragraph after line 85, before `## 5. Offline behaviour (desktop)`, in the same register — sentence first, concrete file path in backticks, no heading of its own, ~120-word hard wrap already used by the file (~78 columns). Content per D-18/D-19/D-20/D-21:

- route: **Azure Artifact Signing** (formerly Trusted Signing);
- validation type: **individual**;
- certificate subject / future `Publisher`: `CN=Jakub Talaj`;
- `PublisherDisplayName` may remain "Azure Design Studio";
- the manifest still carries the placeholder `CN=Azure Design Studio` until Phase 11, which asserts subject equality.

**Deviations (intentional):**

- The existing `MSIX_PFX_BASE64` / `MSIX_PFX_PASSWORD` sentence must **stay** and stay in present tense — it describes today's `publish.yml` (`docs/entra-setup.md:82`, `.github/workflows/publish.yml:5-6,87-95`). The new paragraph is the *target* state; do not silently rewrite the current one.
- Do not edit `Package.appxmanifest` (D-21) and do not add an `azure/trusted-signing-action` YAML snippet here — Phase 11 owns the workflow change.
- Keep the file's `## N.` numbering untouched; the new text is a paragraph inside §4, not a new §5.

---

### `docs/architecture.md` (docs, technical reference)

**Analog:** its own Solution Structure + Tech Stack tables, **lines 15–45**:

```markdown
## Solution Structure (7 projects)
...
**Target**: .NET 7.0, Blazor WebAssembly
...
| **UI framework** | Blazor WebAssembly + .NET 7 |
| **UI components** | Ant Design Blazor 0.14.4 |
| **Diagramming** | Z.Blazor.Diagrams 3.0.4.1 (NuGet) |
| **Auth** | Azure AD B2C (MSAL via `Microsoft.Authentication.WebAssembly.Msal`) |
| **Backend API** | ASP.NET Core gRPC with gRPC-Web (browser-compatible) |
| **ORM** | Entity Framework Core (InMemory dev / SQL Server prod) |
```

**Pattern to copy:** this document is *plain* markdown — no emoji headings, no bolded tree comments (unlike README). Keep that register. The tree uses the same box characters and `—` gloss as README lines 50–60; reuse that formatting, updating the entries in place.

**Lines to correct (verified this session):**

| Line | Current | Target |
|------|---------|--------|
| 15 | `## Solution Structure (7 projects)` | `## Solution Structure (11 projects)` |
| 17–26 | tree: sln + 7 entries, **no `Desktop`** | sln + 11 entries; add `Workspace`, `Discovery`, `Mcp`, **and** `Desktop` |
| 28 | `**Target**: .NET 7.0, Blazor WebAssembly` | `.NET 10` (README:62 wording) |
| 36 | `Blazor WebAssembly + .NET 7` | `Blazor WebAssembly + .NET 10` |
| 37 | `Ant Design Blazor 0.14.4` | `Ant Design Blazor 1.6.2` (matches README Tech Stack) |
| 39 | `| **Auth** | Azure AD B2C (MSAL via …) |` | Entra ID work+personal, MSAL — copy `AGENTS.md:69` semantics |
| 41 | `Entity Framework Core (InMemory dev / SQL Server prod)` | verify against `PROJECT.md`/workspace reality before editing (research flags it as "check before rewriting") |
| 87 | `│  Auth: MSAL → Azure AD B2C                           │` | inside an ASCII box — replace text with `Entra ID` and **re-pad to the box width**, do not change the border |
| 101 | `│  Auth: JWT Bearer + AzureAdB2C                       │` | `Entra ID` (same padding rule) |
| 221 | `- **Azure AD B2C** with `Microsoft.Identity.Web`` | `- **Microsoft Entra ID** with `Microsoft.Identity.Web`` |
| 230 | `Entity Framework Core with InMemory DB (dev) or SQL Server (prod)` | only if line 41 is corrected; keep the two in agreement |

**Deviations (intentional):**

- `7 projects` → `11`, not `8` → `11`: this file is *further* behind than README (its tree predates `Desktop`). Four entries added, not three.
- `.NET 7.0` and `AntDesign 0.14.4` are stale but in scope under D-17's "stack" line; the phase's own acceptance greps only cover count + auth, so treat the stack rows as required-by-D-17 prose fixes.
- ASCII diagrams (87, 101) must stay visually aligned; the file is read as raw text, so trailing spaces matter.
- Do **not** rewrite the whole document; only these statements are false. Sections that are still accurate stay byte-identical.

---

### `docs/v1-plan.md` and `docs/azure-refresh-plan.md` (docs, dated historical records)

**Analog:** the blockquote metadata header both files already use — `docs/azure-refresh-plan.md:1-5`:

```markdown
# Azure Refresh Plan — Modernization Assessment

> **Date:** 2026-06-27
> **Scope:** Upgrade AzureDesignStudio from .NET 7 + deprecated Azure SDK to current Azure-compatible stack
```

and `docs/v1-plan.md:1-5`:

```markdown
# AzureDesignStudio v1 Plan

> **Date:** 2026-10-11
> **From:** v0.4 (post platform-refresh, .NET 8) → **v1.0**
> **Themes (phased):** Platform '26 → Catalog + Cost → Editor UX → Desktop → Release
```

**Recommendation (Q2) — banner, do not rewrite history.** `docs/azure-refresh-plan.md:14` ("All 8 projects must retarget", written 2026-06-27) and `docs/v1-plan.md:103` ("README: 8 projects", a **"Done 2026-10-13"** changelog entry) were true when written. Editing the numbers would falsify a record; leaving them unmarked keeps them looking current. Both files already carry a dated blockquote header, so the compliant minimum is one additional blockquote line in that same list — for example:

```markdown
> **Status:** historical — supersedes nothing; current structure/auth truths live in README, AGENTS.md and docs/architecture.md.
```

**Deviations (intentional):**

- Use the files' own `> **Key:** value` metadata idiom; do not introduce an HTML comment or a new heading.
- Do not touch `docs/azure-refresh-plan.md:115,189` (B2C config specifics) or `docs/v1-plan.md:20` (B2C retirement facts) — they are historical statements of fact, not current-truth claims.
- If the planner reads D-17 as "rewrite the counts", stop and pick the banner: rewriting a "Done <date>" entry is the one edit that makes the tree *less* honest.
- `docs/v3-plan.md:337` and `docs/assessment-2026-10-21.md:18` quote the stale counts as evidence of drift; they are the reference record for this work and must not be edited (they are new/untracked docs riding the docs commit as-is).

---

## Shared Patterns

### Counts are measured at write time, never copied
**Source:** the invariant behind `README.md:48`, `AGENTS.md:9`, `.planning/codebase/CONCERNS.md:9`, `docs/architecture.md:15`, and `.planning/PROJECT.md:67`
**Apply to:** every "N projects" sentence, every commit message, the phase summary

```bash
git ls-files 'src/**/*.csproj' | wc -l     # → 11 today
git rev-list --count origin/main..HEAD      # → re-measure; CONTEXT's 126 is stale
```

The four docs must agree on the same number after the commit; `grep -rn "8 projects\|7 projects" README.md AGENTS.md docs/architecture.md .planning/codebase/CONCERNS.md` → 0 hits is the audit shape.

### Auth wording has exactly one source
**Source:** `AGENTS.md:69` (`Entra ID work+personal (not B2C)`) and `.planning/PROJECT.md:70` (`Entra ID work+personal accounts (not B2C)`)
**Apply to:** `AGENTS.md:9`, `docs/architecture.md:39,87,101,221`

Do not draft new auth prose per file. Reuse the phrase "Entra ID work+personal"; in the ASCII diagrams abbreviate to `Entra ID` and re-pad the box.

### Markdown table / tree formatting
**Source:** `.planning/PROJECT.md:78-83` (three-cell table), `README.md:50-60` (box tree), `docs/architecture.md:33-45` (two-cell stack table)
**Apply to:** PROJECT.md's new row, README and architecture trees

Pipes aligned by hand, separator row exactly `|---|---|` (`PROJECT.md:79`), em dash `—` as the gloss separator in trees, `├──` for all-but-last entries and `└──` for the last.

### Dated-document metadata blockquote
**Source:** `docs/v1-plan.md:3-5`, `docs/azure-refresh-plan.md:3-4`
**Apply to:** the two historical plan docs (banner line only)

### Commit subjects
**Source:** `git log --oneline` — existing history (`docs: create roadmap (11 phases)`, `chore: enable graphify and build knowledge graph`) plus GSD's scoped forms (`docs(01): capture phase context`, `chore(phase-1): untrack omp ui state`)
**Apply to:** the three land commits

Bare conventional type for repo-shaped work, scoped form when the commit belongs to a phase. Do not invent a new prefix (`land:`); `Claude's Discretion` covers the choice but the observed vocabulary is `docs`/`chore`/`feat`/`fix`.

### Whole-file-diff guard (line endings)
**Source:** `core.autocrlf=true` (verified in research); no `.gitattributes` normalisation in play
**Apply to:** all eight edited files

After editing, `git diff --stat <path>` must show a handful of changed lines, not the whole file. A whole-file diff is a CRLF artefact — restore and re-edit, never commit it.

### Verification greps (SC#5 acceptance shape)
```bash
grep -c "8 project\|7 project" README.md AGENTS.md docs/architecture.md .planning/codebase/CONCERNS.md   # → 0
grep -n "11 project" README.md AGENTS.md                                                                 # → present
grep -rn "B2C" AGENTS.md docs/architecture.md                                                            # → 0
grep -n "CN=Jakub Talaj" .planning/PROJECT.md docs/entra-setup.md                                        # → 2 hits
grep -n "Artifact Signing" .planning/PROJECT.md docs/entra-setup.md                                      # → 2 hits
```

---

## No Analog Found

| Item | Role | Data Flow | Reason |
|------|------|-----------|--------|
| Throwaway pointer harness page (D-14 part 2, D-15) | test harness | event-driven | Repo has **no** JS test toolchain (no node, no Jest/Vitest, no Playwright until Phase 8). It is explicitly uncommitted; model it on the research's inline snippet, not on any repo file. Do not add a test project or framework for it. |
| Git operations: `git rm`, `git checkout HEAD -- others.svg`, `git worktree add/remove --force`, `git bundle create`, `git ls-remote` | ops | batch | No repo-internal analog exists (no scripts under `scripts/`, `.github/workflows/` builds only). Use the literal commands from CONTEXT D-02/D-04/D-05/D-11/D-12; they are the specification. |
| `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest` | config | — | **Deliberately not edited** (D-21). Current values: line 9 `Publisher="CN=Azure Design Studio"`, line 15 `PublisherDisplayName>Azure Design Studio`. Phase 11 owns the change. Do not "helpfully" align it with the new subject. |
| `.github/workflows/publish.yml` | CI config | — | **Deliberately not edited.** Lines 5–6 and 87–95 describe the current PFX signing path; Phase 11 replaces it with `azure/trusted-signing-action@v0`. |

## Deliberately Untouched (same commit, no diff)

- `src/AzureDesignStudio/wwwroot/assets/icons/others.svg` — restored via `git checkout HEAD -- …` **before** staging the shell commit (D-04). The restoration produces no diff; a `D` entry in `git status` after staging means the restore was missed or the deletion got staged.
- `src/AzureDesignStudio.Core.Tests/StencilTests.cs` — already modified; travels in the **shell** commit (D-03), not docs.
- `src/AzureDesignStudio/wwwroot/ads-stencils.json`, the new SVG assets, `ads-tokens.css`, the four new components, `AdsCommandService.cs` — land as-is in the shell commit.

## Metadata

**Analog search scope:** repo root, `src/AzureDesignStudio/**`, `docs/`, `.planning/**` (all git-tracked; verified with `git ls-files`)
**Files scanned:** 14 read in full or by targeted range (`ads.js`, `.gitignore`, `README.md`, `AGENTS.md`, `CONCERNS.md`, `PROJECT.md`, `CONVENTIONS.md`, `docs/entra-setup.md`, `docs/architecture.md`, `docs/v1-plan.md`, `docs/azure-refresh-plan.md`, `DiagramPanel.razor.cs`, plus the two phase inputs)
**Pattern extraction date:** 2026-10-21
