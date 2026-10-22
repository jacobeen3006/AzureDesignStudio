---
phase: 02-reproducible-build-and-ci-baseline
plan: 01
subsystem: build
tags: [dotnet, msbuild, nuget, central-package-management, lock-files, global-json, maui]

requires:
  - phase: 01-land-and-stabilize
    provides: a green solution build (0 errors, 91 PRI263 warnings) and 755 offline tests
provides:
  - root global.json pinning SDK 10.0.401 with latestPatch and no prerelease
  - root Directory.Build.props owning MauiVersion 10.0.110 and RestorePackagesWithLockFile
  - root Directory.Packages.props owning all 54 package ids (53 moved + the crypto pin)
  - ten csproj files that declare only Include= plus reference metadata
  - System.Security.Cryptography.Xml 10.0.13 in the WASM and Desktop graphs
  - eleven committed packages.lock.json files (format version 2)
affects: [02-02, 02-03, 02-04, 02-05]

actuals:
  tokens: 85389
  tasks: 3
  commits: 3
  plan_head_before: 578949c
  plan_head_after: 93b17be

tech-stack:
  added: []
  patterns:
    - "One version authority per concern: props for properties, Directory.Packages.props for versions"
    - "Generated artifacts (lock files) are committed unmodified and never hand-edited"
    - "Security remediation of a transitive is a direct reference plus a central pin, not a second suppression"

key-files:
  created:
    - global.json
    - Directory.Build.props
    - Directory.Packages.props
    - src/*/packages.lock.json (11 files)
  modified:
    - AGENTS.md
    - .planning/REQUIREMENTS.md
    - src/AzureDesignStudio/AzureDesignStudio.csproj
    - src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj
    - src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj
    - src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj
    - src/AzureDesignStudio.Discovery/AzureDesignStudio.Discovery.csproj
    - src/AzureDesignStudio.Mcp/AzureDesignStudio.Mcp.csproj
    - src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj
    - src/AzureDesignStudio.SharedModels/AzureDesignStudio.SharedModels.csproj
    - src/AzureDesignStudio.SourceGeneration/AzureDesignStudio.SourceGeneration.csproj
    - src/AzureDesignStudio.Workspace/AzureDesignStudio.Workspace.csproj

key-decisions:
  - "MauiVersion lives in Directory.Build.props, not Directory.Packages.props: props is evaluated first and BundledVersions.targets sets the value only in the inner build"
  - "System.Security.Cryptography.Xml 10.0.13 is a direct reference in WASM and Desktop instead of CentralPackageTransitivePinningEnabled, which fails this tree with NU1109"
  - "Locked mode stays a CI flag; the repository sets only RestorePackagesWithLockFile"
  - "BUILD-01 is recorded as Pending: the pinned SDK is proven here, the clean-clone locked-restore proof belongs to 02-02"

patterns-established:
  - "Version census gate: declared PackageReference id set must equal the Directory.Packages.props id set"
  - "Prove the negative with counters: version-attribute count 0, NU1008 count 0, non-PRI263 warning count 0"

requirements-completed: []
---

# Phase 2 Plan 01: Reproducible Build and CI Baseline Summary

**SDK 10.0.401 and MAUI 10.0.110 pinned at the repository root, all 54 package versions centralized, the vulnerable crypto transitive raised to 10.0.13, and 11 generated lock files committed — with the solution still at 0 errors and 0 non-PRI263 warnings.**

## Performance

- **Duration:** ~11 min (clone 23:06 → final task commit 23:17, local time)
- **Started:** 2026-10-22T21:06Z
- **Completed:** 2026-10-22T21:17Z
- **Tasks:** 3
- **Files modified:** 26 (3 new root files, 10 csproj, 11 lock files, AGENTS.md, REQUIREMENTS.md)

## Accomplishments

- `dotnet --version` at the repository root prints `10.0.401`; `global.json` uses `rollForward: latestPatch`, `allowPrerelease: false`, and no `workloadVersion` (02-05 owns that line).
- `MauiVersion` resolves to `10.0.110` in the inner Desktop evaluation, so both central MAUI rows are real versions instead of empty strings.
- Every declared package version now lives exactly once in `Directory.Packages.props` (54 rows, 54 unique ids): 53 moved verbatim from the csproj files plus the measured crypto pin. No declared `PackageReference` carries `Version=`.
- `System.Security.Cryptography.Xml` resolves `10.0.13` as a direct reference in both the WASM and Desktop graphs; a full-graph audit at `NuGetAuditLevel=high`/`NuGetAuditMode=all` now reports exactly one high advisory — AutoMapper 12.0.1 (`GHSA-rvv3-g6hj-g44x`) — which BUILD-04 owns.
- Eleven generated `packages.lock.json` files (format `version: 2`) are committed; ten carry resolved entries with `contentHash`, and `AzureResources` is the expected empty-graph file.

## Evidence (command → result)

| Command | Result |
|---|---|
| `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` (baseline, before any change) | exit 0, **91 Warning(s), 0 Error(s)** in 3:25 |
| `dotnet --version` (repo root, after Task 1) | `10.0.401` |
| `dotnet msbuild src/AzureDesignStudio.Desktop/... -p:TargetFramework=net10.0-windows10.0.19041.0 -getProperty:MauiVersion` | `10.0.110` |
| `dotnet msbuild src/AzureDesignStudio.Desktop/... -getProperty:MauiVersion -p:ImportDirectoryBuildProps=false` | empty (the E4 outer-evaluation measurement; with the pin in place the outer value is `10.0.110` by inheritance) |
| `dotnet build ... -c Release -p:RunAOTCompilation=false` (Task 1) | exit 0, 91 warnings, 0 errors, `warning (CS\|NU\|IDE\|CA\|MSB)` count **0**, `PRI263` present |
| `grep -h 'PackageReference[^>]*Version=' src/*/*.csproj \| grep -v '<!--' \| wc -l` | `0` |
| Central census (declared ids vs central ids, `sort -u` diff) | `declared=54 central_unique=54 rows=54`, id sets identical |
| `dotnet list <WASM\|Desktop> package --include-transitive \| grep Cryptography.Xml` | `10.0.13 10.0.13` in both |
| `dotnet build ... -c Release` (Task 2) | exit 0, 91 warnings, 0 errors, `NU1008` count 0 |
| `dotnet restore src/AzureDesignStudio.sln` (lock generation) | exit 0, 11 `packages.lock.json` written |
| `dotnet restore src/AzureDesignStudio.Core --locked-mode` | exit 0 |
| `dotnet build ... -c Release` (Task 3) | exit 0, 91 warnings, 0 errors |
| `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure" --nologo` | **Passed! Failed: 0, Passed: 755, Total: 755** |
| `dotnet restore src/AzureDesignStudio.sln --force -p:NuGetAuditLevel=high -p:NuGetAuditMode=all -p:NoWarn=` | exit 0, single `NU1903` = AutoMapper 12.0.1 only; no lock file changed |
| `git ls-files '*packages.lock.json' \| wc -l` / `grep -l '"version": 2'` | `11` / `11` |
| `grep -rn 'CentralPackageTransitivePinning\|RestoreLockedMode' ...` | 0 hits |
| `grep -c '^- \[ \] \*\*BUILD-01\*\*'` and `grep -c '^| BUILD-01 | Phase 2 | Pending |'` | `1` / `1` |

Build logs: `/tmp/p201/baseline.log`, `/tmp/p201/task1.log`, `/tmp/p201/task2.log`, `/tmp/p201/task3-build.log`, `/tmp/p201/task3-test.log`, `/tmp/p201/audit.log` (inside this executor's sandbox; not part of the repository).

## Task Commits

1. **Task 1: one governed build (SDK + MauiVersion pins)** — `6d6a49e` (`build`)
2. **Task 2: centralize all package versions + crypto pin** — `4fefb4e` (`build`)
3. **Task 3: generate and commit the 11 lock files** — `93b17be` (`build`)

Plan metadata (this SUMMARY): see the final commit on `gsd/02-01`.

## Files Created/Modified

- `global.json` — SDK `10.0.401`, `rollForward: latestPatch`, `allowPrerelease: false`.
- `Directory.Build.props` — `MauiVersion` 10.0.110 and `RestorePackagesWithLockFile` true, each with its reason in a comment.
- `Directory.Packages.props` — `ManagePackageVersionsCentrally` plus 54 `PackageVersion` rows.
- `src/AzureDesignStudio/AzureDesignStudio.csproj` — 18 versions removed; `System.Security.Cryptography.Xml` added without a version.
- `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` — 6 versions removed; `System.Security.Cryptography.Xml` added without a version.
- `src/AzureDesignStudio.Core/…`, `.Core.Tests`, `.Discovery`, `.Mcp`, `.Server`, `.SharedModels`, `.SourceGeneration`, `.Workspace` — version attributes removed only; the five commented-out rows in `Core.csproj` and every `PrivateAssets`/`IncludeAssets` block untouched.
- `src/*/packages.lock.json` × 11 — restore output, committed unmodified.
- `AGENTS.md` — the "No Directory.Build.props — version per-project" claim replaced in all three places (table row, conventions bullet, testing bullet).
- `.planning/REQUIREMENTS.md` — `BUILD-01` returned to `[ ]` and `Pending`.

## Decisions Made

- `MauiVersion` in `Directory.Build.props` (evaluated before `Directory.Packages.props`), not in the central file — otherwise both MAUI rows evaluate empty.
- The crypto remediation is a central row plus two direct references; `CentralPackageTransitivePinningEnabled` was avoided because it fails this tree with `NU1109`.
- `RestorePackagesWithLockFile` is a repo property; locked mode is not, so local package edits do not require an override.
- `BUILD-01` stays `Pending`: this plan proves the pin and the locks, not the clean-clone restore.

## Deviations from Plan

**1. [Environment — dispatch defect] Executed in a clone inside the assigned worktree, not the assigned worktree itself**
- **Found during:** pre-flight
- **Issue:** The isolated worktree `C:\Users\jakub\.omp\wt\t541993458\m` contained the **Azure-backup** repository (HEAD `443b8f6`, branch `master`, no `.planning/`, no `src/`). `git worktree list` in `AzureDesignStudio` showed only the main checkout, so no correct worktree existed.
- **Fix:** Cloned the target repository into the worktree — `C:\Users\jakub\.omp\wt\t541993458\m\AzureDesignStudio` (from `C:/Users/jakub/Desktop/Projects/AzureDesignStudio`, base `578949c`) — and executed the plan on branch `gsd/02-01` there. Nothing outside this worktree was modified; the original checkout's uncommitted `.planning/STATE.md`/`state.json` and `.omp-*.json` files were not touched.
- **Orchestrator action required:** merge from `C:\Users\jakub\.omp\wt\t541993458\m\AzureDesignStudio` branch `gsd/02-01` (e.g. `git fetch C:/Users/jakub/.omp/wt/t541993458/m/AzureDesignStudio gsd/02-01`), not from the worktree root. `Main` was messaged about this before any edit.

**2. [Rule 1 - Bug] Invalid XML comment disabled `Directory.Build.props`**
- **Found during:** Task 3 (first lock-generating restore produced zero lock files)
- **Issue:** The comment added for `RestorePackagesWithLockFile` contained `--locked-mode`; `--` is illegal inside an XML comment, so MSBuild reported `MSB4024: The imported project file ... could not be loaded`, `RestorePackagesWithLockFile` evaluated to empty, and no lock files were written. The defective text was never committed (Task 2's commit predates it).
- **Fix:** Rewrote the comment without `--`; `-getProperty:RestorePackagesWithLockFile` then printed `true` and the plain restore emitted all 11 lock files.
- **Files modified:** `Directory.Build.props`
- **Verification:** `dotnet msbuild … -getProperty:RestorePackagesWithLockFile` → `true`; 11 lock files; locked restore of Core exit 0.
- **Committed in:** `93b17be` (Task 3 commit)

**3. [Process note, not a scope change] Line endings**
- The clone runs with `core.autocrlf=true`, so the csproj working copies are CRLF and git stores LF. All csproj edits were single-line attribute removals: `git diff --stat` shows 69 insertions / 69 deletions across the ten files, i.e. no whole-file rewrite. The three new root files and the two added reference blocks are LF in the index, which git normalizes on the next checkout.

**Total deviations:** 1 environment (blocking, reported), 1 auto-fixed bug (Rule 1), 1 process note.
**Impact on plan:** The code outcome matches the plan exactly; the environment deviation changes only *where* the commits live, which the orchestrator must account for when merging.

## Issues Encountered

- `dotnet restore` on a fully up-to-date tree can skip writing lock files; after the properties file was valid, a plain restore wrote them, so no `--force` was needed in the recorded run.
- A git identity is not configured in the clone; it was set locally (`Jakub Talaj <jacobeen@icloud.com>`, matching the repository's history) before the first commit.
- The executor did not update `.planning/STATE.md` or `.planning/ROADMAP.md` (single-writer rule); the orchestrator owns those after merge.

## Known Stubs

None.

## Threat Flags

None — the change surface is build configuration and generated lock files; no new endpoint, auth path, or trust boundary was introduced.

## Next Phase Readiness

- Plan 02-02 can now install `10.0.401` from `global.json`, relies on `<locked-mode>` as a CI flag, and re-marks `BUILD-01` with a clean-clone proof.
- Lock files exist for all 11 projects, so 02-02's negative proof (drift → `NU1004`) has a baseline.
- Unchanged and still owned elsewhere: no `TreatWarningsAsErrors`, `NuGetAuditLevel`, `.editorconfig`, `workloadVersion`, or workflow edits in this plan.

## Self-Check: PASSED

- Created files exist: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, 11 × `src/*/packages.lock.json` — all present and tracked.
- Commits exist on `gsd/02-01`: `6d6a49e`, `4fefb4e`, `93b17be` (3 commits, measured with `git rev-list --count 578949c..HEAD`).
- Working tree clean after the Task 3 commit (`git status --porcelain` empty).

---
*Phase: 02-reproducible-build-and-ci-baseline*
*Completed: 2026-10-22*
