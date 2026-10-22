---
phase: 02-reproducible-build-and-ci-baseline
plan: 03
subsystem: build
tags: [dotnet, msbuild, warnaserror, editorconfig, dotnet-format, whitespace]
requires:
  - phase: 02-reproducible-build-and-ci-baseline
    provides: root Directory.Build.props and Directory.Packages.props from 02-01, with 11 committed lock files
provides:
  - TreatWarningsAsErrors=true for all 11 projects, proven effective against a probe warning (error CS0169)
  - minimal root .editorconfig ([*.cs] whitespace keys only, no end_of_line/charset/style)
  - one whitespace-only normalisation commit that leaves the tree clean under dotnet format --verify-no-changes
affects: [02-05]
actuals:
  tokens: 61000
  tasks: 2
  commits: 2
  plan_head_before: b435bc3
  plan_head_after: f801ba0
tech-stack:
  added: []
  patterns:
    - "A gate is proven by making it fail: warnings-as-errors was tested against a probe project that imports the repo's own props"
    - "Formatting policy omits anything that would fight the checkout convention (line endings, charset)"
key-files:
  created:
    - .editorconfig
  modified:
    - Directory.Build.props
    - 443 tracked .cs files (whitespace only)
key-decisions:
  - "TreatWarningsAsErrors is the plain boolean, not an enumerated WarningsAsErrors list, and no MSBuild-level task-warning switch is set: the 91 PRI263 warnings are task warnings and must stay warnings"
  - ".editorconfig omits end_of_line and charset because either key turns a 38-file fix into a hundreds-of-files rewrite (LF index vs CRLF checkout; 102 BOM'd files)"
  - "The normalisation commits every file the formatter actually rewrites, including the 430 that only gain a final newline, so a later `dotnet format whitespace` is a no-op instead of a surprise 430-file diff"
metrics:
  duration: 24 min
  completed: 2026-10-22
status: complete
---

# Phase 02 Plan 03: Warnings-as-Errors and Whitespace Contract Summary

Compiler warnings became build failures across all 11 projects (a measured no-op today, proven to bite), and a minimal `.editorconfig` plus one whitespace-only commit made `dotnet format --verify-no-changes` exit 0 on the whole solution.

## Commits

| Task | Commit | Subject | Paths |
|------|--------|---------|-------|
| 1 | `47816c8` | `build(02-03): treat warnings as errors (compiler warnings only)` | `Directory.Build.props` (1 file, +6) |
| 2 | `f801ba0` | `style(02-03): normalise whitespace under a minimal .editorconfig` | `.editorconfig` + 443 `.cs` files (444 files, +723/−698) |

Measured: `git rev-list --count b435bc3..HEAD` = 2.

## What changed

### Task 1 — `Directory.Build.props`

```xml
  <PropertyGroup>
    <MauiVersion>10.0.110</MauiVersion>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <!-- Measured basis: a clean Release rebuild of all 11 projects emits zero compiler, NuGet,
         analyzer, or MSBuild warnings, so this is a no-op today that keeps it that way. The 91
         remaining warnings are WinAppSDK resource-packaging task warnings (PRI263); this is a
         Roslyn switch, so it does not promote them. The MSBuild-level task-warning switch is
         deliberately left unset: it would turn that resource-packaging quirk into a failure. -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
```

No `WarningsAsErrors`, `WarningsNotAsErrors`, `NoWarn`, `MSBuildTreatWarningsAsErrors`,
`CodeAnalysisTreatWarningsAsErrors`, or `EnforceCodeStyleInBuild` was added anywhere.

### Task 2 — `.editorconfig`

```ini
root = true

[*.cs]
# Deliberately absent, each for a measured reason:
#   end_of_line — the index is LF for all 820 source files while a Windows checkout materialises
#                 519 of them as CRLF; pinning either value would rewrite hundreds of files.
#   charset     — 102 of 820 files carry a UTF-8 BOM and the rest do not; any value forces a
#                 wholesale rewrite of the majority.
indent_style = space
indent_size = 4
tab_width = 4
trim_trailing_whitespace = true
insert_final_newline = true
```

## Evidence

### Task 1 — the gate is effective, not merely present

Probe project (`net10.0`, one class with an unused private field) built with
`-p:DirectoryBuildPropsPath=<repo>/Directory.Build.props`:

```text
C:\Users\jakub\AppData\Local\Temp\tmp.ETSGE2roJp\Probe.cs(4,17): error CS0169: The field 'C.unusedField' is never used [...\Probe.csproj]
Build FAILED.
RC=1
```

The probe directory was deleted afterwards; nothing was written into the repository.

Property resolves for a real project:

```text
$ dotnet msbuild src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj -getProperty:TreatWarningsAsErrors
true
```

Clean Release rebuild of all 11 projects with the property on
(`dotnet build src/AzureDesignStudio.sln -c Release --no-incremental -p:RunAOTCompilation=false`):

```text
Build succeeded.
    91 Warning(s)
    0 Error(s)
warning-lines=182          (each warning appears twice in the console log)
pri263-warning-lines=182
non-pri263-warning-lines=0
error-lines=0
```

So: zero `CS`/`NU`/`IDE`/`CA`/`MSB` warning lines, and the 91 `PRI263` task warnings are still
warnings — `MSBuildTreatWarningsAsErrors` is not set.

### Task 2 — the normalisation is real and whitespace-only

Pre-normalisation census of the same command the plan 02-05 gate will run
(`dotnet format whitespace src/AzureDesignStudio.sln --verify-no-changes --no-restore`, measured in a
pristine clone of `b435bc3`): **exit 2, 333 `error WHITESPACE` diagnostics in 41 files** — Core 152,
Desktop 45, Server 44, Core.Tests 43, Discovery 23, WASM 17, Workspace 4, SourceGeneration 4,
AzureResources 1. (The research baseline was 316/38; the count is an observation, and the per-project
split differs slightly from E13.)

After the commit:

```text
$ dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore
FMT_RC=0

$ dotnet format whitespace src/AzureDesignStudio.sln     # re-run of the apply pass
APPLY_RC=0
dirty-after-apply=0                                      # idempotent

$ dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false
Build succeeded.
    91 Warning(s)
    0 Error(s)

$ dotnet test src/AzureDesignStudio.Core.Tests -c Release --no-build --filter "Category!=LiveAzure"
Passed!  - Failed: 0, Passed: 755, Skipped: 0, Total: 755, Duration: 2 s - AzureDesignStudio.Core.Tests.dll (net10.0)
```

Commit shape:

```text
cs-files=443     all-paths=444    non-cs=1 (.editorconfig)
eof-newline-only=430   other-whitespace=13
git show -w --name-only f801ba0 -- '*.cs'  ->  6 files
```

Stronger whitespace-only proof than `git diff -w` (which is *not* empty — see Deviations): for every
one of the 443 changed files, the old blob (`git show f801ba0^:<path>`) and the new file normalised by
deleting all BOM bytes and all whitespace are byte-identical:

```text
total=443 content-diffs=0
```

The six files where `git show -w` still prints output are whitespace changes that git's `-w`
heuristic cannot suppress: `BicepParamsTextTests.cs`, `ReconcileEngineTests.cs`,
`TopologyPositions.cs`, `TopMenu.razor.cs` (collection/object initialiser elements split onto their
own lines), `FunctionAppModel.cs` (`};` → `}` + newline + `;`), and `StorageAccountModel.cs` (a
doubled UTF-8 BOM collapsed to one). Each one has identical token content under the normalisation
above.

## Deviations from Plan

**1. [Plan premise false] The normalisation touched 443 `.cs` files, not 38.**

- **Found during:** Task 2, first `dotnet format whitespace` run.
- **Issue:** the plan (and E12) sized the change from `--verify-no-changes` *diagnostics* (333 in 41
  files here). The formatter's apply pass additionally appends the missing final newline to files the
  verifier never reports — 430 of the 443 changed files differ from `HEAD` by ≤2 bytes and only gain
  the trailing newline.
- **Fix:** the full formatter output was committed, so a later `dotnet format whitespace` run is a
  no-op (`dirty-after-apply=0`) instead of silently dirtying 430 files. The verifier still exits 0
  either way; committing the subset would have left the declared `insert_final_newline` contract
  unenforced on disk.

**2. [Plan premise false] `git show -w` is not empty for the `.cs` paths (6 files).**

- **Found during:** Task 2, pre-stage diff audit.
- **Issue:** git's `-w` ignores whitespace *within* a line, but not a newline inserted between
  tokens (`};` → `}\n;`), nor a BOM-byte change. The 6 files listed above are therefore visible to
  `-w` while being token-identical.
- **Fix:** the trust-boundary requirement (T-02-08: no semantic change rides along) is satisfied by a
  stronger check — BOM- and whitespace-stripped content equality for all 443 files
  (`content-diffs=0`). No file was restored or dropped to make a weaker check pass.

**3. [Environment] `NU1403` on `Microsoft.NET.Sdk.WebAssembly.Pack 10.0.12` during rebuild.**

- **Found during:** the post-commit Release build.
- **Issue:** the committed lock file pins the nuget.org content hash (`MIeBVeWk…`), but the SDK's own
  `C:\Program Files\dotnet\library-packs` source ships a *different* build of version 10.0.12
  (`contentHash DbQBhmNA…`). Once the global-package cache no longer held the nuget.org copy, restore
  resolved the `library-packs` copy and failed hash validation
  (`error NU1403: Package content hash validation failed for Microsoft.NET.Sdk.WebAssembly.Pack.10.0.12`).
- **Fix (machine-local, not committed):** re-seeded the global package cache from the nuget.org
  `.nupkg` (`dotnet restore … --source <local folder feed> -p:DisableImplicitLibraryPacksFolder=true`),
  which restored `contentHash MIeBVeWk…`; the build then succeeded and **no lock file changed**
  (`git status --short` empty after restore).
- **Flagged for the orchestrator:** this is a reproducibility hazard for plan 02-02/02-05, not a
  defect introduced here — the same version exists in two sources with different content, and NuGet
  may pick the local `library-packs` copy. See "Known Issues".

**4. [Commit-message correction] The Task 2 commit body says "402 of them only gain the missing final
newline"; the measured number is 430.** The figure was an estimate written before the classification
was run; history was not rewritten, so the SUMMARY is the corrected record (`eof-newline-only=430`,
`other-whitespace=13`).

## Known Issues

- `Microsoft.NET.Sdk.WebAssembly.Pack 10.0.12` resolves to different content depending on whether it
  comes from nuget.org or the SDK's `library-packs` folder, and the committed lock pins the nuget.org
  hash. A machine (or runner) whose global package cache lacks the package can fail `NU1403` before
  compiling. Owner: the plan that owns lock files / the CI restore steps (02-02, 02-05). This plan
  did not change any package source configuration (out of `files_modified`).

## Threat Flags

None. No new network endpoint, auth path, file-access pattern, or schema change was introduced; the
only process that rewrites source is `dotnet format whitespace`, and its output is proven
token-identical.

## Self-Check: PASSED

- `Directory.Build.props` contains `TreatWarningsAsErrors=true` and none of the prohibited properties — PASSED.
- Probe importing the repo props fails with `error CS0169` — PASSED.
- Release solution build: 0 errors, 0 non-`PRI263` warning lines, 91 `PRI263` warnings — PASSED.
- Offline suite `Passed: 755`, `Failed: 0` — PASSED.
- `.editorconfig` has `root = true`, only `[*.cs]` whitespace keys, no `end_of_line`/`charset`/style/IDE/Razor — PASSED.
- `dotnet format … --verify-no-changes --no-restore` exits 0; `dotnet format whitespace` re-run leaves the tree clean — PASSED.
- Task 2 commit contains `.editorconfig` + only `.cs` files; all 443 `.cs` files token-identical after whitespace/BOM stripping — PASSED.
- Commits exist: `47816c8`, `f801ba0` — PASSED.
