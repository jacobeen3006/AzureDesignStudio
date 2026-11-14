---
phase: 03-server-foundation-and-integration-harness
plan: 01
subsystem: infra
tags: [nuget-audit, automapper, mvc-testing, ef-core-sqlite, lock-files, xunit]

requires:
  - phase: 02-build-baseline
    provides: single dated AutoMapper NuGetAuditSuppress, expiry target, lock files per project
provides:
  - restore, build and the 134-test Server.Tests suite runnable again at HEAD (no NU1903)
  - central Microsoft.AspNetCore.Mvc.Testing 10.0.15 row, referenced by Server.Tests
  - Server selects Database:Provider=Sqlite (Microsoft.EntityFrameworkCore.Sqlite referenced) and references Core directly
  - regenerated Server and Server.Tests packages.lock.json
affects: [03-02, 03-03, 03-04, 03-05, 03-06, 08-automapper-replacement]

actuals:
  tokens: 2085
  tasks: 2
  commits: 2
plan_head_before: 2f0f8b1911f60b13bcd8be309e9c8eba9e7cb369
plan_head_after: d8dc9f6dcb75ff3e8f0920204ff0deb56d1be6fa
commits: 2

tech-stack:
  added: [Microsoft.AspNetCore.Mvc.Testing 10.0.15, Microsoft.EntityFrameworkCore.Sqlite (Server reference, existing central row 10.0.12)]
  patterns: ["one dated expiry property covers every AutoMapper 12.0.1 advisory suppression"]

key-files:
  created: []
  modified:
    - Directory.Build.props
    - Directory.Build.targets
    - Directory.Packages.props
    - src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj
    - src/AzureDesignStudio.Server.Tests/AzureDesignStudio.Server.Tests.csproj
    - src/AzureDesignStudio.Server/packages.lock.json
    - src/AzureDesignStudio.Server.Tests/packages.lock.json

key-decisions:
  - "Suppress GHSA-gpwp-5797-mjff and GHSA-6vhm-j9h7-f9gh (AutoMapper 12.0.1, High, published 2026-11-13) under the existing 2027-01-31 expiry; the allowlist stays AutoMapper-only and the expiry is not widened"
  - "Microsoft.AspNetCore.Mvc.Testing is pinned at 10.0.15 (the train of every other ASP.NET Core row), not the roadmap's stale 10.0.13"

requirements-completed: [TEST-01]

coverage:
  - id: D1
    description: "Restore of Server.Tests no longer fails with NU1903; allowlist covers three AutoMapper 12.0.1 advisories under one unchanged expiry"
    requirement: "TEST-01"
    verification:
      - kind: other
        ref: "dotnet restore src/AzureDesignStudio.Server.Tests --locked-mode (only the tolerated NU1004 drift for src/AzureDesignStudio/AzureDesignStudio.csproj remains)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Mvc.Testing, EF Core Sqlite and the Server-to-Core reference wired centrally with consistent Server and Server.Tests locks; the 134 existing tests still pass in the CI shape"
    requirement: "TEST-01"
    verification:
      - kind: unit
        ref: "dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false (Passed: 134, Failed: 0)"
        status: pass
    human_judgment: false

duration: 8min
completed: 2026-11-14
status: complete
---

# Phase 3 Plan 01: Restore-to-green and harness dependencies Summary

**Restore, build and the 134-test server suite run again after adding the two new AutoMapper 12.0.1 advisories to the single dated exception, and Server.Tests now has Mvc.Testing 10.0.15 with Server able to select Sqlite and reference Core directly.**

## Performance

- **Duration:** 8 min
- **Started:** 2026-11-14T10:13Z
- **Completed:** 2026-11-14T10:21Z
- **Tasks:** 2
- **Files modified:** 7

## Accomplishments

- Task 1 (tracer): two `NuGetAuditSuppress` items added (GHSA-gpwp-5797-mjff, GHSA-6vhm-j9h7-f9gh, both High, published 2026-11-13) beside GHSA-rvv3-g6hj-g44x. All three share `AdsAuditSuppressionAutoMapperExpires` (2027-01-31), which is unchanged. The comment now names all three ids and notes the suppression is advisory-scoped. `AdsCheckAuditSuppressionExpiry` names all three ids. Restore, Release build and the 134 existing tests pass.
- Task 2: `Microsoft.AspNetCore.Mvc.Testing` 10.0.15 added as a central row and referenced version-less by Server.Tests; `Microsoft.EntityFrameworkCore.Sqlite` and `AzureDesignStudio.Core` referenced by Server. Only the Server and Server.Tests locks changed, and both diffs are pure additions (Server: Sqlite Direct plus its transitive set; Server.Tests: Mvc.Testing Direct, TestHost and the Sqlite set Transitive).
- Tracer gate: the tracer's `<verify>` (locked restore filter and the full test run) was green end to end before expansion, so Task 2 started.

## Task Commits

1. **Task 1: restore-to-green tracer** - `8895f19` (build)
2. **Task 2: Mvc.Testing, Sqlite and Core reference; lock regeneration** - `d8dc9f6` (build)

## Files Created/Modified

- `Directory.Build.props` - two more audit suppressions, rewritten comment
- `Directory.Build.targets` - expiry error names three advisories
- `Directory.Packages.props` - `Microsoft.AspNetCore.Mvc.Testing` 10.0.15
- `src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj` - Sqlite package, Core project reference
- `src/AzureDesignStudio.Server.Tests/AzureDesignStudio.Server.Tests.csproj` - Mvc.Testing reference
- `src/AzureDesignStudio.Server/packages.lock.json`, `src/AzureDesignStudio.Server.Tests/packages.lock.json` - regenerated

## Decisions Made

See `key-decisions` above. Both follow the plan and research.

## Deviations from Plan

None - plan executed exactly as written.

Environment notes (not deviations): the machine has only SDK 10.0.403 (pin is 10.0.401), so every non-locked restore or test run rewrote `src/AzureDesignStudio/packages.lock.json` (`Microsoft.NET.ILLink.Tasks` and `Microsoft.AspNetCore.App.Internal.Assets` 10.0.12 -> 10.0.14). It was reverted with `git checkout -- <file>` each time and never staged. The locked restore therefore still prints the one tolerated `NU1004` pair for `src/AzureDesignStudio/AzureDesignStudio.csproj`; no NU1903 and no NU1004 for Server or Server.Tests. The Server and Server.Tests locks were generated under 10.0.403 (10.0.401 is not installed); their diffs contain only the entries named in the plan, and CI's locked restore on the pinned SDK remains the arbiter.

## Issues Encountered

None.

## Known Stubs

None.

## Threat Flags

None. The only new surface is the audit allowlist (T-03-01, mitigated: AutoMapper-only, one unchanged expiry) and two Microsoft-authored packages (T-03-SC, audited in research).

## Next Phase Readiness

Plan 03-02 onward can build and run tests, use `WebApplicationFactory` from Server.Tests, and select `Database:Provider=Sqlite` in the server.

## Self-Check: PASSED

- All seven plan paths modified and committed (`git show --name-only` of 8895f19 and d8dc9f6 list exactly the plan's paths).
- Commits 8895f19 and d8dc9f6 are ancestors of HEAD; `git rev-list --count 2f0f8b1..HEAD` is 2 before this docs commit.
- Acceptance greps: 3 `NuGetAuditSuppress Include`; both new ids present in props and targets; expiry `2027-01-31` count 1; Mvc.Testing central row count 1 and version-less in the csproj; Sqlite and Core references count 1 each; lock entries present; lock changes between the two commits limited to the Server and Server.Tests locks; `git status --porcelain -- '*packages.lock.json'` is empty after the final drift revert.
- Plan verification: locked restore shows no NU1903/NU1004 for Server or Server.Tests; tests `Passed! Failed: 0, Passed: 134, Total: 134`.
