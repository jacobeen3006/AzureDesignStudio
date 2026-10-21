# Phase 02: Reproducible Build and CI Baseline - Pattern Map

**Mapped:** 2026-10-21
**Files analyzed:** 31 intended paths (32 rows — `build.yml` has two independent changes)
**Analogs found:** 16 / 32 rows (13 exact self-analog, 3 role-match); 15 rows are new-by-design; 1 row is an expected no-op (`AzureResources`)

This phase writes build infrastructure, not application code, and the repository has almost no build-infrastructure precedent: no `global.json`, no `Directory.Build.*`, no `Directory.Packages.props`, no `.editorconfig`, no `.gitattributes`, no `NuGet.config`, no `packages.lock.json` (verified this session with `find` at depth 4 and `git ls-files '*packages.lock.json'` → 0). What *does* exist is the shape of the 11 csproj files, `src/version.json`, and three workflow files. Each new artefact below either imitates the observed style of those files or is marked **NEW-BY-DESIGN** with the research measurement that fixes its shape. No house convention is invented.

All analog paths named below were checked with `git ls-files` (all tracked). No `.gsd/capabilities/` mirror is referenced.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `global.json` | config (SDK resolver input) | batch (evaluated per `dotnet` invocation) | `src/version.json` (only root-style JSON tool config) | no-analog (structural only) |
| `Directory.Build.props` | config (MSBuild props, inherited by 11 projects) | batch | `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:6-42` (PropertyGroup + why-comments) | no-analog (style only) |
| `Directory.Packages.props` | config (CPM version authority) | batch | `src/AzureDesignStudio/AzureDesignStudio.csproj:23-54` (representative block; 74 `PackageReference` lines exist across 11 files — 69 declared, 5 commented) | no-analog file / exact source data |
| `Directory.Build.targets` | build target | event-driven (`BeforeTargets`) | `DropForeignThreadingAnalyzers` target, e.g. `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:83-89` | role-match |
| `.editorconfig` | config (formatter policy) | batch | none | no-analog |
| `src/AzureDesignStudio/packages.lock.json` | generated data (NuGet graph) | batch | none | no-analog |
| `src/AzureDesignStudio.Core/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.Core.Tests/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.Desktop/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.Discovery/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.Mcp/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.Server/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.SharedModels/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.SourceGeneration/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.Workspace/packages.lock.json` | generated data | batch | none | no-analog |
| `src/AzureDesignStudio.AzureResources/packages.lock.json` | generated data | batch | none | no-analog |
| `.github/workflows/build.yml` (Linux job edits) | CI config | request-response (per PR) | same file, `build.yml:11-46` | exact (self) |
| `.github/workflows/build.yml` (new `desktop` job) | CI config | event-driven | `publish.yml:55-100` (`desktop-msix`) + `build.yml:11-46` | role-match |
| `.planning/phases/02-…/02-DESKTOP-RUNS.md` | docs (evidence ledger) | transform | `.planning/phases/01-land-and-stabilize/01-VERIFICATION.md:99-193` | role-match |
| `src/AzureDesignStudio/AzureDesignStudio.csproj` | config (project) | batch | self, lines 18-22 and 23-54 | exact |
| `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj` | config (project) | batch | self, lines 10-13 and 18-34 | exact |
| `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` | config (project) | batch | self, lines 63-70 | exact |
| `src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj` | config (project) | batch | self, lines 14-33 | exact |
| `src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj` | config (project) | batch | self, lines 10-25 | exact |
| `src/AzureDesignStudio.SharedModels/AzureDesignStudio.SharedModels.csproj` | config (project) | batch | self, lines 14-26 | exact |
| `src/AzureDesignStudio.SourceGeneration/AzureDesignStudio.SourceGeneration.csproj` | config (project) | batch | self, lines 23-31 | exact |
| `src/AzureDesignStudio.Workspace/AzureDesignStudio.Workspace.csproj` | config (project) | batch | self, lines 9-11 | exact |
| `src/AzureDesignStudio.Discovery/AzureDesignStudio.Discovery.csproj` | config (project) | batch | self, lines 10-13 | exact |
| `src/AzureDesignStudio.Mcp/AzureDesignStudio.Mcp.csproj` | config (project) | batch | self, lines 10-13 | exact |
| `src/AzureDesignStudio.AzureResources/AzureDesignStudio.AzureResources.csproj` | config (project) | batch | none — no `PackageReference`; edited only if a `Version=` appears | no change expected |
| `AGENTS.md` | docs (agent knowledge base) | transform | `AGENTS.md:57,63-74`; Phase 1 precedent `01-PATTERNS.md` § AGENTS.md | exact (self) |
| `.planning/REQUIREMENTS.md` | planning state | transform | `REQUIREMENTS.md:51-54` (the unchecked rows) + `:164` | exact (self) |

---

## Pattern Assignments

### `global.json` (config, batch) — NEW-BY-DESIGN

**Analog:** none. The nearest structural analogue is `src/version.json:1-7`, the only other JSON tool-config at the top of the tree, and it is deliberately *not* a template for content:

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/Nerdbank.GitVersioning/master/src/NerdBank.GitVersioning/version.schema.json",
  "version": "1.0",
  "cloudBuild": {
    "setVersionVariables": false
  }
}
```

`src/AzureDesignStudio.sln:22` declares `version.json = version.json` **relative to `src/`** — i.e. the version authority already lives beside the solution and must not be moved. `global.json` is different: it is resolved from the process working directory upward, and every CI and developer command in this repo runs from the repository root (`dotnet build src/…`, `build.yml:32-36`), so it goes at the repo root, beside `src/`.

**Shape fixed by research, not by house style:**

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

- `version` + `rollForward` are one step; `workloadVersion: "10.0.401.1"` is a **separate, revertible** step (Q2, Q9 step 6) because it is the one line that can fail to resolve from nuget.org (A3).
- Evidence: E1 (10.0.401 is the only local SDK), E3 (workload set `10.0.401.1`, mode `workload-set`), E21 (`latestPatch` does **not** cross feature bands — a missing 4xx band is a hard failure), E22 (pinned version resolves to itself).
- Do not write `rollForward: major/minor`; do not write `10.0.x`. If `setup-dotnet` is given `dotnet-version` as well, two authorities compete (Q2).

### `Directory.Build.props` (config, batch) — NEW-BY-DESIGN (style from Desktop csproj)

**Style analog:** `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:6-42` — one `<PropertyGroup>` of plain properties, each either self-explanatory or preceded by an XML comment that names the *reason* (e.g. the `EnableDefaultCssItems` comment at line 17-18, the `MauiXamlInflator` block at 22-31). The comments are two to five lines, state a constraint, and never narrate a diff.

**The comment voice to copy**, from `src/AzureDesignStudio/AzureDesignStudio.csproj:18-22`:

```xml
  <PropertyGroup>
    <!-- AutoMapper 12.0.1 GHSA-rvv3-g6hj-g44x (DoS via uncontrolled recursion) accepted:
         mappings are shallow node DTOs; 13+ requires a commercial license key. -->
    <NoWarn>$(NoWarn);NU1903</NoWarn>
  </PropertyGroup>
```

**Content is evidence-driven, not invented:**

| Property / item | Value | Evidence | Placement reason |
|---|---|---|---|
| `MauiVersion` | `10.0.110` | E4 (empty in the outer evaluation), E5 (`10.0.110` in the inner build), E20 (props is evaluated before `Directory.Packages.props`) | Must be here, not in `Directory.Packages.props`, or the two MAUI `PackageVersion` rows evaluate empty |
| `RestorePackagesWithLockFile` | `true` | E16, E32 | Every project gets a lock file; a new project cannot opt out |
| `TreatWarningsAsErrors` | `true` | E7/E8 (91 warnings, **all** `PRI263` tool warnings; zero CS, NU, IDE, CA or MSB warnings), E9 (Desktop still builds with the switch on) | No-op today; prevents regressions; does not promote task warnings |
| `NuGetAuditLevel` | `high` | E24 (SDK defaults: `true`/`low`/`all`), E25 (NU1903 fires), E26 (code-scoped promotion also works) | Only High/Critical are reported, so only they can fail |
| `NuGetAuditSuppress Include="https://github.com/advisories/GHSA-rvv3-g6hj-g44x"` | one item | E27 (suppression silences NU1903 end-to-end); Q6 | Advisory-scoped, not package-scoped — say so in the comment |
| `AdsAuditSuppressionAutoMapperExpires` | `2027-01-31` | Q6 / `Directory.Build.targets` below | Read by the expiry target |

**Required exclusions (each measured):**

- **No `RestoreLockedMode`** anywhere in the repo. It is a CI flag only; committing it forces `--force-evaluate` on every local package edit (Q1). `ContinuousIntegrationBuild=true` does **not** imply it (E19).
- **No repo-wide `ImplicitUsings` or `Nullable`.** AGENTS.md:65 claims all SDK-style projects enable `ImplicitUsings`; that is false today (see the fiction table below) — `Core.Tests`, `AzureResources` and `SourceGeneration` do not, so adding it here would silently change their compilation. `Nullable` must not be turned on for `AzureResources` (Q4: it would open exactly the warning explosion this phase is trying to avoid).
- **No `MSBuildTreatWarningsAsErrors`** (Q4: it would turn the 91 `PRI263` resource-packaging warnings into failures).
- The research example also shows `DisableImplicitNuGetFallbackFolder`; **no measurement in the ledger requires it**. The planner may include or omit it, but must not claim it is evidence-backed.

### `Directory.Packages.props` (config, batch) — NEW-BY-DESIGN (data from the csproj files)

**Source data analog:** the per-project declarations themselves. Measured this session: **74 `PackageReference` lines** in total — **69 declared references** carrying `Version=` across **53 distinct ids**, plus **5 commented-out rows** in `Core.csproj:21-25` that are not declarations — and **0 ids with two different versions** (matches E15, corrected). The executor moves `Version=` attributes; it does not choose versions. One row is not a move: the vulnerable `System.Security.Cryptography.Xml` transitive (10.0.10 in the WASM and Desktop graphs) is pinned to 10.0.13 and referenced directly by those two projects, because BUILD-04 permits only the AutoMapper allowlist entry and 10.0.10 carries three high advisories.

Representative block to imitate mechanically — `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:63-70`:

```xml
    <ItemGroup>
        <PackageReference Include="Azure.Identity" Version="1.21.0" />
        <PackageReference Include="Microsoft.Identity.Client" Version="4.91.0" />
        <PackageReference Include="Microsoft.Identity.Client.Broker" Version="4.91.0" />
        <PackageReference Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
        <PackageReference Include="Microsoft.AspNetCore.Components.WebView.Maui" Version="$(MauiVersion)" />
        <PackageReference Include="Microsoft.Extensions.Logging.Debug" Version="10.0.0" />
    </ItemGroup>
```

**Rules the executor must follow:**

1. `ManagePackageVersionsCentrally` = `true` in a `PropertyGroup`.
2. One `<PackageVersion Include="…" Version="…" />` per **distinct id** (53 rows from the declared references), version copied verbatim from the csproj that declares it. The five commented-out rows contribute no row.
3. The two MAUI rows keep the property form: `Version="$(MauiVersion)"` — the pin itself lives in `Directory.Build.props` (E20).
4. **Metadata stays on the reference, not the version.** `PrivateAssets`, `IncludeAssets`, `ExcludeAssets` are reference metadata: `Nerdbank.GitVersioning`'s `<PrivateAssets>all</PrivateAssets>` pair (`AzureDesignStudio.csproj:45-48`, `Core.csproj:30-33`, `Server.csproj:29-32`, `SharedModels.csproj:22-25`), `xunit.runner.visualstudio`/`coverlet.collector` (`Core.Tests.csproj:17-24`), `Grpc.Tools` (`AzureDesignStudio.csproj:36-39`), and `Scriban`'s `IncludeAssets="Build"` (`SourceGeneration.csproj:29`) all remain in their csproj; only `Version=` disappears.
5. Leave commented-out rows alone — `Core.csproj:21-25` contains five commented `AzureDesignStudio.AzureResources.*` references. They are not declarations; do not convert them and do not delete them.
6. No exclusions: `AzureResources` has no `PackageReference` but still gets a lock file (Q1, 66-byte file measured on an empty project).
7. The crypto pin is a **measured exception, not a move**: add `<PackageVersion Include="System.Security.Cryptography.Xml" Version="10.0.13" />` and a `<PackageReference Include="System.Security.Cryptography.Xml" />` (no `Version`) in `AzureDesignStudio.csproj` and `AzureDesignStudio.Desktop.csproj` — the only two graphs that resolve the transitive 10.0.10, which carries three high advisories. Do **not** use `CentralPackageTransitivePinningEnabled`: measured on this tree it turns every central row into a transitive floor and fails restore with `NU1109` on `Microsoft.Extensions.Logging.Debug` and `Microsoft.AspNetCore.Components.WebAssembly`.

### `Directory.Build.targets` (build target, event-driven) — NEW-BY-DESIGN (target shape from csproj)

**Analog:** the repo already writes custom MSBuild targets with the exact idiom this file needs — a comment stating *why*, then `<Target Name="…" BeforeTargets="…">`. Four copies exist and are byte-identical in intent: `src/AzureDesignStudio/AzureDesignStudio.csproj:73-80`, `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:83-89`, `src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj:41-47`, `src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:39-45`:

```xml
    <!-- Analyzer donor from the referenced WASM app's Azure.Bicep.Decompiler; keep out. -->
    <Target Name="DropForeignThreadingAnalyzers" BeforeTargets="CoreCompile">
        <ItemGroup>
            <Analyzer Remove="@(Analyzer)" Condition="'%(Analyzer.NuGetPackageId)' == 'Microsoft.VisualStudio.Threading.Analyzers'" />
        </ItemGroup>
    </Target>
```

Copy the shape (comment first, `BeforeTargets`, one `<Error Condition=…>`), and the naming convention (`Ads…`, matching `AdsWorkspace`, `AdsToolEngine`, `AdsTokenBroker`).

**Content fixed by research** (Q6, verified to fire on both `restore` and `build`):

```xml
<Project>
  <PropertyGroup>
    <_AdsToday>$([System.DateTime]::UtcNow.ToString("yyyy-MM-dd"))</_AdsToday>
    <_AdsExpiryCompare>$([System.String]::Compare('$(AdsAuditSuppressionAutoMapperExpires)', '$(_AdsToday)'))</_AdsExpiryCompare>
  </PropertyGroup>
  <Target Name="AdsCheckAuditSuppressionExpiry" BeforeTargets="Restore;Build;PrepareForBuild">
    <Error Condition="$(_AdsExpiryCompare) &lt; 0"
           Text="NuGetAuditSuppress for AutoMapper (GHSA-rvv3-g6hj-g44x) expired on $(AdsAuditSuppressionAutoMapperExpires); today is $(_AdsToday)." />
  </Target>
</Project>
```

**Non-negotiable detail:** the `[System.String]::Compare` indirection. Both obvious spellings produce `error MSB4086: A numeric comparison was attempted on …` (Pitfall 7, measured twice). The `<Project>` has **no `Sdk` attribute** — it is imported into every project, including `netstandard2.0` SourceGeneration.

### `.editorconfig` (config, batch) — NEW-BY-DESIGN

**Analog:** none (verified absent; there is no `.gitattributes` either).

**Shape fixed by measurement:**

```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
tab_width = 4
trim_trailing_whitespace = true
insert_final_newline = true
```

- E12: today's gate is **exit 2, 316 `WHITESPACE` diagnostics in 38 of 820 files** — every one whitespace, not a style-convention conflict.
- E28: index is LF for all 820 files while this worktree checks out 519 as CRLF (`core.autocrlf=true`, no `.gitattributes`) → **`end_of_line` must be omitted**, either value rewrites hundreds of files.
- E29: 102 of 820 files carry a UTF-8 BOM, 718 do not → **`charset` must be omitted**.
- No `csharp_style_*` / `dotnet_diagnostic.IDE####.severity` rows: that is `dotnet format style`, a different gate with no consensus (Q5).
- One step: the `.editorconfig` and the one-time `dotnet format whitespace src/AzureDesignStudio.sln` commit (38 files) land **together**; the CI gate is the *next* step, or its first run is red on the same PR that introduces it (Q9).
- Do not add a `[*.razor]` section: the measured diagnostic set is C# whitespace and the research's seeded file has only `[*.cs]`.

### 11 × `src/*/packages.lock.json` (generated data) — NEW-BY-DESIGN

**Analog:** none; zero lock files exist and `.gitignore` contains no rule that would hide them (E32; re-verified: no `packages.lock.json` pattern in `.gitignore`).

**How they are produced:** add `RestorePackagesWithLockFile=true` to `Directory.Build.props`, run a plain `dotnet restore`, and commit the 11 generated files — one per project directory, including `AzureResources` (no `PackageReference`) and `SourceGeneration` (`netstandard2.0`). Shape per E16: NuGet format `"version": 2` with `"requested"`, `"resolved"` and `"contentHash"` per dependency. **Do not hand-edit, do not hand-create.** The post-restore assertion is `git status` showing exactly those 11 files (Q9 step 3).

**Why they are not self-enforcing:** `--locked-mode` exits **0 and creates the file** when none exists (E18). Hence the paired CI guard below; the lock files alone prove nothing.

### `.github/workflows/build.yml` — Linux job edits (CI config, request-response) — EXACT (self)

**Analog:** the same file, `build.yml:11-46`. Preserve all of it except the two changes below.

**Existing shape to preserve verbatim** (`build.yml:15-24`): checkout with the nbgv comment, then `setup-dotnet`:

```yaml
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0 # avoid shallow clone so nbgv can do its work.


    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'
```

**Change 1 — one authority for the SDK.** Replace the `with:` body at lines 23-24 with:

```yaml
      with:
        global-json-file: global.json
        cache: true
        cache-dependency-path: '**/packages.lock.json'
```

Do not leave `dotnet-version: '10.0.x'` beside `global.json` (Q2 anti-pattern). The same line also exists at `build.yml:62` (CodeQL job) and `publish.yml:15,26,65`; `publish.yml` is **out of scope and must not be disturbed** (its MSIX path is Phase 11's), and the CodeQL setup is not a gate this phase owns — record the duplicate in the phase summary rather than editing files the phase does not need.

**Change 2 — locked restore plus the missing-file guard.** Insert before the existing `Build` step (`build.yml:26`), keeping that step's comment and explicit project set untouched (`build.yml:27-36`):

```yaml
    - name: Restore (locked)
      run: |
        dotnet restore src/AzureDesignStudio.Server --locked-mode
        dotnet restore src/AzureDesignStudio --locked-mode
        dotnet restore src/AzureDesignStudio.Mcp --locked-mode
        dotnet restore src/AzureDesignStudio.Core.Tests --locked-mode

    - name: Lock files must be committed
      run: git diff --exit-code -- '*packages.lock.json'
```

- E17: a changed central version fails with **`error NU1004`** — that is the gate's proof.
- E18: the diff guard is the only thing that catches a lock file the restore just created; without it the gate is green forever while pinning nothing.
- Keep `fetch-depth: 0` (nbgv is live: E30, `src/version.json`), and keep the existing `Test (offline)` step (`build.yml:37-38`) and the `pwsh` Docker step (`build.yml:40-45`) unchanged.

### `.github/workflows/build.yml` — new `desktop` job (CI config, event-driven) — ROLE-MATCH

**Analogs:** two, and each contributes a different half.

- **Job/skeleton and comment voice:** the existing `build` job, `build.yml:11-46` (job name, `runs-on`, `steps`, why-comment above the build command).
- **Windows + MAUI + artefact half:** `publish.yml:55-100`:

```yaml
  desktop-msix:
    name: Package Windows MSIX
    runs-on: windows-latest
    env:
      MSIX_PFX: ${{ secrets.MSIX_PFX_BASE64 }}
    steps:
    - uses: actions/checkout@v4
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: ${{ env.DOTNET_VERSION }}
    - name: Install MAUI workload
      run: dotnet workload install maui-windows
```

and the artefact tail from `publish.yml:96-100` (`actions/upload-artifact@v4` with `name`/`path`).

**Job shape the executor must write** (values fixed by Q3/Q7/Q8, not by the analog):

| Element | Value | Evidence |
|---|---|---|
| job id / display name | `desktop` / `Desktop (windows-latest)` | distinct from the existing `build` job (Pitfall 12) |
| `runs-on` | `windows-latest` | Q3 |
| `continue-on-error` | `true` inline with a comment naming the flip condition | Q7; this is what makes it non-blocking |
| `timeout-minutes` | `30` | Q7 (local no-AOT build is 74 s) |
| checkout | `actions/checkout@v4` + `fetch-depth: 0` | same as `build.yml:15-18`, nbgv needs history (E30) |
| SDK | `setup-dotnet` + `global-json-file: global.json` + lock-file cache | Q2/Q7 |
| workloads | `dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` | **deviation:** do not copy `publish.yml:67`'s `dotnet workload install maui-windows`; the image ships MAUI **9** and Q3 requires restore-by-project |
| build | `dotnet build …Desktop.csproj -c Release --no-restore -p:RunAOTCompilation=false` | E10 (74 s) vs E9 (466 s with AOT) |
| format gate | `dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore --include <changed>` | E23 (the real flag; `--verify-changed` does not exist), E14 (18 s scoped) |
| metrics | start timer → build → `Record duration` (`if: always()`) → `upload-artifact@v4` (`if: always()`) | Q8 |

**Changed-files format step** (PowerShell, because the job is Windows — Q5/Pitfall 6; exit **2** means "would change"):

```yaml
    - name: Format (changed files)
      shell: pwsh
      run: |
        git fetch --no-tags --depth=1 origin "$env:GITHUB_BASE_REF"
        $changed = git diff --name-only --diff-filter=ACMR "origin/$env:GITHUB_BASE_REF...HEAD" -- '*.cs'
        if (-not $changed) { Write-Host 'no C# changes'; exit 0 }
        dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore --include $changed
```

**Do not** run the format gate on the Linux job (Pitfall 6: the solution contains the Windows-only Desktop project; `build.yml:27-31` documents the same reason for the explicit project set). **Do not** cache `C:\Program Files\dotnet\packs` (5,081 MB payload, replaced on image updates; Q7). **Do not** use `pull_request_target` (Security Domain).

### `.planning/phases/02-reproducible-build-and-ci-baseline/02-DESKTOP-RUNS.md` (docs evidence ledger) — ROLE-MATCH

**Analog:** `.planning/phases/01-land-and-stabilize/01-VERIFICATION.md:99-193`. Phase 1's ledger is the house format for evidence: a table whose every row is a **behaviour, the command that produced it, and the observed result** — e.g. `01-VERIFICATION.md:154-161`:

```markdown
| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Core commit is green at its own tree | `dotnet build src/AzureDesignStudio.sln`; `dotnet test … --filter "Category!=LiveAzure"` in a worktree at `078a9d7` | build 0 errors; `Passed: 753, Failed: 0` | ✓ PASS |
| Fresh clone has no lying artefacts | clone bundle; `git ls-files \| grep -E '\.dll$\|^\.(omo\|swarm)/'`; count `.planning`/csproj | no matches; 39; 11 | ✓ PASS |
```

and its front matter (`01-VERIFICATION.md:1-6`) carrying `phase:`, `verified:`, `status:`, `score:`.

**What this file must contain** (Q7/Q8, SC#2/SC#5):

1. One row per Windows run: run id, commit sha, `continue-on-error` state, conclusion, job name, **seconds** (from the metrics artefact, cross-checked against the Actions API `started_at`/`completed_at`), and the run URL.
2. The SC#2 red-run row for `ci-proof/desktop-compile-error`: PR link (closed, unmerged), failing `CS` error naming `MauiProgram.cs`, run URL, and the guard measurement `git rev-list --count origin/main..main` = 0.
3. The flip record: the commit that deletes `continue-on-error`, plus the required-check proof (UI screenshot path or `gh api …/branches/main/protection` output).
4. The local baseline for comparison: **74 s** clean Desktop Release build with AOT disabled (E10), **466 s** with AOT (E9).

**Style rules from Phase 1 that carry over:** measurements are stated with the command that produced them; a claim without a command/URL is not evidence; `✓`/`✗` status column; no estimates presented as measurements.

### The 11 csproj files (config, batch) — EXACT (self)

Two edits only: **strip `Version=` from every `PackageReference`**, and **delete the two `NoWarn NU1903` blocks**. Everything else — TFMs, `Nullable`, `ImplicitUsings`, `WindowsPackageType=None` (`Desktop:41`), `RunAOTCompilation` (`AzureDesignStudio.csproj:10`), `InternalsVisibleTo` (`Discovery:17`), the `DropForeignThreadingAnalyzers` targets — stays byte-identical.

**Per-project declaration ranges** (verified line numbers; the range names where `Version=` lives):

| Project | Declarations | `NoWarn` to delete | Notes |
|---|---|---|---|
| `AzureDesignStudio.csproj` | 23-54 | **18-22** (comment + line 21) | 18 references; `Microsoft.AspNetCore.Components.WebAssembly.DevServer` (41) keeps `PrivateAssets="all"` |
| `Core.csproj` | 18-34 | **10-13** (comment + line 12) | five commented-out refs at 21-25 stay |
| `Desktop.csproj` | 63-70 | — | two `$(MauiVersion)` refs at 67-68 keep the property form |
| `Server.csproj` | 14-33 | — | 15 refs; nbgv at 29-32 |
| `Core.Tests.csproj` | 10-25 | — | test SDK/xunit/coverlet `PrivateAssets` stay |
| `SharedModels.csproj` | 14-26 | — | `Protobuf` items at 28-32 untouched |
| `SourceGeneration.csproj` | 23-31 | — | `Scriban` keeps `IncludeAssets="Build"` |
| `Workspace.csproj` | 9-11 | — | one reference |
| `Discovery.csproj` | 10-13 | — | one reference |
| `Mcp.csproj` | 10-13 | — | two references |
| `AzureResources.csproj` | — | — | **no `PackageReference`**; expect no diff |

**Ordering constraint:** `Directory.Build.props` (with `MauiVersion`) must exist before `Directory.Packages.props` is imported, and the `Version=` removal is what makes NU1008 disappear (Q1/Q9). Steps 1→2 in Q9 are separate and each verified.

**Deletion is required, not optional:** if either `NoWarn` survives, WASM and Core bypass the audit gate entirely and the dated allowlist proves nothing (Q6/Pitfall 9). The suppression item in `Directory.Build.props` replaces both.

### `AGENTS.md` (docs) — EXACT (self), truth maintenance

**Analog:** its own convention blocks — `AGENTS.md:57` ("Fix build/packages | root csproj files | No Directory.Build.props — version per-project"), `AGENTS.md:63-74` (the CONVENTIONS bullets), and the Phase 1 precedent that AGENTS.md is corrected when the tree changes (`01-PATTERNS.md` § `AGENTS.md`, which reused `AGENTS.md:69` as the authority).

After the props file lands, lines 57 and 67 ("No Directory.Build.props — each csproj declares its own packages/versions") and line 74 ("no `Directory.Build.props` for test — each test project independent") are **false**. The honest replacement is a one-line convention, in the existing bullet voice, e.g.:

```markdown
- **Build config**: `Directory.Build.props` owns repo-wide properties (MauiVersion, lock files, warnings, audit level); `Directory.Packages.props` owns every package version; each csproj declares only `Include=`. Locked restore is a CI flag, never a repo property.
```

Timing: same commit that lands `Directory.Build.props`/`Directory.Packages.props`, or the phase summary's own docs are wrong by the end of the phase. Do not touch the `Nullable` bullet (line 64) — it is true and unchanged.

### `.planning/REQUIREMENTS.md` (planning state) — EXACT (self)

**Analog:** the unchecked siblings at `REQUIREMENTS.md:51-54` and the traceability row shape at `:164-168`.

Research Q9 requires this explicitly: `BUILD-01` is `[x]` at line 50 and `Complete` at line 164 while **none** of its artefacts exist. Set it back to `[ ]`/`Pending` at phase start and re-mark it only when Q9 steps 1-4 are green. This is the same class of "tracked artefact stops lying" work Phase 1 did; it is not scope expansion.

---

## Shared Patterns

### One authority per concern
**Source:** research Pattern 1; observed violation in this repo — three workflows each carry their own SDK selection: `build.yml:24`, `build.yml:62`, `publish.yml:15,26,65`.
**Apply to:** `global.json`, `Directory.Packages.props`, `Directory.Build.props`, `packages.lock.json`, `.editorconfig`.
Each build input that can drift gets exactly one file that owns it. Duplicated policy is how the non-existent `dotnet format --verify-changed` survived into the roadmap note (E23).

### Gates that fail for the right reason
**Source:** research Pattern 2; anti-example at `AzureDesignStudio.csproj:21` and `Core.csproj:12`.
**Apply to:** every gate this phase writes.
Each gate must produce a diagnostic that names its cause: `NU1004` (lock drift), `NU1903` (advisory), `WHITESPACE` (formatting), `CS…` (compile break), `error : NuGetAuditSuppress … expired` (expiry). A gate that reports green while the vulnerable package ships (per-project `NoWarn`) is the anti-pattern.

### Prove the negative
**Source:** research Pattern 3; SC#2 and SC#5 are both phrased "fails when X".
**Apply to:** the Desktop job, the locked restore, the audit gate, the expiry target, the format gate.
Proof is an observed failure, not a wiring assertion: a closed PR whose Desktop job is red (Q7), a deliberately bumped central version producing NU1004 (E17), an expiry date re-pointed to yesterday (Q6), a mis-indented file producing exit 2 (E14). Record each in `02-DESKTOP-RUNS.md` / the phase verification.

### Phase 1 execution conventions that must carry forward
**Source:** `.planning/phases/01-land-and-stabilize/01-01-PLAN.md:1-50` and `01-PATTERNS.md` § Shared Patterns.
**Apply to:** every phase-2 plan.
- Plan front matter names `files_modified` as an explicit path list; `prohibitions` enumerates the forbidden shortcuts.
- **No `git add -A` / `git add .` / `git commit -a`; every commit stages an explicit path list** (`01-01-PLAN.md:52-54`, staging example at `:109`). This matters most for the 38-file whitespace normalisation commit: stage the measured file list, not the directory.
- Build proofs run in a throwaway worktree (`01-01-PLAN.md:50`, mechanism at `:115`) — Phase 2's clean-clone proof uses `NUGET_PACKAGES=$(mktemp -d)` / an empty cache instead (Pitfall 13).
- Verify commands are written into the plan with their expected output, not paraphrased ("`grep -c 'PackageReference[^>]*Version=' src/*/*.csproj` = 0").

### Whole-file-diff guard (line endings)
**Source:** E28 — index LF for all 820 files, worktree CRLF for 519, `core.autocrlf=true`, no `.gitattributes`.
**Apply to:** all 11 csproj edits and the 38-file whitespace commit.
After editing, `git diff --stat <path>` must show changed lines, not the whole file. A whole-file diff is a CRLF artefact — restore and re-edit, never commit it. This is why `.gitattributes` is **not** created in this phase.

### Comment voice
**Source:** `Desktop.csproj:6-31`, `AzureDesignStudio.csproj:17-20,34-35`, `Desktop.csproj:83`.
**Apply to:** `Directory.Build.props`, `Directory.Packages.props`, `Directory.Build.targets`, the new workflow job.
Two-to-five-line XML/YAML comments that state a measured constraint and the reason it is not the obvious alternative ("Must be here, not in Directory.Packages.props: the property is empty in the outer evaluation"). No diff narration, no emoji, no TODO.

---

## Conventions AGENTS.md claims that do **not** exist (do not inherit them)

This table exists so no plan builds on a fiction. Every row was verified this session.

| Claim | AGENTS.md line | Verified reality | Consequence for Phase 2 |
|---|---|---|---|
| "No `Directory.Build.props` — version per-project" | 57, 67 | **True today**, and deliberately retired by this phase | Not a fiction — it is the state step 1 replaces; the same commit must update these lines |
| "no `Directory.Build.props` for test — each test project independent" | 74 | True today; `Core.Tests.csproj:1-8` is a plain `Microsoft.NET.Sdk` project | After step 1, `Core.Tests` **will** inherit repo-wide properties. Do not add `Condition="'$(IsTestProject)' != 'true'"` without a measurement that requires it |
| "`ImplicitUsings`: enabled in all SDK-style projects" | 65 | **False.** Only 9 of 11 set it: `Desktop:17`, `AzureDesignStudio:5`, `Core:6`, `Discovery:6`, `Mcp:7`, `Server:6`, `SharedModels:6`, `Workspace:6`, `AzureResources:5`. `Core.Tests` and `SourceGeneration` do not | Never set `ImplicitUsings` globally in `Directory.Build.props` — it would silently change those two projects' compilation |
| "Fix build/packages → root csproj files" | 57 | **False.** There are no csproj files at the repo root; all 11 are under `src/` (`git ls-files 'src/**/*.csproj'` → 11) | The new build files go at the root; the projects do not |
| "SDK projects: … `Microsoft.NET.Sdk.Razor` (Core), `Microsoft.NET.Sdk` (others)" | 63 | Imprecise: `Desktop.csproj:1` is also `Microsoft.NET.Sdk.Razor`; `Core.Tests`, `Workspace`, `Discovery`, `Mcp`, `SharedModels`, `SourceGeneration`, `AzureResources` use `Microsoft.NET.Sdk` | Not load-bearing; do not "correct" SDK attributes in this phase |
| "`ubuntu slnf parsing is broken in SDK 10.0.4xx`" | 88 | **Unverified** (research A4). The workflow's own comment, `build.yml:27-31`, attributes the exclusion to `net10.0-windows` and says nothing about sln parsing | Do not build the format-gate placement on this claim; the gate goes to Windows because the solution contains the Desktop project (Pitfall 6), on either explanation |
| "CI: build + CodeQL" | 43 (STRUCTURE tree) | Three workflows exist: `build.yml`, `codeql-analysis.yml`, `publish.yml` | Not edited this phase; `publish.yml` is Phase 11's |
| Directory.Build-style conventions at all | — | **None exist**: no `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes`, `NuGet.config`, or `packages.lock.json` (verified by `find` and `git ls-files`) | Every one of those files is NEW-BY-DESIGN; the only honest template is the research evidence ledger |

`Nullable` (AGENTS.md:64, "all projects except AzureResources") **is** true and is not changed: `AzureResources.csproj:1-7` sets only `TargetFramework` and `ImplicitUsings`, so nullable analysis is off there and produces no warnings (Q4).

---

## No Analog Found

Files with no **file-level** analog. Each is **NEW-BY-DESIGN**; use the cited research measurement, not an invented house style. (`Directory.Build.targets` appears here because no repo-wide target file has ever existed; its *idiom* is still copied from the four inline `DropForeignThreadingAnalyzers` targets, which is why its classification row reads role-match.)

| File | Role | Data Flow | Why no analog | Shape fixed by |
|---|---|---|---|---|
| `global.json` | config | batch | No SDK pin exists anywhere; the only JSON tool-config (`src/version.json`) is nbgv's, with a different schema | E1, E3, E21, E22; Q2 |
| `Directory.Build.props` | config | batch | Repo has never had shared MSBuild properties; AGENTS.md states its absence as a convention | E4, E5, E7-E9, E20, E24, E25, E27; Q1, Q4, Q6 |
| `Directory.Packages.props` | config | batch | 69 declared versions live in 10 csproj files; no central file has ever existed | E15 (53 distinct declared ids, 0 conflicts, 5 commented rows); Q1 |
| `Directory.Build.targets` | build target | event-driven | Custom targets exist only inline in four csproj files; no repo-wide target file | Q6 (expiry verified on both restore and build); Pitfall 7 |
| `.editorconfig` | config | batch | Absent; no `.gitattributes` and no prior formatting policy | E12, E13, E14, E28, E29; Q5 |
| 11 × `src/*/packages.lock.json` | generated data | batch | Zero lock files; `.gitignore` has no rule for or against them | E16, E17, E18, E32; Q1 |

---

## Deliberately Not Created / Not Edited

| Path | Why |
|---|---|
| `.gitattributes` | Creating one (`* text=auto`) triggers a whole-repo line-ending rewrite (E28). Pitfall 5 says omit, not normalise. |
| `NuGet.config` | No measurement calls for a feed list; adding one would change restore's source set without evidence. |
| `WarningsAsErrors` / `MSBuildTreatWarningsAsErrors` | The first enumerates codes forever and the second promotes the 91 `PRI263` tool warnings (Q4, E8/E9). |
| `.github/workflows/publish.yml` | The MSIX path is Phase 11's; its `10.0.x` selections (`publish.yml:15,26,65`) are noted, not edited. If a tag run later fails with `A compatible .NET SDK was not found`, the fix is a `global-json-file` swap in that file, not a change to `global.json`. |
| `.github/workflows/codeql-analysis.yml` | No gate this phase owns; also pins `actions/checkout@v3` (`codeql-analysis.yml:43`), which is unrelated drift. |
| `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest` | Phase 11 owns publisher identity; `publish.yml:68-73` is the only thing that currently rewrites it. |
| Any `.razor` formatting policy | Razor markup is outside the measured `dotnet format` diagnostic set (E12/E13). |

---

## Metadata

**Analog search scope:** repo root; `src/*/` (all 11 csproj read in full); `src/version.json`; `src/AzureDesignStudio.sln`; `.github/workflows/` (three files read in full); `.planning/phases/01-land-and-stabilize/` (`01-PATTERNS.md`, `01-VERIFICATION.md`, `01-01-PLAN.md`); `AGENTS.md`; `.planning/{ROADMAP,REQUIREMENTS,PROJECT}.md`; `.planning/phases/02-…/{02-RESEARCH,02-VALIDATION}.md`. All analog paths verified with `git ls-files` (tracked, no capability mirror).
**Files scanned:** 31 intended paths; 17 existing files read in full (`AGENTS.md`, `src/version.json`, `src/AzureDesignStudio.sln`, all 11 csproj, all 3 workflows) plus 7 planning documents/ranges.
**Measurements taken this session (not carried from research):** 11 csproj on disk = 11 tracked; 74 `PackageReference` lines in total = 69 declared references + 5 commented-out rows; 53 distinct declared ids; 0 duplicate versions; 4 projects reference `Nerdbank.GitVersioning`; `ImplicitUsings` present in 9 of 11 projects; zero build-config files anywhere at depth ≤4; zero lock files; `.gitignore` has no lock-file rule; `System.Security.Cryptography.Xml` resolves 10.0.10 in exactly two graphs (WASM, Desktop) and 10.0.13 clears all three high advisories there.
**Pattern extraction date:** 2026-10-21

---

## PATTERN MAPPING COMPLETE

**Phase:** 02 — Reproducible Build and CI Baseline
**Files classified:** 31 intended paths (32 rows; `build.yml` has two independent changes)
**Analogs found:** 16 / 32 rows (13 exact self-analog, 3 role-match); 15 new-by-design rows; 1 expected no-op (`AzureResources`)

### Coverage
- Exact (self or same-file) analog: 13 rows — 10 csproj edits, the Linux job in `build.yml`, `AGENTS.md`, `.planning/REQUIREMENTS.md`
- Role-match analog: 3 rows — `Directory.Build.targets` (inline target idiom), the new Desktop job (`publish.yml:55-100`), `02-DESKTOP-RUNS.md` (`01-VERIFICATION.md`)
- No file-level analog: 16 rows — 15 new-by-design (`global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, 11 × `packages.lock.json`) plus `Directory.Build.targets`, whose file is new but whose target idiom is copied; every one cites its E-ID and Q-number in the tables above

### Key Patterns Identified
- Every build input gets exactly one authority: SDK in `global.json`, versions in `Directory.Packages.props`, exact graph in per-project `packages.lock.json`, warning/audit policy in `Directory.Build.props`, formatting in `.editorconfig` — replacing three workflows that each carry `dotnet-version: '10.0.x'`.
- Each gate must fail with a cause-naming diagnostic (`NU1004`, `NU1903`, `WHITESPACE`, `CS…`, the expiry `<Error>`), and locked restore must be paired with `git diff --exit-code -- '*packages.lock.json'` because `--locked-mode` alone passes when a lock file is missing (E18).
- The repo's own MSBuild idiom (comment-then-`<Target BeforeTargets=…>`, `Ads…` naming) is the template for the one bespoke artefact; `$(MauiVersion)` must be pinned in `Directory.Build.props` before `Directory.Packages.props` reads it (E4/E5/E20).
- Phase 1 execution conventions carry forward: explicit path staging, worktree/empty-cache build proofs, commands recorded with their observed output in the phase evidence ledger.
- AGENTS.md's claim that `ImplicitUsings` is enabled in all SDK-style projects is false (9/11), there are no root csproj files, and no `Directory.Build.*`/`global.json`/`.editorconfig`/lock file has ever existed — recorded so no plan inherits a fiction.

### File Created
`.planning/phases/02-reproducible-build-and-ci-baseline/02-PATTERNS.md`

### Ready for Planning
The planner can reference each analog by file:line, and must treat every NEW-BY-DESIGN row as evidence-bound: the research E-ID fixes its shape, and no new house convention may be introduced for it.
