---
phase: 02-reproducible-build-and-ci-baseline
plan: 05
subsystem: infra
tags: [github-actions, windows-latest, maui, dotnet-workloads, dotnet-format, continue-on-error]

requires:
  - phase: 02-02
    provides: locked restore (--locked-mode) and the lock-file guard idiom the Windows job repeats
  - phase: 02-04
    provides: central build props with warnings-as-errors, so the Desktop Release build is a real gate
provides:
  - non-blocking `Desktop (windows-latest)` job that builds Desktop, format-checks changed files, and records its own duration
  - pinned `sdk.workloadVersion` for reproducible workload restore
  - SC#2/SC#5 evidence ledger with local baselines and empty run/proof tables
affects: [02-06-recorded-runs, 02-07-blocking-flip, 11-aot-publish]

actuals:
  tokens: 2401
  tasks: 3
  commits: 3
  plan_head_before: b9cebd1
  plan_head_after: 1af6b6a

tech-stack:
  added: []
  patterns:
    - "Non-blocking CI job: `continue-on-error: true` plus an `if: always()` metrics artefact that makes the duration a thing, not a claim"
    - "Changed-file format gate: base-ref short circuit → `git diff --diff-filter=ACMR` → `dotnet format --verify-no-changes --include` (exit 2 = would change)"
    - "Workload restore by project, so the runner image's older workload band cannot masquerade as the TFM's"

key-files:
  created:
    - .planning/phases/02-reproducible-build-and-ci-baseline/02-DESKTOP-RUNS.md
  modified:
    - .github/workflows/build.yml
    - global.json

key-decisions:
  - "Workloads are restored from the Desktop project, not by the bare workload name publish.yml uses: the image ships .NET 9 MAUI and the project's target framework is the authority."
  - "AOT is disabled in the job (74 s vs 466 s); AOT is a publish concern re-verified in Phase 11, and the phase's proof is compile+format correctness, not packaging."
  - "`sdk.workloadVersion` is its own one-line commit so a runner-side resolver failure in plan 02-06 reverts it alone."
  - "The evidence ledger ships empty on purpose: run rows need a run URL and a measured duration, and requirement status stays untouched until plan 02-06 records real runs."

patterns-established:
  - "Non-blocking job + metrics artefact: the mechanism half of a gate lands before the blocking flip."
  - "Ledger with exact column contract and labelled fill-in fields, so a later plan parses it instead of re-deriving it from prose."

requirements-completed: []  # Intentionally empty: BUILD-02/03/05 need recorded runs; plan 02-06 owns that evidence.

coverage:
  - id: D1
    description: "`desktop` job exists with name Desktop (windows-latest), windows-latest, continue-on-error, 30-minute timeout, locked restore behind the three-part guard, AOT-disabled Release build, changed-file format gate, and always-on metrics upload"
    requirement: BUILD-02
    verification:
      - kind: other
        ref: "python -c \"import yaml; … jobs.desktop assertions\" → `job ok`"
        status: pass
    human_judgment: false
  - id: D2
    description: "Format gate proven both ways: exit 0 on a clean file, exit 2 with a WHITESPACE diagnostic on a mis-indented one, exit 0 after revert"
    requirement: BUILD-03
    verification:
      - kind: other
        ref: "throwaway detached worktree: `dotnet format … --verify-no-changes --no-restore --include src/AzureDesignStudio.Core/APIM/APIMModel.cs` → rc 0 / rc 2 / rc 0"
        status: pass
    human_judgment: false
  - id: D3
    description: "`global.json` pins `sdk.workloadVersion` 10.0.401.1 as its own commit while `dotnet --version` still resolves 10.0.401"
    requirement: BUILD-02
    verification:
      - kind: other
        ref: "`dotnet --version` → 10.0.401; `dotnet workload --version` → 10.0.401.1; `dotnet workload restore …Desktop.csproj` → exit 0"
        status: pass
    human_judgment: false
  - id: D4
    description: "SC#2 red run and SC#5 two-green-run flip recorded from real CI runs"
    requirement: BUILD-05
    verification: []
    human_judgment: true
    rationale: "Only GitHub Actions can execute the job; a local build cannot prove the job fails or turns green, and the ledger must cite run URLs."
---

# Phase 02 Plan 05: Desktop CI Baseline Summary

**A non-blocking `Desktop (windows-latest)` job that builds the release Desktop app from pinned, locked inputs, format-checks only the files a PR changed, and records its own wall clock as an artefact — plus a pinned workload set and an empty evidence ledger waiting for real runs.**

## Performance

- **Duration:** ~45 min (approximate — start was not instrumented; commit times 2026-10-22T22:33Z → 22:38Z)
- **Started:** 2026-10-22T21:52:00Z
- **Completed:** 2026-10-22T22:40:00Z
- **Tasks:** 3
- **Files modified:** 3 (+181/−1)

## Accomplishments

- `jobs.desktop` — `Desktop (windows-latest)`, `windows-latest`, `continue-on-error: true`, `timeout-minutes: 30`, inheriting only the existing `pull_request`/`push` triggers. No `pull_request_target`, no `permissions` block, no packs cache.
- Locked restore (`dotnet restore src/AzureDesignStudio.sln --locked-mode`) behind the same three-part lock guard as the Linux job, on `shell: bash` because the Windows default is PowerShell.
- Workloads restored **by project** (`dotnet workload restore src/AzureDesignStudio.Desktop/…csproj`) — the image ships .NET 9 MAUI only.
- Release Desktop build with `--no-restore -p:RunAOTCompilation=false`, and a `pwsh` changed-file format gate that exits 0 on a non-PR run or an empty `.cs` diff and treats exit 2 as failure.
- `desktop-build-metrics.json` (`job`, `run_id`, `commit`, `started_at`, `ended_at`, `seconds`, `aot: disabled`) written and uploaded with `if: always()`; also appended to the job summary.
- `global.json` pins `sdk.workloadVersion` `10.0.401.1` as its own revertible commit; `dotnet --version` still prints `10.0.401`.
- `02-DESKTOP-RUNS.md` created with the two local baseline rows and the empty run / SC#2 / SC#5 tables — no fabricated rows and no requirement marked complete.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 1 | Add the non-blocking Desktop job with locked restore, format gate and metrics | `908b62c` | ci |
| 2 | Prove the format gate both ways (throwaway worktree; no repository change) | — | (none by design) |
| 3 | Pin the workload set | `0f1ceec` | build |
| 3 | Add the SC#2/SC#5 evidence ledger | `1af6b6a` | docs |

Plan metadata commit (this SUMMARY) follows as a separate docs commit.

## Evidence

### Task 1 — job structure (parsed, not eyeballed)

`python -c "import yaml; d=yaml.safe_load(open('.github/workflows/build.yml')) …"` → `job ok`, `prohibitions-ok`.

Observed facts behind those assertions:

- `list(d['jobs'])` = `['build', 'codeql', 'desktop']`.
- `jobs.desktop`: `name: Desktop (windows-latest)`, `runs-on: windows-latest`, `continue-on-error: true`, `timeout-minutes: 30`.
- `Setup .NET` carries `global-json-file: global.json`, `cache: true`, `cache-dependency-path: '**/packages.lock.json'`, and **no** `dotnet-version` key.
- `Restore workloads` run = `dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj`.
- `Restore (locked)` → `--locked-mode`, `shell: bash`; `Lock files must be committed` → `shell: bash`, with `git diff --exit-code -- '*packages.lock.json'`, `git status --porcelain -- '*packages.lock.json'`, and the `git ls-files | wc -l` vs `ls src/*/*.csproj | wc -l` equality (11 = 11 locally).
- `Build Desktop` → `--no-restore` and `RunAOTCompilation=false`.
- `Format (changed files)` → `shell: pwsh`, `--verify-no-changes`, `GITHUB_BASE_REF` guard, `--diff-filter=ACMR`, empty-list exit 0.
- `Record duration` → `if: always()` and writes `seconds`; final step → `actions/upload-artifact@v4`, `if: always()`, `name: desktop-build-metrics`.
- `grep pull_request_target|dotnet/packs|workload install .github/workflows/build.yml` → no matches (exit 1 each).
- Commit `908b62c` touches exactly one path: `.github/workflows/build.yml` (105 insertions).

### Task 2 — format gate, both directions

Throwaway detached worktree at `908b62c`, prepared with `dotnet restore src/AzureDesignStudio.sln` (needed because `--no-restore` reads the worktree's own assets file):

| Step | Command (abridged) | Exit | Observed |
|------|--------------------|------|----------|
| clean file | `dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore --include src/AzureDesignStudio.Core/APIM/APIMModel.cs` | 0 | 16.48 s, no diagnostics |
| mis-indented | same, after `printf '   \n' >> …APIMModel.cs` | 2 | `APIMModel.cs(104,1): error WHITESPACE: Fix whitespace formatting. Replace 4 characters with '\n'.` |
| reverted | same, after `git checkout -- …APIMModel.cs` | 0 | no diagnostics |
| changed list | `git diff --name-only --diff-filter=ACMR HEAD -- '*.cs'` | — | 0 lines (the CI step's short-circuit branch) |

Worktree removed with `git worktree remove --force <windows-path>`; `git worktree list` names only the clone, and `git status --porcelain --untracked-files=no` is empty.

### Task 3 — workload pin and ledger

- `grep '"workloadVersion": "10.0.401.1"' global.json` → match.
- `dotnet --version` → `10.0.401`; `dotnet workload --version` → `10.0.401.1`; `dotnet workload restore src/AzureDesignStudio.Desktop/…csproj` → exit 0 (0.57 s, nothing new to install).
- Ledger structure: `## Local baseline`, `## Runs`, `## SC#2`, `## SC#5`, the exact run-table header, `Flip commit:`, `Required check: Desktop (windows-latest)`, `Proof:`, the `74`/`466` baseline figures and the `local — not a CI run` label all present → `TASK3-VERIFY-PASS`.
- Commits: `0f1ceec` (global.json only), `1af6b6a` (ledger only).

### Regression gates on the final tree

| Gate | Command | Result |
|------|---------|--------|
| Release solution build | `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` | exit 0 — `Build succeeded.` 91 Warning(s), 0 Error(s); `grep -cE 'warning (CS\|NU\|IDE\|CA\|MSB)[0-9]+'` = **0** (the 91 are `PRI263` resource-task warnings) |
| Format (whole solution) | `dotnet format src/AzureDesignStudio.sln --verify-no-changes` | exit 0 |
| Offline tests | `dotnet test src/AzureDesignStudio.Core.Tests -c Release --no-build --filter "Category!=LiveAzure"` | exit 0 — `Failed: 0, Passed: 755, Skipped: 0, Total: 755` |

No GitHub Actions run is claimed. The job has never executed on a runner; every run-row in `02-DESKTOP-RUNS.md` is left empty for plan 02-06.

## Files Created/Modified

- `.github/workflows/build.yml` — adds the `desktop` job (105 lines) beside `build` and `codeql`; existing jobs untouched.
- `global.json` — adds `"workloadVersion": "10.0.401.1"` inside the existing `sdk` object.
- `.planning/phases/02-reproducible-build-and-ci-baseline/02-DESKTOP-RUNS.md` — new evidence ledger: front matter (`status: awaiting-runs`), local baseline, empty run table, SC#2 fill-in fields, SC#5 labelled lines.

## Decisions Made

- **Workloads by project, not by name.** `publish.yml`'s `dotnet workload install maui-windows` is the wrong shape for a `net10.0-windows10.0.19041.0` project on an image that ships the .NET 9 band; the project graph is the authority.
- **AOT off in CI.** 74 s vs 466 s, and AOT is re-verified in Phase 11 at publish time.
- **Format gate scoped to changed files.** Whole-solution formatting is ~51 s cold and would make every PR pay for files it did not touch; the repo-wide normalisation already landed in plan 02-03.
- **Workload pin as a one-line commit.** If the set fails to resolve on the runner, plan 02-06 reverts one commit and the rest of the phase stands.

## Deviations from Plan

**1. [Environment] Work executed in a clone inside the isolated worktree**
- **Found during:** setup
- **Issue:** the OMP worktree checked out a different repository, as the shared contract warned.
- **Fix:** `git clone C:/Users/jakub/Desktop/Projects/AzureDesignStudio ./work`, branch `gsd/02-05` at `b9cebd1`. The deliverable commits live there; the orchestrator merges the branch.

**2. [Environment] Repo-local git identity set on the fresh clone**
- **Found during:** Task 1 commit
- **Issue:** `git commit` failed with `unable to auto-detect email address`.
- **Fix:** `git config user.name "Jakub Talaj"` / `user.email "jacobeen@icloud.com"`, matching the author of every prior commit. No content change.

**3. [Execution shape] Task 2's compound `<automated>` line was run step-by-step**
- **Found during:** Task 2
- **Issue:** the compound form builds a `mktemp -d` path that git on Windows resolves differently from the shell; the worktree could not be created/removed by that path, and a first `--no-restore` run inside a fresh worktree had no assets file.
- **Fix:** identical assertions executed as discrete commands with the same values (rc 0 / rc 2 / rc 0, empty changed list, worktree removed, clean tree), plus one preparatory `dotnet restore` inside the throwaway worktree. No deliverable changed.

**4. [Observation] AOT-disabled build re-measured at 176 s, not 74 s**
- **Found during:** Task 3 (baseline note)
- **Issue:** a cold `--no-incremental` rebuild after the normalisation landed measured 176 s wall (91 warnings, 0 errors).
- **Fix:** the ledger keeps the plan's baseline rows (74 s AOT off, 466 s AOT on, E9/E10) and adds a dated note recording the 176 s re-measurement and why it differs (cold tree, warnings-as-errors now on). Neither number is presented as a CI measurement.

---

**Total deviations:** 3 execution/infrastructure (no plan-behaviour change), 1 recorded observation.
**Impact on plan:** none on the deliverable; the job, the pin, and the ledger are exactly the planned shapes.

## Issues Encountered

- The `dotnet format` workspace emits a "Warnings were encountered while loading the workspace" line even on exit 0 (whole-solution and scoped runs alike). It is pre-existing noise, not a diagnostic; the exit code is the gate.

## Known Stubs

None. The empty tables in `02-DESKTOP-RUNS.md` are deliberate placeholders with a named owner (plan 02-06); they are not wired data, and no requirement was marked complete on their behalf.

## Threat Flags

None beyond the plan's threat model. The job runs untrusted PR code with the default read-only token, references no secrets, and adds no cache beyond `setup-dotnet`'s lock-file-keyed NuGet cache.

## Next Phase Readiness

- Plan 02-06 can push this branch, open the deliberate-error PR, and fill every row in `02-DESKTOP-RUNS.md` from run URLs and `desktop-build-metrics.json`.
- Plan 02-07 flips `continue-on-error` after two recorded green runs and requires `Desktop (windows-latest)`; the ledger's three labelled lines are ready for its values.
- The workload pin is independently revertible if the runner cannot resolve the set.

## Self-Check: PASSED

| Claim | Check |
|-------|-------|
| `.github/workflows/build.yml`, `global.json`, `02-DESKTOP-RUNS.md` exist | present in the working tree |
| Commits `908b62c`, `0f1ceec`, `1af6b6a` are ancestors of HEAD | `git log --oneline b9cebd1..HEAD` lists all three |
| Task 1 and Task 3 `<automated>` verifies | both printed `TASK1-VERIFY-PASS` / `TASK3-VERIFY-PASS` |
| Release build / format / tests | exit 0 / exit 0 / 755 passed |
