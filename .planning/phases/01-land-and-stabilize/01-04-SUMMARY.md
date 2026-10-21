---
phase: 01-land-and-stabilize
plan: 04
subsystem: docs
tags: [docs, entra-id, publisher, azure-artifact-signing, sqlite-workspace, ant-design-blazor, ascii-diagrams]

# Dependency graph
requires:
  - phase: 01-03
    provides: the committed core and shell slices this docs reconcile describes
provides:
  - Measured project count (11) and Entra ID work+personal auth wording in README, AGENTS, CONCERNS, architecture
  - Reconciled docs/architecture.md stack facts (.NET 10, Ant Design Blazor 1.6.2, SQLite workspace store)
  - Historical banners on the two dated plan records (docs/v1-plan.md, docs/azure-refresh-plan.md)
  - Publisher decision (`CN=Jakub Talaj`, Azure Artifact Signing) recorded in .planning/PROJECT.md and mirrored in docs/entra-setup.md
affects: [11-signed-msix-and-packaged-install]

actuals:
  tokens: 3649        # chars/4 over `git diff HEAD` for the eight edited files (14596 chars)
  tasks: 3
  commits: 0          # nothing committed by this plan; plan 01-05 owns the docs land commit

tech-stack:
  added: []
  patterns:
    - "Counts measured at write time via `git ls-files 'src/**/*.csproj'`, never copied from planning prose"
    - "Dated records are bannered as historical, not rewritten (D-17 reading)"

key-files:
  created: []
  modified:
    - README.md
    - AGENTS.md
    - .planning/codebase/CONCERNS.md
    - docs/architecture.md
    - docs/v1-plan.md
    - docs/azure-refresh-plan.md
    - .planning/PROJECT.md
    - docs/entra-setup.md

key-decisions:
  - "D-17 applied as: correct every current-truth statement, banner the two dated records — rewriting a 'Done 2026-10-13' entry or a 2026-06-27 plan would falsify a record"
  - "AGENTS.md line 69 lost only its '(not B2C)' parenthetical; the auth model stays stated in full so SC#5's file-wide grep is clean"
  - "The publisher row states the decision and defers the manifest edit to Phase 11 (D-21), so PROJECT.md does not imply the manifest already matches"

patterns-established:
  - "Bounded docs diff: locate statements by content, edit the single line, verify `git diff --stat` stays small"
  - "ASCII-box edits preserve total line length by absorbing the text delta in the trailing spaces before the border"

requirements-completed: []
---

# Phase 01 Plan 04: Docs Truth and Publisher Decision Summary

**README, AGENTS, CONCERNS and architecture now report the measured 11 projects and Entra ID work+personal auth; architecture's stack and four missing project rows match the repo; the two dated plans carry a historical banner; and `CN=Jakub Talaj` via Azure Artifact Signing is recorded identically in PROJECT.md and entra-setup.md.**

## Performance

- **Duration:** 2 min measured edit window (12:11:15 → 12:12:53 local); analysis time before the first edit was not instrumented
- **Started:** 2026-10-21T10:11:15Z (first file edit)
- **Completed:** 2026-10-21T10:12:53Z
- **Tasks:** 3
- **Files modified:** 8

## Accomplishments

- All four "current truth" docs agree on the measured 11-project count and Entra ID work+personal auth; `B2C` is gone from AGENTS.md and docs/architecture.md.
- `docs/architecture.md` gained the four missing tree rows (`Workspace`, `Discovery`, `Mcp`, `Desktop`), `.NET 10`, Ant Design Blazor 1.6.2, and the SQLite workspace store, while both ASCII boxes kept byte-identical line lengths.
- The two dated plan records are marked historical with exactly one added line each and zero deletions — no dated entry rewritten.
- The publisher identity and signing route are recorded once in each authority, with the manifest and `publish.yml` proven untouched.

## Task Commits

Nothing is committed by this plan. Per the plan's `<output>` and the dispatch boundary, plan 01-05 owns the docs land commit for these eight paths. `git status --porcelain` at hand-off shows the eight files modified and no other change from this plan.

## Files Created/Modified

- `README.md` — Solution Structure heading count 8 → 11; three tree rows (`Workspace`, `Discovery`, `Mcp`) inserted before `Desktop`, which keeps `└──`.
- `AGENTS.md` — OVERVIEW clause `(AAD B2C)` → `(Entra ID work+personal accounts)` and `8 projects in src/` → `11 projects in src/`; line 69's obsolete `(not B2C)` parenthetical removed.
- `.planning/codebase/CONCERNS.md` — scope line `src/ — 8 projects` → `11 projects`; front matter, analysis date, line-count figure and severity legend untouched.
- `docs/architecture.md` — heading 7 → 11; tree gained `Workspace`, `Discovery`, `Mcp`, `Desktop`; `**Target**` and UI framework row → .NET 10; UI components row → Ant Design Blazor 1.6.2; auth row and prose auth site → Microsoft Entra ID work+personal; ORM row and prose counterpart → SQLite workspace store / InMemory dev / SQL Server prod; two ASCII-box `Auth:` lines → `Entra ID` re-padded.
- `docs/v1-plan.md` — one `> **Status:** historical record …` blockquote line after `> **Themes (phased):**`.
- `docs/azure-refresh-plan.md` — the same banner line after `> **Scope:**`.
- `.planning/PROJECT.md` — Key Decisions row for the publisher decision; the two falsified Context bullets rewritten to past tense.
- `docs/entra-setup.md` — one target-state paragraph at the end of §4 (route, individual validation, subject, `PublisherDisplayName`, placeholder caveat, `0x8007000B`).

## Decisions Made

- **D-17 reading (explicit):** statements asserted as current truth were corrected; the two dated plan records got a banner instead of rewritten numbers, because editing a `Done 2026-10-13` entry or a 2026-06-27 assessment would convert history into a false present-tense claim. Follows `01-PATTERNS.md` Q2.
- **AGENTS.md line 69:** the wording authority lost only the `(not B2C)` contrast parenthetical, so the phase's file-wide `B2C` gate is satisfiable while the auth model remains fully stated (twice in the file).
- **Publisher wording:** the decision is recorded as decided with implementation deferred to Phase 11, matching D-21; the subject literal is byte-identical in both authorities (`CN=Jakub Talaj`, no quotes, no extra spaces).

## Evidence

### 1. Measured project count

Command:

```text
$ git ls-files 'src/**/*.csproj' | wc -l
11
```

The eleven paths:

```text
src/AzureDesignStudio.AzureResources/AzureDesignStudio.AzureResources.csproj
src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj
src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj
src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj
src/AzureDesignStudio.Discovery/AzureDesignStudio.Discovery.csproj
src/AzureDesignStudio.Mcp/AzureDesignStudio.Mcp.csproj
src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj
src/AzureDesignStudio.SharedModels/AzureDesignStudio.SharedModels.csproj
src/AzureDesignStudio.SourceGeneration/AzureDesignStudio.SourceGeneration.csproj
src/AzureDesignStudio.Workspace/AzureDesignStudio.Workspace.csproj
src/AzureDesignStudio/AzureDesignStudio.csproj
```

Each file showing the measured count:

```text
$ grep -c '11 project' README.md AGENTS.md .planning/codebase/CONCERNS.md
README.md:1
AGENTS.md:1
.planning/codebase/CONCERNS.md:1
$ grep -c '11 project' docs/architecture.md
1
```

README tree rows now read (`└──` stays on Desktop; em dash column verified at position 40 for every row):

```text
├── AzureDesignStudio.Core.Tests       — xUnit unit tests
├── AzureDesignStudio.Workspace        — SQLite workspace store for designs, scans, plans and audit
├── AzureDesignStudio.Discovery        — Tenant analysis engines (scan, drift, guardrails, decommission)
├── AzureDesignStudio.Mcp              — Stdio MCP server with its read-only workspace tools
└── AzureDesignStudio.Desktop          — .NET MAUI Windows app (native gRPC, MSAL broker auth, offline design cache)
```

### 2. Bounded diffs and preserved line endings

```text
$ git diff --stat HEAD -- README.md AGENTS.md .planning/codebase/CONCERNS.md docs/architecture.md docs/v1-plan.md docs/azure-refresh-plan.md .planning/PROJECT.md docs/entra-setup.md
 .planning/PROJECT.md           |  5 +++--
 .planning/codebase/CONCERNS.md |  2 +-
 AGENTS.md                      |  4 ++--
 README.md                      |  9 ++++++++-
 docs/architecture.md           | 28 ++++++++++++++++------------
 docs/azure-refresh-plan.md     |  1 +
 docs/entra-setup.md            |  9 +++++++++
 docs/v1-plan.md                |  1 +
 8 files changed, 41 insertions(+), 18 deletions(-)
```

README's stat includes four insertions that were already in the working tree before this plan (the docs index block: hunks `@@ -280,0 +284,3 @@` and `@@ -281,0 +288 @@`). This plan's own README hunks are `@@ -48 +48 @@` (heading) and `@@ -58,0 +59,3 @@` (three tree rows); the docs index block was not touched.

Line-ending style, measured per file after editing (`grep -c $'\r$'` vs `wc -l`, so CRLF count == total line count means no mixed endings were introduced). `core.autocrlf=true` in this checkout, and all eight files were CRLF-terminated before and after:

```text
README.md CRLF=312 total=312
AGENTS.md CRLF=102 total=102
.planning/codebase/CONCERNS.md CRLF=376 total=376
docs/architecture.md CRLF=324 total=324
docs/v1-plan.md CRLF=133 total=133
docs/azure-refresh-plan.md CRLF=240 total=240
.planning/PROJECT.md CRLF=104 total=104
docs/entra-setup.md CRLF=102 total=102
```

### 3. ASCII-box proof (docs/architecture.md)

Line count containing `Auth:`: 2 at HEAD, 2 now. Byte lengths of those lines, HEAD vs now — identical in order:

```text
HEAD bytes/line
(62, '│  Auth: MSAL → Azure AD B2C                           │')
(60, '│  Auth: JWT Bearer + AzureAdB2C                       │')
NOW bytes/line
(62, '│  Auth: MSAL → Entra ID                               │')
(60, '│  Auth: JWT Bearer + Entra ID                         │')
```

The identity text shrank by 4 and 2 characters; the same number of spaces was added before the closing border, so both right borders stay in their original column. `python -c "... o==n ..."` printed `old [56, 56] new [56, 56]` (character lengths) and the plan's automated check returned `TASK2 VERIFY PASS`.

Both ASCII boxes were checked against their neighbouring rows visually: `│  Auth: …` remains aligned with the other `│  <label>: …` lines in the same box.

### 4. Retired terms and stack facts

```text
$ grep -ci 'B2C' AGENTS.md          → 0
$ grep -ci 'B2C' docs/architecture.md → 0
$ grep -c '\.NET 7' docs/architecture.md → 0
$ grep -c 'Ant Design Blazor 1.6.2' docs/architecture.md → 1
$ grep -c 'SQLite workspace' docs/architecture.md → 3
```

Ant Design Blazor version observed in the repository (not assumed):

```text
$ grep -n 'AntDesign' src/AzureDesignStudio/AzureDesignStudio.csproj
24:    <PackageReference Include="AntDesign" Version="1.6.2" />
```

ORM facts observed before writing them:

```text
src\AzureDesignStudio.Workspace\WorkspaceDbContext.cs:169:            .UseSqlite(ConnectionString)
src\AzureDesignStudio.Mcp\AdsWorkspace.cs:31:            .UseSqlite(_connectionString)
src\AzureDesignStudio.Server\Program.cs:31:...UseInMemoryDatabase("adsdb-inmem")
src\AzureDesignStudio.Server\Program.cs:43:...UseSqlServer(builder.Configuration["ads-main"])
```

### 5. Publisher pair

```text
$ grep -c 'CN=Jakub Talaj' .planning/PROJECT.md docs/entra-setup.md
.planning/PROJECT.md:1
docs/entra-setup.md:1
$ grep -c 'Azure Artifact Signing' .planning/PROJECT.md docs/entra-setup.md
.planning/PROJECT.md:1
docs/entra-setup.md:1
$ grep -o 'CN=[A-Za-z ]*' .planning/PROJECT.md | sort -u
CN=Jakub Talaj
```

`CN=` audit in PROJECT.md yields exactly one distinct value. The Key Decisions row exists once:

```text
| Publisher legal name and signing route: `CN=Jakub Talaj` (individual validation) via Azure Artifact Signing | No private-key custody; matches the Phase 11 `azure/trusted-signing-action@v0` path; identity validation has external lead time | Decided 2026-10-21 — manifest update deferred to Phase 11 (D-21) |
```

Manifest and workflow untouched:

```text
$ git diff --quiet HEAD -- src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest
manifest diff exit=0
$ git diff --quiet HEAD -- .github/workflows/publish.yml
publish diff exit=0
```

(The dispatch acceptance text spelled the manifest path with a duplicated `Platforms/Windows` segment; the canonical path was used for the proof above and is the one Phase 11 will assert against.)

### 6. Banner diffs for the dated plans

```text
$ git diff --numstat HEAD -- docs/v1-plan.md docs/azure-refresh-plan.md
1	0	docs/v1-plan.md
1	0	docs/azure-refresh-plan.md
```

One added line, zero deleted lines in each; no other line in either file changed.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Stale `AntDesign` version in the Key NuGet Packages table**
- **Found during:** Task 2 (architecture reconcile)
- **Issue:** The corrected `**UI components**` row now says `Ant Design Blazor 1.6.2`, but the same document's `### Key NuGet Packages` table still said `| \`AntDesign\` 0.14.4 |`, leaving the file self-contradictory about one named fact (T-01-15, documented stack facts).
- **Fix:** Updated that row to `| \`AntDesign\` 1.6.2 |`, grounded in `src/AzureDesignStudio/AzureDesignStudio.csproj:24`.
- **Files modified:** docs/architecture.md
- **Verification:** `git diff HEAD --unified=0 -- docs/architecture.md` shows one extra single-line hunk; `grep -c 'Ant Design Blazor 1.6.2'` = 1.
- **Committed in:** nothing — plan 01-05 commits these paths.

**2. [Rule 1 - Bug] ASCII box line came out one character short on the first attempt**
- **Found during:** Task 2, first `Auth:` re-pad
- **Issue:** `Azure AD B2C` (12 characters) is four characters longer than `Entra ID` (8), so the three spaces added in the first pass left the first box line at 55 bytes instead of HEAD's 62.
- **Fix:** Added the fourth space before the closing border; rebuilt the `Auth:` line.
- **Files modified:** docs/architecture.md
- **Verification:** the plan's byte/character comparison now reports `old [56, 56] new [56, 56]`.
- **Committed in:** nothing — plan 01-05 commits these paths.

---

**Total deviations:** 2 auto-fixed (2 Rule 1 bugs introduced or exposed by this task's own edits)
**Impact on plan:** Both keep the document internally consistent and the diagram aligned; no scope expansion, no extra file touched.

## Issues Encountered

- **Line-ending assumption corrected by measurement.** The dispatch note said CONCERNS.md, entra-setup.md, PROJECT.md and v1-plan.md were LF; `grep -c $'\r$'` reports all eight files as CRLF (312/312, 102/102, 376/376, 324/324, 133/133, 240/240, 104/104, 102/102). Edits were made against the measured state and all eight remain fully CRLF with small diffs, so no normalization churn was introduced.
- **`grep -c 'Azure Artifact Signing'` initially returned 0 for docs/entra-setup.md** because the first paragraph wrap split the phrase across a line break. Rewrapped so the phrase sits whole on one line; now 1.
- **Observed but left alone (outside the plan's named statements):** `docs/architecture.md`'s Key NuGet Packages table still lists `Microsoft.AspNetCore.Components.WebAssembly 7.0.4` (a .NET 7-era version) and other package versions that were not in this plan's correction list. Only the `AntDesign` row was corrected because this task directly contradicted it.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- All eight paths are edited, uncommitted, and verified; plan 01-05 can stage exactly these eight for the docs land commit.
- `docs/assessment-2026-10-21.md`, `docs/next-phases-plan.md`, `docs/v3-plan.md` were not opened for edit and remain untracked, as the plan requires.
- Phase 11 inherits one authority pair for the publisher subject (`CN=Jakub Talaj` in `.planning/PROJECT.md` and `docs/entra-setup.md`) and a manifest that still carries the placeholder `CN=Azure Design Studio`, deliberately unchanged (D-21).

---
*Phase: 01-land-and-stabilize*
*Completed: 2026-10-21*

## Self-Check: PASSED
