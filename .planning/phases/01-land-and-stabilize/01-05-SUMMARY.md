---
phase: 01-land-and-stabilize
plan: 05
subsystem: docs
tags: [docs, git-land, commit-audit, worktree-proof, publisher, azure-artifact-signing, citation-audit]

# Dependency graph
requires:
  - phase: 01-04
    provides: the eight edited current-truth documents (README, AGENTS, CONCERNS, architecture, two bannered plans, PROJECT.md, entra-setup.md)
  - phase: 01-03
    provides: the committed shell slice whose suite baseline is 755
  - phase: 01-01
    provides: the committed core slice and the tree the docs land on top of
provides:
  - The third and final land commit `ba4da2e` — 12 documentation paths, no source, no GSD state files
  - The three previously untracked docs in the tree, closing the citation leg of SC#1
  - Worktree proof at the docs SHA — Desktop builds, offline suite `Passed: 755`
  - Recorded SC#5 audit over the committed tree (count, retired provider, publisher pair, ancestry, banner numstat)
affects: [01-06, 11-signed-msix-and-packaged-install]

actuals:
  tokens: 34609       # chars/4 over the realized commit diff (138434 chars, `git show HEAD --format= | wc -c`)
  tasks: 3
  commits: 1          # `git rev-list --count 9789076..HEAD` = 1

plan_head_before: 978907650cc7ddc95230ed895192125833658ad0
plan_head_after: ba4da2e29cf0ae0f39e5fbcc61457358b00f8061

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Twelve explicit paths staged in one `git add`; a staged-path allowlist regex plus set equality after the commit replaces `git add -A`"
    - "Per-commit proof from a throwaway `git worktree add` at the commit's own SHA, removed with `--force`"
    - "Land ordering asserted by `git merge-base --is-ancestor` between subject-resolved SHAs, never by log adjacency"

key-files:
  created:
    - docs/v3-plan.md
    - docs/next-phases-plan.md
    - docs/assessment-2026-10-21.md
  modified:
    - README.md
    - AGENTS.md
    - .planning/codebase/CONCERNS.md
    - docs/architecture.md
    - docs/v1-plan.md
    - docs/azure-refresh-plan.md
    - .planning/PROJECT.md
    - docs/entra-setup.md
    - docs/top-100-ui-improvements.md

key-decisions:
  - "The docs commit stages twelve paths in one `git add` and nothing else: no `.planning/STATE.md`, no `.planning/ROADMAP.md`, no `.planning/phases/**`, no source, no `.gitignore` (D-01 boundary)"
  - "The three untracked docs ride in this commit as the citation leg of SC#1 — README already advertises all three, so a fresh clone would otherwise carry dangling links"
  - "The two dated plans are bannered, not rewritten; the commit adds exactly one line and deletes none in each, proven by `git diff --numstat` (1/0)"
  - "The plan's credential-scan regex is over-broad: case-insensitive `clientsecret` matches the identifier `ClientSecret`, which five prose lines use to describe a real defect; a value-shaped scan is empty and the commit carries no credential material (see Deviations)"

patterns-established:
  - "Land audit shape: staged count → allowlist → code-extension → credential scan, all before `git commit`; then set equality and tree proof after"
  - "Ancestry ordering of land commits via `git log --fixed-strings --grep` + `git merge-base --is-ancestor`, because GSD metadata commits interleave"

requirements-completed: [SC3, SC5]

coverage:
  - id: D1
    description: "The docs commit exists with exactly the 12 documentation paths, the three previously untracked docs included, and no source or state path"
    requirement: SC3
    verification:
      - kind: other
        ref: "git show --name-only --pretty=format: HEAD | sed '/^$/d' | wc -l  →  12; code-extension grep → no output"
        status: pass
    human_judgment: false
  - id: D2
    description: "The docs commit restores into a clean worktree, builds the solution including Desktop, and keeps the offline suite at 755"
    requirement: SC3
    verification:
      - kind: integration
        ref: "dotnet build src/AzureDesignStudio.sln -v m in worktree at ba4da2e → 'AzureDesignStudio.Desktop ->'; dotnet test --filter \"Category!=LiveAzure\" → Passed: 755, Failed: 0"
        status: pass
    human_judgment: false
  - id: D3
    description: "SC#5 truth audit: no stale count, no retired identity provider in the four current-truth files; publisher subject and signing route once per authority"
    requirement: SC5
    verification:
      - kind: other
        ref: "grep -iE 'B2C' README.md AGENTS.md docs/architecture.md .planning/codebase/CONCERNS.md → exit 1; grep -E '(7|8) projects?' → exit 1; grep -c '11 project' → 1 each; grep -c 'CN=Jakub Talaj' / 'Azure Artifact Signing' → 1 in each authority"
        status: pass
    human_judgment: false
  - id: D4
    description: "Every docs/*.md path cited by README or AGENTS is tracked in the docs commit's tree (SC#1 citation leg)"
    verification:
      - kind: other
        ref: "8 extracted paths, each `git ls-files --error-unmatch` → tracked, missing=0"
        status: pass
    human_judgment: false
  - id: D5
    description: "Each dated record differs from its pre-banner state by exactly one added line and zero deletions"
    verification:
      - kind: other
        ref: "git diff --numstat ba4da2e^ ba4da2e -- docs/v1-plan.md docs/azure-refresh-plan.md → '1 0' for both"
        status: pass
    human_judgment: false

# Metrics
duration: 6min
completed: 2026-10-21
status: complete
---

# Phase 01 Plan 05: Docs Land Commit Summary

**The docs topic landed as commit `ba4da2e` — twelve documentation paths with the three previously untracked docs now tracked, proved from its own worktree (`AzureDesignStudio.Desktop` built, `Passed: 755 / Failed: 0`), with the SC#5 audit clean over the committed tree and the land ordering core → shell → docs proven by ancestry.**

## Performance

- **Duration:** ~6 min (12:13 → 12:19 local, 10:13Z → 10:18:36Z), of which 214 s was the cold full-solution build in the throwaway worktree and 6 s the suite
- **Started:** 2026-10-21T10:13:00Z (approx., first gate command)
- **Completed:** 2026-10-21T10:18:36Z
- **Tasks:** 3
- **Files modified:** 12 (9 modified, 3 created)

## Accomplishments

- The third and last land commit exists locally with exactly the twelve documentation paths and nothing else: no `src/` path, no `.gitignore`, no harness path, no `.planning/STATE.md`, no `.planning/ROADMAP.md`, no `.planning/phases/**` artefact.
- `docs/v3-plan.md`, `docs/next-phases-plan.md` and `docs/assessment-2026-10-21.md` are admitted to the index for the first time, so a fresh clone carries every document README advertises (citation leg of SC#1: 8 cited docs, 0 missing).
- The docs commit was proved at its own SHA in a throwaway worktree: the solution built with the Desktop project line present and the offline suite stayed at the shell commit's baseline (`Passed: 755`, `Failed: 0`, `Total: 755`).
- SC#5 is proven over the committed tree, not by prose: zero `B2C` hits and zero stale `7|8 projects` hits across the four current-truth files, the measured count once in each, and `CN=Jakub Talaj` / `Azure Artifact Signing` exactly once in each of the two authorities.
- The three land subjects each resolve to exactly one commit and are ordered core → shell → docs by `git merge-base --is-ancestor`, which is the only stable statement when GSD metadata commits sit between them.

## Task Commits

1. **Task 1: Stage the twelve documentation paths and create the docs commit** — `ba4da2e` (docs)
2. **Task 2: Prove the docs commit from a clean worktree at 755** — no commit (verification only)
3. **Task 3: Run the SC#5 truth audit and the docs-topic audit** — no commit (verification only)

This SUMMARY is intentionally **not committed** (dispatch instruction); the orchestrator creates the authoritative commit in the parent repository.

## Files Created/Modified

- `docs/v3-plan.md` — new (471 lines): N-number authority and Trust Gate framing, first time tracked.
- `docs/next-phases-plan.md` — new (188 lines): recommended execution order and the docs-truth item, first time tracked.
- `docs/assessment-2026-10-21.md` — new (221 lines): the dated defect list, first time tracked.
- `README.md` — Solution Structure count 8 → 11 plus three tree rows (9 lines changed).
- `AGENTS.md` — `(AAD B2C)` → `(Entra ID work+personal accounts)`, `8 projects in src/` → `11 projects in src/`.
- `.planning/codebase/CONCERNS.md` — scope line `src/ — 8 projects` → `11 projects`.
- `docs/architecture.md` — .NET 10, Ant Design Blazor 1.6.2, Entra ID work+personal, SQLite workspace store, four missing tree rows; both ASCII boxes re-padded to identical widths.
- `docs/v1-plan.md`, `docs/azure-refresh-plan.md` — one historical-banner blockquote line each, zero deletions.
- `.planning/PROJECT.md` — the publisher Key Decisions row (line 84) plus two Context bullets in past tense.
- `docs/entra-setup.md` — target-state paragraph in §4: Artifact Signing, individual validation, `CN=Jakub Talaj`, manifest placeholder caveat.
- `docs/top-100-ui-improvements.md` — the pre-existing modification carried by the docs topic (169 lines changed).

## Decisions Made

- **The commit scope is exactly the plan's twelve paths, staged in one `git add`.** Nothing else in the working tree — the four OMP harness files (`.planning/.omp-checkpoint.json`, `.omp-next-action.json`, `.omp-task-results.json`, `milestone.lock`) stayed untracked, as did `.planning/phases/**`, `STATE.md` and `ROADMAP.md`.
- **The commit was authored on the workspace's detached HEAD.** This checkout is an OMP isolated workspace whose commit the orchestrator re-creates authoritatively in the parent; no branch was created and nothing was pushed, amended, or force-anything.
- **The worktree lived in the temp directory**, not `../ads-land-docs` relative to the workspace root, to honour the isolation rule that forbids writing outside this tree. Same command shape, same `--force` removal; the path is recorded in the log names.

## Deviations from Plan

### Auto-fixed Issues

None.

### Gate defect reported, not worked around

**1. [Rule 1 - Gate is over-broad] The plan's credential scan matches the `ClientSecret` identifier, not credential material**
- **Found during:** Task 1, pre-commit gate
- **Issue:** The plan's exact pre-commit check `grep -qiE 'clientsecret|…'` over `git diff --cached -U0` prints five lines. All five are prose in the three previously untracked docs that name the `AzureSubscriptionModel.ClientSecret` property and `ClientSecretCredential` construction sites to describe a real stored-secret defect. The same identifier occurs in tracked `.md` files at the parent commit (`.planning/codebase/CONCERNS.md`, `INTEGRATIONS.md`, `STACK.md`, `docs/architecture.md`, several phase files), so the pattern cannot be clean over any commit that includes these documents.
- **What was done instead:** The gate was run verbatim and its five lines are recorded below. A value-shaped scan (assignment of a secret, private-key header, `AccountKey=`/`SharedAccessKey=` with payload, SAS `sig=`) was run over the same diff and is empty. The documents were **not** edited to dodge the regex: they are defect records, and D-17's own rule is that records are marked, not rewritten.
- **Files modified:** none.
- **Verification:** exact gate output (5 identifier lines) and empty value-shaped scan, both quoted in §1 below.
- **Committed in:** n/a (gate defect, not a code change).

---

**Total deviations:** 0 auto-fixed; 1 plan gate reported unsound with evidence.
**Impact on plan:** The commit content is unaffected — no credential value is present. The only unmet acceptance item is the literal "empty credential scan", which fails on identifier prose that predates this phase.

## Issues Encountered

- **`git worktree add` was placed under the system temp dir** rather than the plan's `../ads-land-docs`, because the repo root is an OMP isolated workspace and `..` is the workspace parent, outside the tree this agent may write. Confirmed removed afterwards: `git worktree list` shows only the workspace checkout.
- **`git commit -F /c/Users/...` failed** (`could not read log file`) on the first attempt because the Windows-native git does not resolve `/c/...` paths; re-run with `C:/Users/...` succeeded. No commit was created by the failed attempt (HEAD stayed at `9789076`).
- **Two new docs stored with LF in their blobs** (`docs/assessment-2026-10-21.md`, `docs/next-phases-plan.md`); git printed its `LF will be replaced by CRLF` warning at `git add`, which is the normal `core.autocrlf=true` round-trip, not a mixed-ending artefact. The eight edited files remained fully CRLF (measured in plan 01-04).

## Evidence

### 1. The docs commit and its gates

```text
$ git rev-parse HEAD
ba4da2e29cf0ae0f39e5fbcc61457358b00f8061
$ git rev-parse HEAD^{tree}
dfd25f24dc736fb68d78121a19c3b93e1688b3e2
$ git log -1 --format=%s
docs: correct project count and auth model; record publisher and signing route
$ git show --name-only --pretty=format: HEAD | sed '/^$/d' | wc -l
12
```

The twelve paths (set equal to the plan's `files_modified`):

```text
.planning/PROJECT.md
.planning/codebase/CONCERNS.md
AGENTS.md
README.md
docs/architecture.md
docs/assessment-2026-10-21.md
docs/azure-refresh-plan.md
docs/entra-setup.md
docs/next-phases-plan.md
docs/top-100-ui-improvements.md
docs/v1-plan.md
docs/v3-plan.md
```

Pre-commit gates (outputs observed with the twelve paths staged):

```text
staged count: 12
allowlist violations: 0
code files: 0
```

Post-commit gates:

```text
$ git show --name-only --pretty=format: HEAD | grep -E '\.(cs|razor|js|css|csproj|props)$'
(no output)   exit=1
$ git show --name-only --pretty=format: HEAD | sed '/^$/d' | grep -nE '^src/|^\.gitignore$|harness'
(no output)   exit=1
$ git ls-tree --name-only HEAD -- docs/v3-plan.md docs/next-phases-plan.md docs/assessment-2026-10-21.md
docs/assessment-2026-10-21.md
docs/next-phases-plan.md
docs/v3-plan.md
$ git ls-files --error-unmatch docs/v3-plan.md   # before the commit
exit=1   (genuinely untracked, now tracked at HEAD)
$ git status --porcelain
?? .planning/.omp-checkpoint.json
?? .planning/.omp-next-action.json
?? .planning/.omp-task-results.json
?? .planning/milestone.lock
```

None of the twelve paths appears in `git status --porcelain`; the four remaining entries are OMP harness files, not land content.

**Credential scan — verbatim plan command (`git show HEAD -U0 | grep -niE 'clientsecret|client_secret|password=|api[_-]?key|BEGIN [A-Z ]*PRIVATE KEY|AccountKey=|SharedAccessKey'`)** — 5 lines, all identifier prose:

```text
140:+| N9 stored subscription secret | 17 `ClientSecret` matches under `Server/Models` + `Server/Services`; **8** `ClientSecretCredential` construction sites | **true** |
375:+| Stored subscription secret | **present** — 17 `ClientSecret` matches, 8 `ClientSecretCredential` sites in `Server` | grep |
775:+- The server still stores a **long-lived client secret per subscription** (`AzureSubscriptionModel.ClientSecret`) and exchanges it for ARM tokens.
801:+| Secrets | Client secrets encrypted with Key Vault, decrypted into `ClientSecretCredential` at 7 sites | Long-lived replayable secrets (N9) |
882:+Evidence: `AzureSubscriptionModel.ClientSecret`; `CryptoService` decrypts it; `ClientSecretCredential` is constructed at 7+ sites in `DeployService`.
```

**Value-shaped credential scan over the same diff** (private-key header, assigned secret values, `AccountKey=`/`SharedAccessKey=` payloads, SAS `sig=`):

```text
$ git show HEAD -U0 | grep -niE 'BEGIN [A-Z ]*PRIVATE KEY|(client[_-]?secret|password|accountkey|sharedaccesskey|api[_-]?key)[[:space:]]*[=:][[:space:]]*[^ `|]{6,}|AccountKey=[A-Za-z0-9+/=]{10,}|SharedAccessKey=[A-Za-z0-9+/=]{10,}|sig=[A-Za-z0-9%]{20,}'
(no output)   exit=1   → empty
```

Commit stat:

```text
 .planning/PROJECT.md            |   5 +-
 .planning/codebase/CONCERNS.md  |   2 +-
 AGENTS.md                       |   4 +-
 README.md                       |   9 +-
 docs/architecture.md            |  28 ++-
 docs/assessment-2026-10-21.md   | 221 +++++++++++++++++++
 docs/azure-refresh-plan.md      |   1 +
 docs/entra-setup.md             |   9 +
 docs/next-phases-plan.md        | 188 ++++++++++++++++
 docs/top-100-ui-improvements.md | 169 ++++++++++----
 docs/v1-plan.md                 |   1 +
 docs/v3-plan.md                 | 471 ++++++++++++++++++++++++++++++++++++++++
 12 files changed, 1045 insertions(+), 63 deletions(-)
```

### 2. Worktree proof at the docs SHA

```text
$ git worktree add C:/Users/jakub/AppData/Local/Temp/ads-land-docs HEAD
Preparing worktree (detached HEAD ba4da2e)
HEAD is now at ba4da2e docs: correct project count and auth model; record publisher and signing route
$ git rev-parse HEAD
ba4da2e29cf0ae0f39e5fbcc61457358b00f8061
$ dotnet build src/AzureDesignStudio.sln -v m        # exit=0, 214.13 s
  AzureDesignStudio.Desktop -> C:\Users\jakub\AppData\Local\Temp\ads-land-docs\src\AzureDesignStudio.Desktop\bin\Debug\net10.0-windows10.0.19041.0\win-x64\AzureDesignStudio.Desktop.dll
Build succeeded.
    91 Warning(s)
    0 Error(s)
$ dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"   # exit=0
Passed!  - Failed:     0, Passed:   755, Skipped:     0, Total:   755, Duration: 1 s - AzureDesignStudio.Core.Tests.dll (net10.0)
$ git worktree remove --force C:/Users/jakub/AppData/Local/Temp/ads-land-docs   # exit=0
$ git worktree list
C:/Users/jakub/.omp/wt/t0359e851c/m ba4da2e (detached HEAD)
```

Logs: `C:/Users/jakub/AppData/Local/Temp/ads-land-docs-build.log`, `C:/Users/jakub/AppData/Local/Temp/ads-land-docs-test.log`.

### 3. SC#5 truth audit over the committed tree

```text
$ grep -niE 'B2C' README.md AGENTS.md docs/architecture.md .planning/codebase/CONCERNS.md
(no output)   exit=1     → zero retired-provider hits
$ grep -nE '(7|8) projects?' README.md AGENTS.md docs/architecture.md .planning/codebase/CONCERNS.md
(no output)   exit=1     → zero stale count phrases
$ grep -c '11 project' README.md AGENTS.md docs/architecture.md .planning/codebase/CONCERNS.md
README.md:1
AGENTS.md:1
docs/architecture.md:1
.planning/codebase/CONCERNS.md:1
$ grep -c 'CN=Jakub Talaj' .planning/PROJECT.md docs/entra-setup.md
.planning/PROJECT.md:1
docs/entra-setup.md:1
$ grep -c 'Azure Artifact Signing' .planning/PROJECT.md docs/entra-setup.md
.planning/PROJECT.md:1
docs/entra-setup.md:1
```

The Key Decisions row still starts with the publisher prefix:

```text
84:| Publisher legal name and signing route: `CN=Jakub Talaj` (individual validation) via Azure Artifact Signing | No private-key custody; matches the Phase 11 `azure/trusted-signing-action@v0` path; identity validation has external lead time | Decided 2026-10-21 — manifest update deferred to Phase 11 (D-21) |
```

### 4. Citation leg of SC#1 — every docs path README/AGENTS names is tracked

```text
$ grep -ohE 'docs/[A-Za-z0-9._/-]+\.md' README.md AGENTS.md | sort -u
docs/architecture.md
docs/assessment-2026-10-21.md
docs/entra-setup.md
docs/next-phases-plan.md
docs/top-100-improvements.md
docs/top-100-ui-improvements.md
docs/v2-plan.md
docs/v3-plan.md
$ (each path) git ls-files --error-unmatch <path>
TRACKED   docs/architecture.md
TRACKED   docs/assessment-2026-10-21.md
TRACKED   docs/entra-setup.md
TRACKED   docs/next-phases-plan.md
TRACKED   docs/top-100-improvements.md
TRACKED   docs/top-100-ui-improvements.md
TRACKED   docs/v2-plan.md
TRACKED   docs/v3-plan.md
missing=0
```

### 5. Three-subject ancestry ordering

```text
$ git log --format=%H -n 1 --fixed-strings --grep='feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch'
078a9d746f71baf4da4e7a9f2a96767f565f7fe9
$ git log --format=%H -n 1 --fixed-strings --grep='feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag'
ea8ac77bf6c1b4885ee5766d68566c9d779e7afe
$ git log --format=%H -n 1 --fixed-strings --grep='docs: correct project count and auth model; record publisher and signing route'
ba4da2e29cf0ae0f39e5fbcc61457358b00f8061
# each subject resolves to exactly one commit
1  feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch
1  feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag
1  docs: correct project count and auth model; record publisher and signing route
$ git merge-base --is-ancestor 078a9d7… ea8ac77…   → exit=0
$ git merge-base --is-ancestor ea8ac77… ba4da2e…   → exit=0
```

The three resolved subjects print verbatim as required. The docs commit's parent is the `docs(01-03)` metadata commit `9789076`, which is why ancestry — not adjacency — is the ordering statement.

### 6. Banner-only gate for the two dated records

```text
$ git show HEAD:docs/v1-plan.md | grep -c 'Status:.*historical record'
1
$ git show HEAD:docs/azure-refresh-plan.md | grep -c 'Status:.*historical record'
1
$ git diff --numstat ba4da2e^ ba4da2e -- docs/v1-plan.md docs/azure-refresh-plan.md
1	0	docs/azure-refresh-plan.md
1	0	docs/v1-plan.md
```

One added line, zero deleted lines in each — no dated entry was rewritten.

## User Setup Required

None — no external service configuration required. Nothing was pushed.

## Next Phase Readiness

- The local branch now holds the pre-land tip followed by exactly three land commits, each with an independent worktree build and 753/755/755 suite evidence (753 for the core commit per 01-01's recorded proof).
- SC#5 and the citation leg of SC#1 are proven by recorded command output over the committed tree, so plan 01-06 can proceed to D-08's full SC#1 audit, the bundle, and the two pushes.
- Phase 11 inherits one publisher authority pair (`CN=Jakub Talaj` + Azure Artifact Signing, once per authority) and an untouched `Package.appxmanifest` placeholder.
- Carried forward: the plan's credential-scan regex needs adjusting before it is reused as a gate — it matches the `ClientSecret` identifier, which is present in tracked docs at every commit.

---
*Phase: 01-land-and-stabilize*
*Completed: 2026-10-21*

## Self-Check: FAILED

Four of the five acceptance items are fully observed. The fifth — "the empty credential scan" — is **not** empty:

```text
$ git show HEAD -U0 | grep -niE 'clientsecret|client_secret|password=|api[_-]?key|BEGIN [A-Z ]*PRIVATE KEY|AccountKey=|SharedAccessKey'
140:+| N9 stored subscription secret | 17 `ClientSecret` matches under `Server/Models` + `Server/Services`; **8** `ClientSecretCredential` construction sites | **true** |
375:+| Stored subscription secret | **present** — 17 `ClientSecret` matches, 8 `ClientSecretCredential` sites in `Server` | grep |
775:+- The server still stores a **long-lived client secret per subscription** (`AzureSubscriptionModel.ClientSecret`) and exchanges it for ARM tokens.
801:+| Secrets | Client secrets encrypted with Key Vault, decrypted into `ClientSecretCredential` at 7 sites | Long-lived replayable secrets (N9) |
882:+Evidence: `AzureSubscriptionModel.ClientSecret`; `CryptoService` decrypts it; `ClientSecretCredential` is constructed at 7+ sites in `DeployService`.
```

All five lines are the identifier `ClientSecret`/`ClientSecretCredential` in defect prose from `docs/assessment-2026-10-21.md`, `docs/next-phases-plan.md` and `docs/v3-plan.md`; the value-shaped scan in §1 is empty. Observed and verified: commit SHA `ba4da2e29cf0ae0f39e5fbcc61457358b00f8061`, tree `dfd25f24dc736fb68d78121a19c3b93e1688b3e2`, 12 paths, build/755 proof, ancestry, and both `1 0` numstats.
