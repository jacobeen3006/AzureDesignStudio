# Phase 2: Reproducible Build and CI Baseline - Context

**Gathered:** 2026-10-31
**Status:** Ready for planning (plans 02-01..02-07 already exist; 02-01 executed, 02-02 committed)

<domain>
## Phase Boundary

A clean clone builds from pinned, locked, centralized inputs (SDK 10.0.401 `latestPatch`, `Directory.Packages.props`, per-project `packages.lock.json`), and the Windows desktop host fails a pull request before merge instead of failing at tag time. Requirements BUILD-01..BUILD-05. No MSIX packing in this phase.

</domain>

<decisions>
## Implementation Decisions

### Claude's Discretion
All implementation choices are at Claude's discretion - pure infrastructure phase (build/CI/formatting/audit gates; no user-facing behavior). Discuss-phase was skipped deliberately; the binding inputs are the ROADMAP Phase 2 implementation note, BUILD-01..BUILD-05 / SC#1..SC#5, the PROJECT.md hard constraints, and where measurement contradicts the roadmap note, the measured value in 02-RESEARCH.md wins.

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `global.json`, `Directory.Build.props` (MauiVersion pin, `RestorePackagesWithLockFile`), `Directory.Packages.props`, 11 `packages.lock.json` (landed by 02-01).
- `.github/workflows/build.yml` (Linux build+test+CodeQL), `publish.yml`, `codeql-analysis.yml`.

### Established Patterns
- Locked restore is a CI flag, never a repository property; CI guards it with `git diff --exit-code -- '*packages.lock.json'`.
- Desktop `WindowsPackageType=None` stays the csproj default until Phase 11.

### Integration Points
- `build.yml` Linux job (fast gate) plus new `windows-latest` Desktop job (non-blocking until two green runs, BUILD-05); duration recorded in `02-DESKTOP-RUNS.md`.

</code_context>

<specifics>
## Specific Ideas

No specific requirements - infrastructure phase.

</specifics>

<deferred>
## Deferred Ideas

None - discussion stayed within phase scope.

</deferred>
