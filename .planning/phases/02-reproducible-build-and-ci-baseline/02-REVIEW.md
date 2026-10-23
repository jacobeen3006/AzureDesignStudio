---
phase: 02-reproducible-build-and-ci-baseline
reviewed: 2026-10-23T00:06:00Z
depth: deep
range: 578949c..4868af4
files_reviewed: 43
findings:
  critical: 1
  high: 0
  medium: 5
  low: 5
  total: 11
verdict: BLOCK
---

# Phase 02 — Code Review: Reproducible Build and CI Baseline

**Verdict: BLOCK** — the change set makes the blocking `build` job fail on every
push/PR: the Docker image build no longer sees the central package-version file that
the csproj files now depend on (`NU1015`). One reproducing command is in C-1.

## Reviewed scope + method

- **Range:** `578949c..4868af4` (26 commits) on `main`; review branch `gsd/02-review`
  at `4868af4`. Work done in a clone (`./work`) inside the assigned worktree; the
  original checkout was not touched.
- **Files:** `global.json`, `Directory.Build.props`, `Directory.Build.targets`,
  `Directory.Packages.props`, `.editorconfig`, `.gitattributes`,
  `.github/workflows/build.yml`, `src/DockerBuild.ps1`,
  `src/AzureDesignStudio.Server/Dockerfile`, all 11 `src/*/*.csproj`, all 11
  `src/*/packages.lock.json`, `.planning/REQUIREMENTS.md`, `.planning/STATE.md`,
  `.planning/phases/02-*/02-0[1-5]-SUMMARY.md`, `02-DESKTOP-RUNS.md`, `AGENTS.md`,
  and the 443-file whitespace commit `f801ba0` plus its `.editorconfig` follow-up
  `4254688`.
- **Method:** read every build/config file in full; parsed the lock files
  programmatically (framework keys, direct-dependency sets, resolved versions);
  parsed the workflow with PyYAML and simulated its shell logic; ran the repo's own
  gates locally; ran negative probes for the lock guard, the audit-expiry target, the
  MSBuild property chain, the format gate and the Docker restore; compared all 443
  normalized `.cs` files token-wise (not only `git diff -w`).
- **Local gates executed** (warm cache, SDK 10.0.401, workload set 10.0.401.1):
  - `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false`
    → exit 0, **0 errors, 91 warnings, all `PRI263`**, zero `CS/NU/IDE/CA/MSB`
    warnings (3:28).
  - `dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore`
    → exit 0 (31 s), tree clean.
  - `dotnet test src/AzureDesignStudio.Core.Tests -c Release --no-build --filter
    "Category!=LiveAzure"` → **755 passed, 0 failed**.
  - `dotnet restore src/AzureDesignStudio.sln --locked-mode` → exit 0 for all 11
    projects; a cold-cache locked restore of the WASM project also exited 0.
- **Not executed:** no GitHub Actions run exists, and none is claimed here. Docker
  itself was not invoked; C-1 is reproduced against the exact file set the Docker
  context produces, which is the only input to the failing `dotnet restore` line.

## Findings

| # | Severity | File:line | One-line failure scenario |
|---|----------|-----------|---------------------------|
| C-1 | Critical | `src/DockerBuild.ps1:9`, `src/AzureDesignStudio.Server/Dockerfile:16-22`, `.github/workflows/build.yml:67-72` | Docker context is `src/`; `Directory.Packages.props` is at the repo root, so the in-image restore dies with `NU1015` and the blocking job goes red on every PR. |
| M-1 | Medium | `.github/workflows/build.yml:173-177` | A failed `git fetch`/`git diff` yields an empty changed list, prints "No changed C# files", exits 0 — the format gate passes vacuously. |
| M-2 | Medium | `.github/workflows/build.yml:190-197` | `[DateTime]::Parse('…Z')` returns Local; subtracting `UtcNow` records a duration wrong by the host UTC offset (observed `-50491 s` on UTC+2). |
| M-3 | Medium | `.github/workflows/build.yml:35-39,51,111` | The Linux gate validates 10 of 11 locks; a stale Desktop lock stays green because the only job that restores it is `continue-on-error`. |
| M-4 | Medium | `.github/workflows/build.yml:58-62` | The four builds run implicit *unlocked* restore after the lock guard, so a lock rewritten during the build is never detected in that run. |
| M-5 | Medium | `src/AzureDesignStudio/packages.lock.json:188` | The WASM SDK-pack hash is source-dependent: a machine whose cache holds the nuget.org copy fails `NU1403` while CI passes. |
| L-1 | Low | `.github/workflows/build.yml:16,22,71,84,87,92,97,100,115,120,203` | Actions are pinned to mutable major tags and `nbgv` is installed without a version. |
| L-2 | Low | `.github/workflows/build.yml:11-13,104-112` | `build` and `desktop` jobs declare no `permissions:` and inherit the repository default. |
| L-3 | Low | `.editorconfig:8` | The stated BOM census ("102 of 820") does not match the tree (68 of 738 `.cs`; 128 of 1061 tracked files). |
| L-4 | Low | `02-03-SUMMARY.md:11,33,81,246` | The phase record says `.editorconfig` omits `end_of_line`, but `4254688` added `end_of_line = lf`. |
| L-5 | Low | `.github/workflows/publish.yml:44` | Pre-existing: the release Docker job builds from the repo root while the Dockerfile uses `src/`-relative `COPY` paths; relevant because C-1's fix touches this file. |

### C-1 (Critical) — Docker image build loses the central build inputs

**Where:** `src/DockerBuild.ps1:9` (`docker build … .` with the workflow's
`working-directory: src`, `.github/workflows/build.yml:69`),
`src/AzureDesignStudio.Server/Dockerfile:16-22`,
`Directory.Packages.props` (repo root).

**What breaks:** Plan 02-01 removed every `Version=` attribute from the 11 csproj
files. The build context for `DockerBuild.ps1` is **`src/`**, so the root-level
`Directory.Packages.props` (and `Directory.Build.props`, `Directory.Build.targets`,
`global.json`) are not in the context and are never copied. The Dockerfile's first
build stage copies only the csproj files and immediately restores
(`Dockerfile:22`), before `COPY . .`:

```text
$ git archive HEAD src | tar -x -C /tmp/dockersim
$ cd /tmp/dockersim/src                      # exactly the Docker context
$ dotnet restore AzureDesignStudio.Server/AzureDesignStudio.Server.csproj
... error NU1015: The following PackageReference item(s) do not have a version specified:
    AntDesign, AutoMapper, … (AzureDesignStudio.Core)
    Azure.Extensions.AspNetCore.Configuration.Secrets, … (AzureDesignStudio.Server)
rc=1   # six of the seven projects in the Server closure fail; AzureResources has no packages
```

Copying only `Directory.Packages.props` into that tree makes the same command exit 0
— proving the failure is caused by the missing central file, not by the simulation.
The same extraction at the pre-phase commit passes unchanged, so this is a regression
introduced by centralization, not a pre-existing Dockerfile defect:

```text
$ git archive 578949c src | tar -x -C /tmp/dockersim-base
$ cd /tmp/dockersim-base/src && dotnet restore AzureDesignStudio.Server/AzureDesignStudio.Server.csproj
rc=0, 0 errors          # inline versions, nothing else needed
```
The props-copied run then reports `warning NU1903: … AutoMapper 12.0.1 …`, because
`TreatWarningsAsErrors`, `NuGetAuditLevel`, `NuGetAuditSuppress` and the expiry target
are also absent: the image would also **silently drop the audit gate**. Without
`global.json`, the image builds with whatever `mcr.microsoft.com/dotnet/sdk:10.0`
currently resolves to.

**Failure scenario:** any push or PR to `main`. `build` is a blocking job and
`.github/workflows/build.yml:67-72` is its last step, so the workflow goes red for a
reason unrelated to the change under test. `02-02-SUMMARY.md:122` records these steps
as "unchanged" — the text is unchanged, the behaviour is not.

**Fix (pick one, then re-run the Docker step):**

1. Build from the repository root:
   `docker build -f src/AzureDesignStudio.Server/Dockerfile -t … .` (from the root),
   and in the Dockerfile copy the central files **before** restore, rewriting the
   existing paths accordingly:
   ```dockerfile
   COPY ["Directory.Packages.props", "/src/"]
   COPY ["Directory.Build.props", "/src/"]
   COPY ["global.json", "/src/"]
   COPY ["Directory.Build.targets", "/src/"]
   COPY ["src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj", "/src/AzureDesignStudio.Server/"]
   # … other csproj copies, all prefixed with src/ …
   RUN dotnet restore "/src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj"
   ```
   Note the name collision: `GenerateVersioningTargets.ps1:47` writes
   `src/Directory.Build.targets`, so the root expiry targets and the generated nbgv
   targets must be merged or imported rather than copied to the same path.
2. Or keep the `src/` context and have `DockerBuild.ps1` stage the four root files
   into `src/` before `docker build` (and remove them afterwards), including a merged
   targets file. Also fix `.github/workflows/publish.yml:44`, which already uses the
   root context against `src/`-relative `COPY` paths (L-5).

### M-1 (Medium) — Changed-file format gate passes vacuously when Git fails

**Where:** `.github/workflows/build.yml:173-177`.

```powershell
git fetch --no-tags --depth=1 origin "$env:GITHUB_BASE_REF"
$changed = @(git diff --name-only --diff-filter=ACMR "origin/$env:GITHUB_BASE_REF...HEAD" -- '*.cs')
if ($changed.Count -eq 0) { … 'No changed C# files; skipping the format gate.'; exit 0 }
```

PowerShell does not throw on a non-zero native exit, and none of these exit codes is
checked. Reproduced with a base ref that cannot be fetched:

```text
fetch exit=128
fatal: bad revision 'origin/does-not-exist...HEAD'
diff exit=128 count=0
No changed C# files; skipping the format gate.      # exit 0
```

**Scenario:** a transient network error, a renamed base branch, or a fetch denied by
permissions turns the format gate off while the job reports success — the one thing
BUILD-03 asks this step to prove. The same happens if `git diff` fails for any reason
(e.g. the repo was made shallow by the fetch itself; verified `.git/shallow` appears).

**Fix:** fail on any Git error and keep "empty diff" distinct:

```powershell
git fetch --no-tags --depth=1 origin "$env:GITHUB_BASE_REF"
if ($LASTEXITCODE -ne 0) { Write-Error "git fetch failed ($LASTEXITCODE)"; exit 1 }
$changed = @(git diff --name-only --diff-filter=ACMR "origin/$env:GITHUB_BASE_REF...HEAD" -- '*.cs')
if ($LASTEXITCODE -ne 0) { Write-Error "git diff failed ($LASTEXITCODE)"; exit 1 }
```

### M-2 (Medium) — Duration metric is timezone-dependent and can be negative

**Where:** `.github/workflows/build.yml:152-155` (timer), `:190-197` (parse and
subtract).

`[DateTime]::Parse('2026-10-23T12:00:00.0000000Z')` returns `Kind=Local` converted to
the host zone; subtracting `[DateTime]::UtcNow` then mixes local ticks with UTC ticks.
Measured on this UTC+2 machine:

```text
kind=Local value=2026-10-23T14:00:00.0000000+02:00
diff-seconds=-50491.55      # a 2 h 01 min error, sign included
```

GitHub-hosted `windows-latest` runs in UTC, so the number happens to be right there —
the artefact's correctness depends on the runner's timezone. `started_at` is written
from the parsed Local value while `ended_at` is UTC (`:195-196`), so the two fields in
`desktop-build-metrics.json` are not comparable, which breaks the ledger's stated
cross-check against the Actions API (`02-DESKTOP-RUNS.md`, "Local baseline"). The
step also throws on `[DateTime]::Parse('')` when `Start timer` was skipped by an
earlier failure (e.g. the lock guard), losing the artefact instead of recording a
failed run.

**Fix:**

```powershell
$raw = '${{ steps.t0.outputs.start }}'
if ([string]::IsNullOrWhiteSpace($raw)) { Write-Warning 'timer missing'; exit 0 }
$start = [DateTime]::Parse($raw, [Globalization.CultureInfo]::InvariantCulture,
          [Globalization.DateTimeStyles]::AdjustToUniversal)
$end = [DateTime]::UtcNow
# started_at = $start.ToString('o') (Kind=Utc), seconds = [int]($end - $start).TotalSeconds
```

### M-3 (Medium) — The blocking gate never validates the Desktop lock

**Where:** `.github/workflows/build.yml:35-39` (four restored roots), `:51` (count
assertion), `:111` (`continue-on-error: true`).

The four roots close over 10 of 11 projects; `AzureDesignStudio.Desktop` is the only
one not restored (`ProjectReference` closure computed from the csproj files). The
third guard line only compares **counts** (`git ls-files … | wc -l` vs
`ls src/*/*.csproj | wc -l`, 11 = 11), which says nothing about freshness.

**Scenario:** a PR edits `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj`
(or a central version used only by it) and forgets to regenerate
`src/AzureDesignStudio.Desktop/packages.lock.json`. The Linux locked restore never
sees the project, the guard counts 11 = 11, all Linux steps pass. The `desktop` job
would fail on `NU1004`, but it is non-blocking by design (BUILD-05 is Pending), so
the stale lock merges and the next person to restore Desktop hits the failure. This is
the exact drift the phase exists to prevent; it is currently only *observed* by a job
nobody is required to pass.

**Fix:** do not rely on the count equality alone. Until plan 02-07 makes the Desktop
job blocking, either (a) add a blocking step that runs
`dotnet restore src/AzureDesignStudio.Desktop --locked-mode` on a Windows runner
(it cannot run on Linux because of the `net10.0-windows` TFM), or (b) add a
Linux-safe structural check that every lock file's `Direct` entry set still equals its
csproj's `PackageReference` set, which catches "project changed, lock not
regenerated" for all 11 projects without restoring them.

### M-4 (Medium) — Builds restore unlocked after the lock guard

**Where:** `.github/workflows/build.yml:58-62` (`dotnet build … -c Release`, no
`--no-restore`).

With `RestorePackagesWithLockFile=true` (root props), a plain restore rewrites a lock
that disagrees with the graph. Demonstrated by changing one central version:

```text
dotnet restore src/AzureDesignStudio.Core --locked-mode  → error NU1004, rc=1
dotnet restore src/AzureDesignStudio.Core               → rc=0
git status --porcelain
 M src/AzureDesignStudio.Core/packages.lock.json        # lock rewritten in place
```

The guard asserts lock state **before** the build, so any rewrite in the build's
implicit restore is invisible for that run (and the next run's guard would blame the
next commit). The job's green result then describes a dependency graph that is not
the committed one. The `desktop` job already does this correctly
(`:161 --no-restore`).

**Fix:** `dotnet build … -c Release --no-restore` on all four Linux builds, or move
the `Lock files must be committed` step after the build.

### M-5 (Medium) — WASM SDK pack lock hash encodes a source, not a package

**Where:** `src/AzureDesignStudio/packages.lock.json:184-188`
(`Microsoft.NET.Sdk.WebAssembly.Pack` `contentHash DbQBhmNA…`).

The same id/version produces two different NuGet content hashes depending on where it
is resolved:

| Source | NuGet contentHash |
|--------|-------------------|
| SDK `library-packs` (SDK 10.0.401 here) | `DbQBhmNA…` (the committed value) |
| nuget.org | `MIeBVeWk…` (the value 02-01 originally committed) |

Reproduced against the committed lock with an empty package folder and the default
nuget.org source:

```text
$ NUGET_PACKAGES=<empty dir> dotnet restore src/AzureDesignStudio --locked-mode \
    -p:DisableImplicitLibraryPacksFolder=true
→ error NU1403: Package content hash validation failed for
  Microsoft.NET.Sdk.WebAssembly.Pack.10.0.12. The package is different than the last restore.
  # installed .nupkg.metadata: contentHash MIeBVeWk…, source https://api.nuget.org/v3/index.json
```

A clean-cache restore that resolves from `library-packs` exits 0 with the committed
`DbQBhmNA…`. (Raw file SHA-512s are a red herring: the library-packs nupkg is
`XjVNjIav…` and today's nuget.org nupkg is `3XajuLX9…`; NuGet's `contentHash` is not
the file hash. Compare the `contentHash` values, not the `.nupkg` bytes.)

**Scenario:** a developer who built the WASM project before this phase has the
nuget.org copy in `~/.nuget/packages`; after pulling the phase, every
`--locked-mode` restore fails `NU1403` until that cache entry is deleted. CI cold
caches pass, so the failure only appears on real machines — the opposite of what a
lock file is for. `02-02-SUMMARY.md:73` calls this "Fixed"; the same document's
deviation note (`:207`) and `02-03-SUMMARY.md` "Known Issues" say it is not, for
exactly those machines.

**Fix:** make the source deterministic and regenerate the lock from an empty cache.
The portable option is to stop NuGet from preferring the SDK fallback folder and let
the lock pin the nuget.org content (e.g. set `DisableImplicitLibraryPacksFolder=true`
repo-wide in `Directory.Build.props`, then keep `MIeBVeWk…`); the alternative is to
add an explicit, cross-platform fallback/feed that always supplies the library-packs
copy. Whichever is chosen, record the other hash in `02-DESKTOP-RUNS.md`/the phase
notes as the known rejected value so the next `NU1403` is diagnosable in one step.

### L-1 (Low) — Mutable action tags and unpinned global tool

**Where:** `.github/workflows/build.yml:16,22,84,87,92,97,100,115,120,203`
(`actions/checkout@v4`, `actions/setup-dotnet@v4`,
`github/codeql-action/*@v3`, `actions/upload-artifact@v4`) and `:71`
(`dotnet tool install -g nbgv`).

A retagged action or a new `nbgv` release changes what a "reproducible" build runs,
with no repository change. `actions/setup-dotnet` and `checkout` are the two that
matter most here because they establish the toolchain. Fix: pin action SHAs (a
comment with the tag is conventional) and `dotnet tool install -g nbgv --version
<exact>` or a checked-in tool manifest with `dotnet tool restore`. `nbgv` is
pre-existing and only noted because the phase's goal is this kind of determinism.

### L-2 (Low) — No least-privilege `permissions:` on two jobs

**Where:** `.github/workflows/build.yml:11-13` (`build`), `:104-112` (`desktop`).

Only `codeql` declares permissions. Both other jobs inherit the repository default,
which may be read/write. Neither needs anything beyond `contents: read`
(`desktop` also needs nothing else; artifacts use the built-in token). Fix: add
`permissions: { contents: read }` to both jobs.

### L-3 (Low) — `.editorconfig` BOM census is stale

**Where:** `.editorconfig:8` — "102 of 820 files carry a UTF-8 BOM".

Measured at `4868af4`: 68 of 738 tracked `.cs` files, and 128 of 1061 tracked files
overall, start with `EF BB BF`. The decision to omit `charset` is still correct; the
number used to justify it is not. Fix: drop the count or restate it as a measurement
with the command that produced it.

### L-4 (Low) — The 02-03 record contradicts the shipped `.editorconfig`

**Where:** `.planning/phases/02-reproducible-build-and-ci-baseline/02-03-SUMMARY.md:11`
("no end_of_line/charset"), `:33`, `:81`, `:246` (self-check PASSED).

Commit `4254688` (`fix(02-03): pin source EOL to LF so the format gate is
platform-stable`) added `end_of_line = lf` to `.editorconfig:10`, and the summary was
not updated. The tree is correct (format exits 0, the platform argument in the new
comment is sound); the phase's own self-check now asserts something false about the
artifact. Fix: amend the summary's `What changed` and `Self-Check` lines to describe
the final file.

### L-5 (Low, pre-existing) — Release Docker job's context/COPY mismatch

**Where:** `.github/workflows/publish.yml:44`
(`docker build -f src/AzureDesignStudio.Server/Dockerfile … .` from the repo root)
vs `src/AzureDesignStudio.Server/Dockerfile:16-21`
(`COPY ["AzureDesignStudio.Server/…"]`).

The context root is the repo root, so those `COPY` sources do not exist. This predates
the phase (the Dockerfile was written for a `src/` context) but it must be handled by
C-1's fix, because the two callers now need different — or unified — contexts.

## Dimensions with no finding

- **`global.json` (SDK/workload pin).** `sdk.version 10.0.401`, `rollForward
  latestPatch`, `allowPrerelease false`, `workloadVersion 10.0.401.1` all evaluate as
  intended: `dotnet --version` → `10.0.401`, `dotnet workload list` reports "Using
  workload version 10.0.401.1", and `dotnet workload restore
  src/AzureDesignStudio.Desktop/…csproj` exits 0 without touching lock files. No
  MSBuild evaluation errors. The workload-set key is valid in this schema location.
- **`Directory.Build.props` (warnings, audit, suppression expiry property).**
  `TreatWarningsAsErrors` is effective and currently a no-op: the full Release build
  produced 0 errors and 91 `PRI263` task warnings, zero compiler/NuGet/analyzer
  warnings. `NuGetAuditLevel=high` / `NuGetAuditMode=all` resolve
  (`dotnet msbuild -getProperty:` → `high` / `all`) and `git grep 'NoWarn' -- src`
  finds no suppression left. The only suppression is the documented,
  advisory-scoped `NuGetAuditSuppress` row with its date property.
- **`Directory.Build.targets` (expiry target).** Fires on `restore` as well as
  `build` (probed with `-p:AdsAuditSuppressionAutoMapperExpires=2020-01-01` →
  exit 1 with the target's message; `dotnet restore` never starts). Boundary is
  correct: the gate fails only when the expiry is *earlier* than the current UTC
  date, so the suppression stays valid through the recorded date (probed with the
  then-current date → exit 0). `_AdsExpiryCompare` evaluates to
  the integer `1`/`0`/`-1`, so the `< 0` condition is numeric and no `MSB4086` is
  reachable (no direct date comparison anywhere; the probes produced zero
  `MSB4086`). An empty override fails closed rather than silently disabling the
  gate (verified: `-p:AdsAuditSuppressionAutoMapperExpires=` → exit 1). No finding.
- **`Directory.Packages.props` (centralization).** 54 rows, 54 unique ids, no
  duplicate id, no empty `Version`, no id at two versions. Every version matches the
  pre-phase csproj value exactly (checked against `git show 578949c:…csproj`; zero
  differences, including the five deliberately commented-out `PackageReference`
  rows). No `Version=` remains on any declared `PackageReference` (count 0). The two
  `$(MauiVersion)` rows are non-empty and resolve to `10.0.110` in the outer
  evaluation (`-getItem:PackageVersion`), confirming the documented
  props-before-packages ordering.
- **The 11 `packages.lock.json` files.** Format `version: 2`; framework keys match
  every project's TFM (`net10.0`, `net10.0/browser-wasm`,
  `net10.0-windows10.0.19041` + `win-x64`, `.NETStandard,Version=v2.0`). For each
  project, the set of `Direct` entries equals the csproj `PackageReference` set; the
  only extras are the expected SDK-injected `NETStandard.Library` and
  `Microsoft.AspNetCore.App.Internal.Assets`. `AzureResources`' empty graph is
  correct (no package references). The crypto pin resolves as `Direct 10.0.13` in
  both the WASM and Desktop graphs. All 11 pass `--locked-mode` with a warm cache,
  and the WASM project also passes with an **empty** package folder (exit 0). M-5 is
  the one caveat.
- **EOL / format contract.** `.gitattributes` `* text=auto eol=lf` is safe here:
  0 tracked files had CRLF in the index at both `578949c` and `HEAD`, the worktree is
  LF, the 7 binary files (gif/png/ico/ttf) are correctly detected `-text`, and there
  are no `.bat`/`.cmd` files that would need CRLF; the `.sln` and `.ps1` files are
  LF in the index and parse/execute. A fresh clone is clean under `git status`.
  `dotnet format --verify-no-changes` exits 0 on the final tree (31 s).
- **The whitespace normalization commit.** All 443 changed `.cs` files have an
  identical sequence of alphanumeric runs before and after, and `git diff -w`
  surfaces only six files: four object-initializer reflows, two `};` → `}` + newline
  + `;` splits (an empty statement, semantically inert), and the doubled-BOM collapse
  in `StorageAccountModel.cs` (documented). No string literal content changed; the
  raw-string count is 48 on both sides. The summary's "BOM- and whitespace-stripped
  byte-identical" method is weaker than "token-identical" (deleting whitespace cannot
  detect a merge like `int x` → `intx`), but the stronger independent check above
  passes, so the commit is safe.
- **Requirements honesty.** `BUILD-02`, `BUILD-03`, `BUILD-05` are `[ ]` / `Pending`
  and no document claims a GitHub Actions run; `02-DESKTOP-RUNS.md` is
  `status: awaiting-runs` with an empty run table, and `02-05-SUMMARY.md:164`
  explicitly says none happened. `BUILD-04`'s `[x]` is fully supported by
  `Directory.Build.props` + `Directory.Build.targets` + the probes. `BUILD-01`'s
  `[x]` is supported by the artifacts and a clean-cache restore, with M-5 as the
  stated caveat. `STATE.md`'s deferred-work note is accurate.
- **Security scan of the change set.** No secrets, credentials, tokens or new
  endpoints; no shell interpolation of untrusted input (`GITHUB_BASE_REF` and the
  changed paths are passed as quoted arguments / array elements, not concatenated
  into a command); no `pull_request_target`, `eval`, or unpinned download that
  executes. The only deliberate weakening is the documented AutoMapper advisory
  suppression, which is dated and build-enforced (see the target analysis above).
  No injection or auth/authz surface is touched by this phase.
- **Lock guard's three assertions, as built.** Verified locally: the count is 11 = 11;
  a stale lock fails the locked restore with `NU1004`; a deleted lock is recreated by
  `--locked-mode` and caught by the working-tree/count lines, not by `git diff` —
  i.e. each line addresses the failure mode in its comment. The weaknesses are M-3
  (coverage) and M-4 (ordering), not the three lines' intent.
- **`dotnet format` invocation shape.** `--include` with an array of Git paths works
  (independently re-run: exit 0 on the clean tree, exit 2 on a deliberately
  mis-indented file in plan 02-05's test), `--diff-filter=ACMR` handles renames and
  skips deletions, and the `--no-restore` flag is backed by the preceding locked
  restore.

## Verdict

**BLOCK.** C-1 breaks the blocking `build` workflow on every push and PR to `main`
(the Docker image restore cannot resolve the newly versionless package references),
and the phase record currently asserts those steps are unaffected. M-1, M-2 and M-3
should be fixed before the phase is called complete; M-4 and M-5 can be scheduled but
M-5 must at least be documented as the residual NU1403 hazard it is. L-1 to L-5 are
hygiene and record-accuracy fixes.

---

_Reviewed: 2026-10-23T00:06:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_
