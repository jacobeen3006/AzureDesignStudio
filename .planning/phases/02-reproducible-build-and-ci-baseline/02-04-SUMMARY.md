---
phase: 02-reproducible-build-and-ci-baseline
plan: 04
subsystem: build
tags: [dotnet, msbuild, nuget-audit, nugetauditsuppress, supply-chain, vulnerability-gate]
requires:
  - phase: 02-reproducible-build-and-ci-baseline
    provides: root Directory.Build.props with TreatWarningsAsErrors (02-03), Directory.Packages.props with the System.Security.Cryptography.Xml 10.0.13 pin (02-01)
provides:
  - NuGetAuditLevel=high and NuGetAuditMode=all in Directory.Build.props, promoted to errors by TreatWarningsAsErrors
  - exactly one NuGetAuditSuppress (GHSA-rvv3-g6hj-g44x / AutoMapper 12.0.1) with AdsAuditSuppressionAutoMapperExpires=2027-01-31
  - Directory.Build.targets AdsCheckAuditSuppressionExpiry on Restore;Build;PrepareForBuild
  - both per-project NoWarn NU1903 blocks deleted (WASM and Core)
affects: [08-auto-mapper-replacement]
actuals:
  tokens: 1630
  tasks: 2
  commits: 2
  plan_head_before: e222e67
  plan_head_after: b563a93
tech-stack:
  added: []
  patterns:
    - "A suppression with an expiry: NuGet has no expiry of its own, so the date is enforced by an MSBuild target on restore and build"
    - "Date comparison via System.String.Compare into a property, because direct date comparison is MSB4086"
key-files:
  created:
    - Directory.Build.targets
  modified:
    - Directory.Build.props
    - src/AzureDesignStudio/AzureDesignStudio.csproj
    - src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj
    - .planning/REQUIREMENTS.md
key-decisions:
  - "High/Critical only (NuGetAuditLevel=high): lower severities are not emitted at all, so a moderate advisory can never turn every developer's local build red through TreatWarningsAsErrors"
  - "NuGetAuditMode=all is set repo-wide because the SDK only defaults to all for .NETCoreApp 10.0+; the netstandard2.0 SourceGeneration project would otherwise audit direct dependencies only"
  - "The exception is advisory-scoped rather than package-scoped (NuGet's semantics), so the comment beside the item records that any other package sharing GHSA-rvv3-g6hj-g44x would also be silenced"
  - "The expiry message names the date, the advisory and the replacement obligation, so the failing build explains itself without the planning docs"
metrics:
  duration: 25 min
  completed: 2026-10-23
status: complete
---

# Phase 02 Plan 04: Audit-as-Error with One Dated Exception Summary

High/Critical NuGet advisories now fail restore and build in every project's full graph; the only way the tree stays green is the single AutoMapper exception, which a build-enforced target retires on 2027-01-31.

## Commits

| Task | Commit | Subject | Paths |
|------|--------|---------|-------|
| 1 | `2564a30` | `build(02-04): fail on High/Critical advisories with one dated AutoMapper exception` | `Directory.Build.props`, `Directory.Build.targets`, `src/AzureDesignStudio/AzureDesignStudio.csproj`, `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj` (4 files, +42/−10) |
| 2 | `b563a93` | `docs(02-04): mark BUILD-04 complete after the four audit probes` | `.planning/REQUIREMENTS.md` (1 file, +2/−2) |

Measured: `git rev-list --count e222e67..HEAD` = 2.

## What changed

### `Directory.Build.props` — the gate and its single exception

```xml
  <PropertyGroup>
    <NuGetAuditLevel>high</NuGetAuditLevel>
    <NuGetAuditMode>all</NuGetAuditMode>
    <AdsAuditSuppressionAutoMapperExpires>2027-01-31</AdsAuditSuppressionAutoMapperExpires>
  </PropertyGroup>
  <ItemGroup>
    <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-rvv3-g6hj-g44x" />
  </ItemGroup>
```

Each element carries the comment the plan specifies: why only High/Critical are reported (so only they can fail), the measurement that the SDK's `all` default applies only to `.NETCoreApp` 10.0+ (hence setting it repo-wide so the `netstandard2.0` SourceGeneration project audits transitives too), what the exception covers (AutoMapper 12.0.1 DoS via uncontrolled recursion; 13+ needs a commercial licence key; Phase 8 replaces it), that the suppression is advisory-scoped rather than package-scoped, and that `AdsCheckAuditSuppressionExpiry` enforces the date.

No `NU19xx` suppression remains under `src/`: the deletion rationale lives in the props comment block, not in a csproj, so the gate `git grep -n 'NU1903' -- src` stays empty.

### `Directory.Build.targets` — NEW

```xml
<Project>
  <PropertyGroup>
    <_AdsToday>$([System.DateTime]::UtcNow.ToString("yyyy-MM-dd"))</_AdsToday>
    <_AdsExpiryCompare>$([System.String]::Compare('$(AdsAuditSuppressionAutoMapperExpires)', '$(_AdsToday)'))</_AdsExpiryCompare>
  </PropertyGroup>
  <Target Name="AdsCheckAuditSuppressionExpiry" BeforeTargets="Restore;Build;PrepareForBuild">
    <Error Condition="$(_AdsExpiryCompare) &lt; 0"
           Text="AutoMapper audit suppression expired on $(AdsAuditSuppressionAutoMapperExpires) (advisory GHSA-rvv3-g6hj-g44x); replace the package before this date. Today is $(_AdsToday)." />
  </Target>
</Project>
```

No `Sdk` attribute; the comparison is the `System.String.Compare` indirection (Pitfall 7). The target shape copies the repository's inline `DropForeignThreadingAnalyzers` idiom: comment first, `BeforeTargets`, one `<Error>`.

### The two deleted blocks

- `src/AzureDesignStudio/AzureDesignStudio.csproj` — the whole `<PropertyGroup>` holding the AutoMapper comment and `<NoWarn>$(NoWarn);NU1903</NoWarn>` (was lines 18–22).
- `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj` — the whole tab-indented `<PropertyGroup>` (was lines 10–13).

Both projects consume AutoMapper; leaving either would have made the repo-wide gate bypassable in exactly the two places that matter (Pitfall 9, T-02-12).

## Gate evidence (Task 1)

| Check | Command | Result |
|-------|---------|--------|
| No audit suppression in tracked source | `git grep -n 'NU1903' -- src` | no output, exit 1 |
| Full restore | `dotnet restore src/AzureDesignStudio.sln` | exit 0; 11 projects restored; no `NU190x` line |
| Release build | `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` | exit 0, `Build succeeded.` 0 Errors, 91 Warning(s) — all 91 are `PRI263` resource-packaging task warnings (182 lines = 91 warnings echoed twice); `grep -cE 'warning (CS\|NU\|IDE\|CA\|MSB)[0-9]+'` = **0** |
| Locked restore | `dotnet restore src/AzureDesignStudio.Core --locked-mode` | exit 0 |
| Format gate | `dotnet format src/AzureDesignStudio.sln --verify-no-changes` | exit 0 |
| Commit shape | `git show --name-only --pretty=format: HEAD` | exactly the four expected paths |

Resolved `System.Security.Cryptography.Xml` (from the committed lock files, so the crypto pin still holds and needs no suppression):

| Project | TFM | Type | Resolved |
|---------|-----|------|----------|
| `src/AzureDesignStudio` | `net10.0` | Direct | **10.0.13** |
| `src/AzureDesignStudio.Desktop` | `net10.0-windows10.0.19041` | Direct | **10.0.13** |

## Probes (Task 2) — verbatim

### Probe A — the gate blocks

```
dotnet restore src/AzureDesignStudio.Core --locked-mode \
  -p:DirectoryBuildPropsPath='...\probes\props.xml' \
  -p:AdsAuditSuppressionAutoMapperExpires=2027-01-31 -p:NoWarn=
```
(the temp props carried only `TreatWarningsAsErrors=true`, `NuGetAuditLevel=high`, `NuGetAuditMode=all` — no suppression item)

Exit code **1**:

```
C:\...\src\AzureDesignStudio.Core\AzureDesignStudio.Core.csproj : error NU1903: Warning As Error: Package 'AutoMapper' 12.0.1 has a known high severity vulnerability, https://github.com/advisories/GHSA-rvv3-g6hj-g44x
  Failed to restore C:\...\src\AzureDesignStudio.Core\AzureDesignStudio.Core.csproj (in 149 ms).
```

No `expired on` in the log (grep found none) — the expiry property was satisfied, so the audit was the only failing mechanism. No `MSB4086`.

### Probe B — the exception is the only cover

```
dotnet restore src/AzureDesignStudio.Core --locked-mode -p:NoWarn=
```

Exit code **0**:

```
  Determining projects to restore...
  Restored ...\src\AzureDesignStudio.AzureResources\... (in 81 ms).
  Restored ...\src\AzureDesignStudio.SourceGeneration\... (in 144 ms).
  Restored ...\src\AzureDesignStudio.Core\... (in 144 ms).
```

`grep -c NU1903` = 0. With Probe A, this shows the single suppression item — not an inherited `NoWarn` — is doing the work.

### Probe C — the expiry fires

```
dotnet build src/AzureDesignStudio.Core -c Release -p:AdsAuditSuppressionAutoMapperExpires=2020-01-01
```

Exit code **1**:

```
Directory.Build.targets(14,5): error : AutoMapper audit suppression expired on 2020-01-01 (advisory GHSA-rvv3-g6hj-g44x); replace the package before this date. Today is 2026-10-22. [C:\...\src\AzureDesignStudio.Core\AzureDesignStudio.Core.csproj]

Build FAILED.
    0 Warning(s)
    1 Error(s)
```

The target fired during restore evaluation (the build never reached compile), exactly as `BeforeTargets="Restore;Build;PrepareForBuild"` intends. Then, with no override:

```
dotnet build src/AzureDesignStudio.Core -c Release
```

Exit code **0**:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

`MSB4086` count across all four logs: **0**.

### Probe D — the level and mode are live

```
$ dotnet msbuild src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj -getProperty:NuGetAuditLevel
high
$ dotnet msbuild src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj -getProperty:NuGetAuditMode
all
$ grep -cE '^[[:space:]]*<NuGetAuditSuppress Include=' Directory.Build.props
1
```

`grep -rn 'NuGetAuditSuppress'` over the tracked build files returns that one line only, and `git grep -c 'NoWarn.*NU19' -- src` returns nothing.

## Requirements

`BUILD-04` is `[x]` / `Complete` in both locations. The emitted diff:

```diff
-- [ ] **BUILD-04**: CI fails on High/Critical vulnerable packages, with one dated AutoMapper allowlist entry that expires.
+- [x] **BUILD-04**: CI fails on High/Critical vulnerable packages, with one dated AutoMapper allowlist entry that expires.
-| BUILD-04 | Phase 2 | Pending |
+| BUILD-04 | Phase 2 | Complete |
```

## Deviations from Plan

None — plan executed as written. Two environment notes, not deviations:

- The isolated worktree was a checkout of the session repository, so the project was cloned into `./work` and `gsd/02-04` branched from `e222e67` as the shared contract directs.
- The clone had no committer identity; `git config user.name/user.email` were set locally to the repository's existing author (`Jakub Talaj <jacobeen@icloud.com>`) so the commits join the history cleanly.

## Threat register outcome

| Threat ID | Disposition | Evidence |
|-----------|-------------|----------|
| T-02-11 (single advisory-scoped exemption) | mitigate — applied | exactly one `NuGetAuditSuppress`, Probe A shows the gate fails without it |
| T-02-12 (per-project `NoWarn` bypass) | mitigate — applied | both blocks deleted in the same commit; `git grep -n 'NU1903' -- src` empty; Probe B with `-p:NoWarn=` proves no inherited suppression covers the advisory |
| T-02-13 (allowlist expiry) | mitigate — applied | `AdsCheckAuditSuppressionExpiry` proven by Probe C |
| T-02-SC (package installs) | accept | no package operation in this plan |

## Known Stubs

None.

## Self-Check: PASSED

- `Directory.Build.props`, `Directory.Build.targets`, both csproj files and `.planning/REQUIREMENTS.md` all present in the working tree and committed.
- `git merge-base --is-ancestor 2564a30 HEAD` and `git merge-base --is-ancestor b563a93 HEAD` both succeed.
- Working tree clean (`git status --porcelain --untracked-files=no` prints nothing).
