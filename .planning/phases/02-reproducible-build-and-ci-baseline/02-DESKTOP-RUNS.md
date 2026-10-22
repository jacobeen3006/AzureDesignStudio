---
phase: 02-reproducible-build-and-ci-baseline
created: 2026-10-21
status: awaiting-runs
---

# Desktop CI runs — SC#2 and SC#5 evidence ledger

The `Desktop (windows-latest)` job in `.github/workflows/build.yml` is non-blocking
(`continue-on-error: true`) until two consecutive green runs are recorded below. Every
row must cite a run URL and a duration; a number without a run is not evidence. The
local baselines exist only for comparison and are labelled as such — they never count
toward the flip.

## Local baseline

Both rows are measurements on the development machine, not CI executions. They are the
comparison point for the runner's duration; the job's own duration arrives in
`desktop-build-metrics.json` (and independently from the Actions API `started_at` /
`completed_at`).

| Measurement | Command | Result | Status |
|-------------|---------|--------|--------|
| Desktop Release build, AOT disabled | `dotnet build src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj -c Release --no-incremental -p:RunAOTCompilation=false` | 74 s wall, 91 Warning(s), 0 Error(s) | ✓ local — not a CI run |
| Desktop Release build, AOT at its default (on) | `dotnet build src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj -c Release --no-incremental -p:TreatWarningsAsErrors=true` | 466 s wall, 91 Warning(s), 0 Error(s) | ✓ local — not a CI run |

The 74 s figure is why the job disables AOT: AOT is a publish concern, re-verified in
Phase 11, and it multiplies this job's cost by roughly six. The 91 warnings are `PRI263`
tool warnings from resource packaging; they are not promoted by `TreatWarningsAsErrors`.

Re-measured on 2026-10-23 while executing plan 02-05, the AOT-disabled command above
returned 176 s wall on a cold tree (91 Warning(s), 0 Error(s), restore already complete).
The 74 s figure is the E10 research measurement; the difference is machine state and build
warmth, not a code change. Both are local measurements — neither is a CI number, and the
`## Runs` rows below take their seconds from the run's own artefact.

## Runs

One row per `Desktop (windows-latest)` execution. `Seconds` comes from
`desktop-build-metrics.json` and is cross-checked against the Actions API
`started_at`/`completed_at`. Leave a row out rather than estimate it.

| Run | Date (UTC) | Commit | Job | continue-on-error | Conclusion | Seconds | Source | URL |
|-----|------------|--------|-----|-------------------|------------|---------|--------|-----|

## SC#2 — deliberate compile error

A throwaway branch carries one unambiguous compile error; the PR is opened and closed
without merging, so `main` never contains the error. Fill every field from the run itself.

- Branch: `<pending — ci-proof/desktop-compile-error>`
- PR (closed, unmerged): `<pending>`
- Run URL: `<pending>`
- Failing job: `<pending — Desktop (windows-latest)>`
- Failing error: `<pending — CS#### in src/AzureDesignStudio.Desktop/MauiProgram.cs>`
- Guard measurement `git rev-list --count origin/main..main`: `<pending — expected 0>`

## SC#5 — flip to blocking

The flip happens only after the two green runs above are recorded. Plan 02-06 records the
runs; plan 02-07 removes `continue-on-error`, requires the check, and fills the three
labelled lines below — they are parsed verbatim, so keep the labels at the start of a line.

- Green run 1: `<pending — run URL, date, seconds>`
- Green run 2: `<pending — run URL, date, seconds>`

Flip commit: <pending — plan 02-07>
Required check: Desktop (windows-latest)
Proof: <pending — plan 02-07 records the `gh api repos/{owner}/{repo}/branches/main/protection` excerpt or the settings transcript here>

### Required-check evidence

Nothing recorded yet. Paste the `gh api` JSON excerpt or the branch-protection settings
transcript after the check is required; a screenshot path alone is not machine-checkable.
