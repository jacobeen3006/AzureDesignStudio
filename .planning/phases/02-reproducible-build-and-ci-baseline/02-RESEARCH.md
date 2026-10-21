# Phase 2: Reproducible Build and CI Baseline — Research

**Researched:** 2026-10-21
**Domain:** .NET 10 build reproducibility (SDK pinning, central package management, lock files), NuGet auditing, and GitHub Actions CI for a MAUI + Blazor solution
**Overall confidence:** HIGH for everything measured on this machine; MEDIUM for runner-image behaviour (read from `actions/runner-images`, not from a live run); LOW for the download cost and duration of `dotnet workload restore`, which cannot be measured on a machine that already has the workloads installed.

---

## Summary

The phase is smaller and more mechanical than its roadmap note implies, and the one thing that looks dangerous (turning on warnings-as-errors) is already safe. Measured today: a clean Release rebuild of all 11 projects produces **91 warnings and 0 errors**, and **every one of the 91 is `PRI263` from the MAUI desktop project's `WinAppSDKGenerateProjectPriFile` target** — there is not a single C# compiler, analyzer, style, or NuGet-audit warning anywhere in the solution. Flipping `TreatWarningsAsErrors` on today therefore changes nothing (measured: the Desktop build still succeeds with 91 warnings). Nothing in the repo needs to be cleaned up before the warning gate lands.

Three of the roadmap's stated premises need correcting against measurement. First, `version.json` is **not** absent: it lives at `src/version.json`, is referenced by the solution file as `version.json` relative to `src/`, and resolves `Version` to `1.0.0`, so the `fetch-depth: 0` comment in `build.yml` is correct and AGENTS.md is correct — the root simply never had one. Second, `dotnet format --verify-changed` does not exist; the real flag is `--verify-no-changes` (verified against the installed CLI). Third, `BUILD-01` is already marked **Complete** in `REQUIREMENTS.md` while the artefacts that would prove it (`global.json`, `Directory.Packages.props`, `packages.lock.json`) do not exist anywhere in the tree — the requirement is not satisfied and the traceability table will need correcting as part of this phase.

The two real risks are ordering and false-green. Ordering: locked-mode restore applied before lock files are committed does not fail — it silently writes the lock files (measured), so a CI gate built only on `--locked-mode` can pass forever while pinning nothing; it needs a committed-lock-file diff guard beside it. False-green: `ContinuousIntegrationBuild=true` does **not** enable locked mode (measured: restore exit 0, lock file rewritten), so the flag must be passed explicitly in CI. The Windows Desktop job, as written in the roadmap (`dotnet build` of the Desktop project in Release), costs ~7m46s on this machine, because the Desktop project's `ProjectReference` pulls the Blazor WASM project into a Release build that AOT-compiles it; the same build with `-p:RunAOTCompilation=false` takes 74 s, so the AOT path accounts for the 392 s difference. That measurement is what makes a per-PR Windows job credible.

**Primary recommendation:** Land the phase as ten ordered, individually verifiable steps — CPM plus `MauiVersion` pin, then lock files, then `global.json`, then locked CI, then `.editorconfig` plus one 38-file whitespace commit, then the format gate, then warnings-as-errors (already green), then the audit gate with a build-enforced dated suppression, then the non-blocking Windows job with a duration artifact, then the human step that flips it to blocking.

---

## Design Constraints (no CONTEXT.md)

There is no `CONTEXT.md` for this phase — `/gsd-discuss-phase` was skipped deliberately — so there are no user decisions to honour verbatim. The binding inputs are instead:

1. `.planning/ROADMAP.md` Phase 2 implementation note (verbatim, the only pre-existing direction): *"`global.json` (10.0.401/`latestPatch`), `Directory.Build.props`, `Directory.Packages.props`, `packages.lock.json`, `.editorconfig`. In `.github/workflows/build.yml` add a `windows-latest` job with NuGet cache and `dotnet workload restore` (runner images ship MAUI 9 workloads), keep the Linux job as the fast gate, and use `dotnet format --verify-changed`. Keep `WindowsPackageType=None` as the csproj default — packaging in Phase 11 passes `-p:WindowsPackageType=Package`. Do not add MSIX packing until the duration is known."*
2. `BUILD-01`…`BUILD-05` in `.planning/REQUIREMENTS.md` and SC#1…SC#5 in the roadmap.
3. Hard constraints already recorded in `.planning/PROJECT.md`: 11 projects, .NET 10, Desktop is Windows-only, `WindowsPackageType=None` until Phase 11, one test project today.

Where measurement contradicts the roadmap note, the measurement wins and the correction is recorded in the relevant question below (Q3 workload version, Q5 format flag).

---

## Evidence Ledger

Every number the planner may reuse, with the command that produced it. All commands were run from the repository root on this machine, 2026-10-21.

| # | Measurement | Command | Result |
|---|---|---|---|
| E1 | Local SDK | `dotnet --version` / `dotnet --list-sdks` | `10.0.401`, and it is the **only** SDK installed |
| E2 | Local workloads | `dotnet workload list` | `maui 10.0.110/10.0.100`, `wasm-tools 10.0.112/10.0.100` |
| E3 | Workload set | `dotnet workload --version` / `dotnet workload config --update-mode` | `10.0.401.1`, mode `workload-set` |
| E4 | `MauiVersion` in an outer (multi-target) evaluation | `dotnet msbuild …Desktop.csproj -getProperty:MauiVersion` | `""` (empty) |
| E5 | `MauiVersion` in an inner build | same, `-p:TargetFramework=net10.0-windows10.0.19041.0` | `10.0.110` |
| E6 | Release solution build (AOT on, warm obj) | `dotnet build src/AzureDesignStudio.sln -c Release` | **91 Warning(s), 0 Error(s), Time Elapsed 00:11:32.53** |
| E7 | Clean Release rebuild, AOT disabled | same `--no-incremental -p:RunAOTCompilation=false` | **91 Warning(s), 0 Error(s), 00:01:19.68** |
| E8 | Warning composition | histogram over the build log | 182 lines, all `WINAPPSDKGENERATEPROJECTPRIFILE PRI263`; `warning CS|NU|IDE|CA|MSB` counts all **0** |
| E9 | Desktop Release build, warnings-as-errors on | `dotnet build …Desktop.csproj -c Release -p:TreatWarningsAsErrors=true` | **succeeded**, 91 Warning(s), 0 Error(s), `00:07:46.14` |
| E10 | Desktop Release build, AOT disabled | same, `--no-incremental -p:RunAOTCompilation=false` | **74 s wall**, 91 Warning(s), 0 Error(s) |
| E11 | Offline suite | `dotnet test src/AzureDesignStudio.Core.Tests -c Release --no-build --filter "Category!=LiveAzure"` | `Failed: 0, Passed: 755, Skipped: 0, Total: 755` |
| E12 | Solution-wide format check | `dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore` | **exit 2**, 316 `error WHITESPACE`, 38 files; 51 s on the first (cold workspace load), 30 s on a repeat |
| E13 | Format diagnostics by project | histogram over E12 log | Core 152, Core.Tests 45, Server 44, Desktop 26, Discovery 23, WASM 17, Workspace 4, SourceGeneration 4, AzureResources 1; Mcp 0, SharedModels 0 |
| E14 | Format check scoped to one changed file | `--include src/AzureDesignStudio.Core/APIM/APIMModel.cs` | **exit 2**, 19 diagnostics, 18 s |
| E15 | Package population | `grep PackageReference` over the 11 csproj, then the same grep filtered with `grep -v '<!--'` | 74 `PackageReference` lines in total = **69 declared references** carrying `Version=` + 5 commented-out rows; **53 distinct declared ids**; 58 distinct ids when the commented rows are counted, which are not declarations; **no id with two different versions** |
| E16 | CPM + lock file compose | temp project, CPM + `RestorePackagesWithLockFile` | `packages.lock.json` written with `"resolved": "13.0.3"` from the central file |
| E17 | Locked mode on a changed central version | `dotnet restore --locked-mode` after editing `Directory.Packages.props` | **exit 1, `error NU1004`** |
| E18 | Locked mode with **no** lock file | `dotnet restore --locked-mode` after deleting it | **exit 0**, lock file created — no failure |
| E19 | Does `ContinuousIntegrationBuild` imply locked mode? | `dotnet restore -p:ContinuousIntegrationBuild=true` with a stale lock | **exit 0**, lock rewritten to `13.0.1` — no |
| E20 | `global.json` name in `Directory.Build.props` is visible to `Directory.Packages.props` | temp probe, `-getItem:PackageVersion` | `Probe.Ordering` resolved to `from-build-props` — **yes, props→packages ordering holds** |
| E21 | `rollForward: latestPatch` across feature bands | `global.json` 10.0.300 + `latestPatch` | `A compatible .NET SDK was not found. Requested SDK version: 10.0.300` |
| E22 | `rollForward: latestPatch` on the pinned version | `global.json` 10.0.401 + `latestPatch` | `10.0.401` |
| E23 | `dotnet format --verify-changed` exists? | `dotnet format --help` | **absent**; `--verify-no-changes`, `--include`, `--exclude`, `--no-restore`, `--report`, `--severity` present |
| E24 | NuGet audit defaults in the installed SDK | `grep` of `C:\Program Files\dotnet\sdk\10.0.401\NuGet.targets` | `NuGetAudit` default `true`; `NuGetAuditLevel` default `low`; `NuGetAuditMode` default `all` for `net10.0` |
| E25 | AutoMapper audit warning, promoted | `dotnet restore …Core -p:NoWarn= -p:TreatWarningsAsErrors=true -p:NuGetAuditLevel=high` | **exit 1**, `error NU1903: Warning As Error: Package 'AutoMapper' 12.0.1 has a known high severity vulnerability, https://github.com/advisories/GHSA-rvv3-g6hj-g44x` |
| E26 | Same, restricted to audit codes | `-p:WarningsAsErrors=NU1903%3BNU1904` | identical failure — code-scoped promotion works |
| E27 | `NuGetAuditSuppress` silences it | `-p:DirectoryBuildPropsPath=<temp props with the advisory URL>` | **exit 0**, no NU1903 |
| E28 | Line endings | `git ls-files '*.cs' '*.razor' --eol` | index **LF for all 820**; worktree 519 `crlf` / 301 `lf` (machine has `core.autocrlf=true`, no `.gitattributes`) |
| E29 | BOM usage | first-3-bytes probe over the same 820 files | 102 files carry a UTF-8 BOM, 718 do not |
| E30 | Versioning | `dotnet msbuild …Core.csproj -getProperty:Version` | `1.0.0` — nbgv is live from `src/version.json` |
| E31 | Clone depth | `git rev-parse --is-shallow-repository` | `false` |
| E32 | `packages.lock.json` is not ignored | `git check-ignore -v` | not ignored — lock files will be tracked |
| E33 | Audit census at `NuGetAuditLevel=high`, `NuGetAuditMode=all`, `NoWarn=` | `dotnet restore src/AzureDesignStudio.sln --force -p:NoWarn=` and per-project restores | Two high advisories: AutoMapper 12.0.1 (GHSA-rvv3-g6hj-g44x) and `System.Security.Cryptography.Xml` 10.0.10 (GHSA-7hqp-7c5m-4vq6 plus two siblings). The crypto package is transitive through Azure.Bicep/Azure.Deployments/Microsoft.Windows.Compatibility and appears in exactly the WASM and Desktop graphs. |
| E34 | Remediation for the vulnerable crypto transitive | scratch project + a repo worktree with CPM | `CentralPackageTransitivePinningEnabled` fails the tree with `NU1109` (Microsoft.Extensions.Logging.Debug 10.0.12 vs 10.0.0; Microsoft.AspNetCore.Components.WebAssembly 10.0.13 vs 10.0.12). A central `PackageVersion` 10.0.13 row plus a direct `<PackageReference>` in WASM and Desktop resolves 10.0.13 in both, with `NuGetAuditMode=all`, `Level=high`, the single AutoMapper suppression, and `NoWarn=` cleared: restore exit 0, no `NU19xx`, Release builds of both projects exit 0 with no compiler warning. |
| E35 | SDK audit-mode default | `grep -A4 NuGetAuditMode "C:\Program Files\dotnet\sdk\10.0.401\NuGet.targets"` | `all` only when `TargetFrameworkIdentifier` is `.NETCoreApp` and version ≥ 10.0; otherwise `direct` — so `SourceGeneration` (`netstandard2.0`) audits direct dependencies only unless the property is set |

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| BUILD-01 | A clean clone restores and builds with the SDK pinned at 10.0.401 and locked package versions. | Q1 (lock-file mechanics), Q2 (`global.json` shape and roll-forward semantics), Q9 (order of landing). Note: marked Complete in REQUIREMENTS.md today, which measurement contradicts — see Q9. |
| BUILD-02 | The Desktop host builds on pull requests, and a deliberate error in `MauiProgram.cs` fails the job. | Q3 (MAUI workload on the runner), Q7 (job shape, non-blocking→blocking, compile-error proof), Q8 (duration artifact). |
| BUILD-03 | Package versions are centralized, app projects treat warnings as errors, and changed files pass a format check. | Q1 (CPM mechanics incl. `$(MauiVersion)`), Q4 (warning baseline = 91 PRI263, TWA:E safe), Q5 (format gate command and one-time normalisation). |
| BUILD-04 | CI fails on High/Critical vulnerable packages, with one dated AutoMapper allowlist entry that expires. | Q6 (audit defaults, NU1903 promotion, `NuGetAuditSuppress`, build-enforced expiry). |
| BUILD-05 | The non-blocking Windows Desktop job records its duration and becomes blocking after two consecutive green runs. | Q7 (what the workflow can do vs what a human must do), Q8 (how the duration becomes an artefact). |
</phase_requirements>

---

## Answers to the nine decision questions

### Q1. Lock files + central package management

**Compose: yes, and CPM is what makes the lock file worth having.** Measured in a throwaway project (E16): with `ManagePackageVersionsCentrally=true` and `RestorePackagesWithLockFile=true`, restore writes `packages.lock.json` containing `"requested": "[13.0.3, )"`, `"resolved": "13.0.3"`, `"contentHash": "HrC5BXdl00IP9zeV+0Z848QWPAoCr9P3bDEZguI+gkLcBKAOxix/tLEAAHC+UvDNPv4a2d18lOReHMOagPa+zQ=="`. Editing the central file to `13.0.1` and restoring in locked mode fails with (E17):

```
error NU1004: The package reference Newtonsoft.Json version has changed from [13.0.3, ) to [13.0.1, ).
The packages lock file is inconsistent with the project dependencies so restore can't be run in locked mode.
Disable the RestoreLockedMode MSBuild property or pass an explicit --force-evaluate option to run restore to update the lock file.
```

**Exact property names, and where each belongs** (`[VERIFIED: C:\Program Files\dotnet\sdk\10.0.401\NuGet.targets:950-980]`, which passes both through to the restore task: `<RestorePackagesWithLockFile>`, `<NuGetLockFilePath>`, `<RestoreLockedMode>`):

| Property | File | Value | Why there |
|---|---|---|---|
| `RestorePackagesWithLockFile` | `Directory.Build.props` | `true` | Every project gets a lock file, and a *new* project cannot silently opt out. |
| `RestoreLockedMode` | **nowhere in the repo** — CI command line only | `true` via `--locked-mode` or `-p:RestoreLockedMode=true` | Locally a developer must be able to add a package with a plain `dotnet restore`; committing `RestoreLockedMode=true` makes every local package edit require `--force-evaluate`. |

**How "restore fails if a lock file would change" is enforced, and its one hole.** `dotnet restore --locked-mode` (CLI flag exists, `[VERIFIED: dotnet restore --help]`: `--locked-mode  Don't allow updating project lock file.`) fails with NU1004 when a committed lock file disagrees with the graph — measured (E17). But **with no lock file present, `--locked-mode` succeeds and writes one** (E18: exit 0, file created). A CI gate made only of `--locked-mode` therefore cannot fail for the "forgot to commit the lock file" case; the gate needs a second step that proves the working tree did not change:

```bash
dotnet restore <projects> --locked-mode
git diff --exit-code -- '*packages.lock.json'   # catches a lock file that --locked-mode just created
```

Likewise `ContinuousIntegrationBuild=true` does **not** turn locked mode on (E19: restore exit 0 and the stale lock file was rewritten), so the flag must be explicit.

**What breaks for `netstandard2.0` SourceGeneration.** Nothing structural: it receives its own `packages.lock.json` like any project, and its four packages (`Microsoft.CodeAnalysis.Analyzers` 3.11.0, `Microsoft.CodeAnalysis.CSharp` 4.11.0, `Scriban` 7.5.0, `Microsoft.CSharp` 4.7.0) pin normally. Its `PackageScribanIncludeSource=true` and `EnforceExtendedAnalyzerRules=true` are build-time concerns, unaffected by lock files. A project with **zero** package references also gets a lock file (measured on an empty project: a 66-byte file), so `AzureDesignStudio.AzureResources` (no `PackageReference`) is not a special case.

**What breaks for the MAUI Desktop project — this is the one real trap.** The csproj uses `<PackageReference Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />` and the same for `Microsoft.AspNetCore.Components.WebView.Maui` (`[VERIFIED: src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:67-68]`). Measured: `$(MauiVersion)` is **empty in the outer (multi-target) evaluation** (E4) and only resolves to `10.0.110` in the inner build (E5), because it comes from `BundledVersions.targets` in the MAUI SDK pack, which is imported as `Sdk.targets`. `Directory.Packages.props` is imported from `NuGet.props` during the early props phase, so a `PackageVersion` entry written as `Version="$(MauiVersion)"` would evaluate to empty and fail. Two facts make the fix safe:

- `Directory.Build.props` **is** evaluated before `Directory.Packages.props` (E20 — a property set in the former resolved inside the latter), so defining `<MauiVersion>10.0.110</MauiVersion>` in `Directory.Build.props` is visible to `Directory.Packages.props`, *and* MAUI's own `BundledVersions.targets` sets it only `Condition=" '$(MauiVersion)' == '' "` (`[VERIFIED: C:\Program Files\dotnet\packs\Microsoft.Maui.Sdk\10.0.110\Sdk\BundledVersions.targets:7]`), so the pinned value wins.
- Pinning it also removes the lock-file churn that would otherwise occur when the workload manifest moves: the Desktop lock file records `Microsoft.Maui.Controls 10.0.110`, and without a pin an SDK feature-band change silently changes it and breaks locked restore.

| Decision | Value |
|---|---|
| `MauiVersion` | `10.0.110` (`[VERIFIED: measured E5]`; matches `dotnet workload list` manifest `10.0.110/10.0.100`) |
| CPM entries | 53 `<PackageVersion>` rows from the declared references (E15, corrected: the 5 commented-out rows are not declarations), plus the `System.Security.Cryptography.Xml` 10.0.13 security pin (E33/E34); no package id currently has two versions, so the move is mechanical with zero conflict resolution |
| Lock files | 11 files, one per project directory, tracked (`packages.lock.json` is not in `.gitignore` — E32) |

### Q2. SDK pinning

**Exact shape** (`global.json` at the repository root, beside `src/`):

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestPatch",
    "allowPrerelease": false,
    "workloadVersion": "10.0.401.1"
  }
}
```

`workloadVersion` is the workload-set pin (E3: local set is `10.0.401.1`, update mode `workload-set`; `[CITED: learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-sets]` documents the `sdk.workloadVersion` key). Treat that one line as a **separate, revertible step**: it is the line that makes `dotnet workload restore` on the runner reproducible, and it is also the line that can fail if the set cannot be resolved from nuget.org. Land `version`/`rollForward` first, verify; add `workloadVersion` second, verify.

**What `latestPatch` actually does — measured, not assumed.** With only 10.0.401 installed, `"version": "10.0.300"` + `latestPatch` fails with `A compatible .NET SDK was not found. Requested SDK version: 10.0.300` (E21). So `latestPatch` does **not** roll across feature bands; a CI image missing the `10.0.4xx` band is a hard failure, not a downgrade. `"version": "10.0.401"` + `latestPatch` resolves `10.0.401` (E22). Rolling to a later patch *within* 4xx (e.g. 10.0.402) is documented behaviour (`[CITED: learn.microsoft.com/en-us/dotnet/core/tools/global-json#rollforward]`) but unverifiable here because no such SDK exists on this machine.

**What happens on the runner images today.** Read from `actions/runner-images` `main` at image versions 20261012/20261013:

- `ubuntu-latest` → Ubuntu 24.04; `.NET Core SDK: 8.0.131, …, 10.0.112, 10.0.204, 10.0.303, **10.0.401**` (`[CITED: raw.githubusercontent.com/actions/runner-images/main/images/ubuntu/Ubuntu2404-Readme.md]`).
- `windows-latest` → Windows Server 2025; same SDK list ending `**10.0.401**` (`[CITED: …/images/windows/Windows2025-Readme.md]`).
- Label mapping: `windows-latest` = `windows-2025`; `ubuntu-latest` = `ubuntu-24.04`, with an announced migration of `ubuntu-latest` to 26.04 in November 2026 (`[CITED: raw.githubusercontent.com/actions/runner-images/main/README.md]`).

So 10.0.401 is present on both images *today* and `global.json` alone would work — for now. Pin it anyway: those lists change weekly, and the failure mode (E21) is a hard stop.

**`actions/setup-dotnet` can install it, and the exact `dotnet-version` value matters.** The action's supported syntax table says `A.B.C` "installs exact version of .NET SDK" and `A.B.Cxx` "installs the latest version of the specific SDK release" (`[CITED: raw.githubusercontent.com/actions/setup-dotnet/main/README.md]`). Therefore:

- Use `with: { global-json-file: global.json }` — the action reads `sdk.version`/`rollForward` and installs what it resolves. Note the README's constraint that with `rollForward`, `sdk.version` must be fully qualified (`10.0.401`, not `10.0.x`), which ours is.
- If you prefer `dotnet-version`, write `'10.0.401'` (exact) or `'10.0.4xx'` (band). **Not** `'10.0.x'`: that resolves the highest 10.0 patch available and can drift away from the pinned band. The repo currently uses `dotnet-version: '10.0.x'` in all three workflows — that line becomes redundant once `global.json` exists and should be replaced, not left to compete with it.
- The action's current major is **v6**; the repo pins `@v4` (`[VERIFIED: .github/workflows/build.yml:22-24]`). v4 still works; bumping is optional and out of this phase's scope, but if a pin is rewritten, use `@v5`/`@v6` and note the node24 runner requirement.

### Q3. MAUI on `windows-latest`

**The roadmap note is accurate but insufficient.** The Windows Server 2025 image readme lists VS components `maui.blazor`, `maui.core`, `maui.windows` at **`9.0.111.6930`**, `wasm.tools 9.0.2026.41315`, and `runtimes.ios`/`runtimes.maccatalyst 9.0.2026.41315` (`[CITED: …/images/windows/Windows2025-Readme.md]`). Those are .NET 9 MAUI workloads. A `net10.0-windows10.0.19041.0` project needs the .NET 10 MAUI SDK, so `dotnet workload restore` must run before the build. Confirmed locally that this is what supplies the target: the MAUI SDK pack present is `Microsoft.Maui.Sdk/10.0.110`, and the workload manifest is `10.0.110` (E2, E5).

**What `dotnet workload restore` must be given.** A project or solution, not a workload id: `dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` (`[CITED: learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-restore]` — "analyzes a project or solution to determine which workloads it needs, then installs any workloads that are missing"). Do **not** run it against `src/AzureDesignStudio.sln` on the Linux job: the solution contains the Windows-only Desktop project, and a solution-wide workload restore there would demand `maui` on a platform that cannot host it. On Linux the WASM AOT path needs `wasm-tools` instead — the existing Linux job has neither a workload step nor an explicit wasm-tools install, which is fine only because a plain `dotnet build` has been succeeding there; keep it explicit (`dotnet workload restore src/AzureDesignStudio/AzureDesignStudio.csproj`) if that assumption ever proves wrong.

**`WindowsPackageType=None`.** It selects unpackaged (no MSIX) output — `[CITED: learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli]` ("The `<WindowsPackageType>` property specifies that the app should be published as a packaged MSIX app"). It does **not** change workload resolution and it does not skip the PRI/resource pipeline: measured, the Desktop build with `WindowsPackageType=None` (`[VERIFIED: src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:41]`) still emits 91 `WINAPPSDKGENERATEPROJECTPRIFILE PRI263` warnings (E8). Keep it as the default; packaging in Phase 11 overrides it on the command line.

**`WindowsAppSDKSelfContained`.** Publish-only: it bundles the Windows App SDK runtime into the output instead of depending on a machine-installed one. It appears only in the publish command (`[VERIFIED: AGENTS.md "COMMANDS"]`, `.github/workflows/publish.yml`) and is irrelevant to a PR compile job. Leave it to Phase 11.

**Cost.** Unmeasurable on this machine (workloads already installed): `dotnet workload restore` for MAUI 10 downloads the MAUI pack set from nuget.org; expect minutes, not seconds `[ASSUMED]`. Cheapest verification: read the first green Windows job's "Restore workloads" step duration (which Q8's metrics artefact will already record).

### Q4. Warnings as errors

**The measured baseline makes the decision easy: there is nothing to suppress.** A clean Release rebuild of all 11 projects (`--no-incremental`) produces 91 warnings, and all 91 are `WINAPPSDKGENERATEPROJECTPRIFILE : warning : PRI263: 0xdef01051 - No default or neutral resource given for 'Files/wwwroot/_framework/….resources.….wasm'` from the Desktop project (E7, E8). Zero `CS`, `NU`, `IDE`, `CA` or `MSB` warnings exist. Consequences:

- **Nullable-disabled `AzureResources` is not a noise source** — it has no `<Nullable>` element at all (`[VERIFIED: src/AzureDesignStudio.AzureResources/AzureDesignStudio.AzureResources.csproj:4-6]`, only `TargetFramework`/`ImplicitUsings`), so nullable analysis is off and no CS86xx can be emitted. Do not "fix" this in Phase 2; enabling nullable there would create the explosion the brief worried about.
- **Generated ARM types are not a noise source** — the templates emit `[GeneratedCode("ArmTypeGenerator")]` types and none produced a build diagnostic.
- **AntDesign 1.6.2 is not a noise source** — no analyzer diagnostics appeared.

**The right scope.** `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`, repo-wide, including `SourceGeneration` and `AzureResources`. Do not use `WarningsAsErrors` for the C# side (that would enumerate codes forever); do not set `MSBuildTreatWarningsAsErrors` — that switch promotes *task* warnings, which would convert the 91 `PRI263` warnings into build failures for a resource-packaging quirk unrelated to code quality. Measured proof that the plain property is safe: `dotnet build …Desktop -c Release -p:TreatWarningsAsErrors=true` **succeeded** with 91 warnings and 0 errors (E9) — i.e. `TreatWarningsAsErrors` (a Roslyn switch) does not touch tool warnings.

**The two properties the brief asks about are not gates:**

- `EnforceCodeStyleInBuild` — default `false`; setting it `true` makes IDE code-style rules run in the build (`[CITED: learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#enforcecodestyleinbuild]`). Do **not** enable it in this phase: there is no `.editorconfig` style configuration yet, and it would crack open a second, unreviewed gate in the same PR that introduces the format gate.
- `CodeAnalysisTreatWarningsAsErrors` — an **opt-out**, not a gate: it exists so CAxxxx warnings can stay warnings under `-warnaserror` (`[CITED: …/msbuild-props#codeanalysistreatwarningsaserrors]`). Since there are zero CA warnings, leave it unset.

**If noise appears later**, exclude narrowly by code (`<NoWarn>` / `<WarningsNotAsErrors>` per project) rather than lowering the gate globally; but no such exclusion is justified by today's measurement.

### Q5. Formatting gate

**`--verify-changed` does not exist.** `dotnet format --help` on 10.0.401 lists `--verify-no-changes`, `--include`, `--exclude`, `--exclude-diagnostics`, `--no-restore`, `--report`, `--severity`, `--include-generated` — and no `--verify-changed` (E23). The roadmap's command must be corrected.

**Command that fails a changed file in CI** (measured shape, E14):

```bash
git fetch --no-tags --depth=1 origin "$GITHUB_BASE_REF"
CHANGED=$(git diff --name-only --diff-filter=ACMR "origin/$GITHUB_BASE_REF...HEAD" -- '*.cs' | tr '\n' ' ')
[ -z "$CHANGED" ] && { echo "no C# changes"; exit 0; }
dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore --include $CHANGED
```

Measured behaviour: `--include` restricts the report to the named file (one file → 19 diagnostics, 18 s, exit 2), while the whole solution reports 316 across 38 files in 51 s cold / 30 s warm. Exit code for "would change" is **2** (not 1) — a CI step treating any non-zero as failure is correct; do not special-case.

**Seeding `.editorconfig` so the first run is not a mass reformat.** The first run today is *already* small: 316 whitespace diagnostics in **38 files** out of 820 (738 `.cs` + 82 `.razor`), spread as Core 152, Core.Tests 45, Server 44, Desktop 26, Discovery 23, WASM 17, Workspace 4, SourceGeneration 4, AzureResources 1 (E12, E13). Every one is `WHITESPACE`, and spot-checking shows they are trailing spaces and stray indentation, not a style-convention conflict:

```text
    public string SkuName␠     ← trailing space
    {␠                        ← trailing space
```

Therefore the correct seeding is *minimal and permissive*:

```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
tab_width = 4
trim_trailing_whitespace = true
insert_final_newline = true
```

and then a **one-time normalisation commit** produced by `dotnet format whitespace src/AzureDesignStudio.sln` (38 files). Deliberately omitted, each for a measured reason:

- `end_of_line` — the index is LF for all 820 files but this Windows worktree checks out 519 as CRLF (`core.autocrlf=true`, no `.gitattributes`; E28). Setting `end_of_line = crlf` would fail every file in CI on Linux; setting `lf` would rewrite 519 working copies on every Windows machine. Leaving it unset keeps the formatter following each file's existing convention — which is why today's check flags 38 files and not 500.
- `charset` — 102 of 820 files carry a UTF-8 BOM and 718 do not (E29). Any `charset` value forces a wholesale rewrite of the majority.
- Style rules (`csharp_style_*`, `dotnet_diagnostic.IDE####.severity`) — that is `dotnet format style` territory, needs consensus, and belongs to a later phase.

### Q6. Vulnerability gate

**How NuGet audit behaves in .NET 10, read from the installed SDK** (`[VERIFIED: C:\Program Files\dotnet\sdk\10.0.401\NuGet.targets:73-82]`):

```xml
<NuGetAudit Condition=" '$(NuGetAudit)' == '' ">true</NuGetAudit>
<NuGetAuditLevel Condition=" '$(NuGetAuditLevel)' == '' ">low</NuGetAuditLevel>
<NuGetAuditMode Condition=" '$(NuGetAuditMode)' == '' … AND VersionGreaterThanOrEquals('$(TargetFrameworkVersion)', '10.0')">all</NuGetAuditMode>
<NuGetAuditMode Condition=" '$(NuGetAuditMode)' == '' ">direct</NuGetAuditMode>
```

So on this repo audit is already on, at **`low`** level, in **`all`** mode for the `net10.0` projects and `direct` for `netstandard2.0` SourceGeneration. Codes map to severity: NU1901 low, NU1902 moderate, NU1903 high, NU1904 critical (`[CITED: learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1901-nu1904]`).

**Yes, `TreatWarningsAsErrors` promotes them** — measured: `-p:TreatWarningsAsErrors=true -p:NuGetAuditLevel=high -p:NoWarn=` on the Core project fails restore with `error NU1903: Warning As Error: Package 'AutoMapper' 12.0.1 has a known high severity vulnerability, https://github.com/advisories/GHSA-rvv3-g6hj-g44x` (E25). The code-scoped alternative also works: `-p:WarningsAsErrors=NU1903%3BNU1904` produces the identical failure (E26). `NuGet.targets` passes `TreatWarningsAsErrors`, `WarningsAsErrors` and `NoWarn` into the restore graph (`[VERIFIED: …/NuGet.targets:946-949]`), which is why restore-time audit honours them.

**Make CI fail on High/Critical only.** Because `TreatWarningsAsErrors` is repo-wide for BUILD-03, the level must be raised so lower severities are never emitted at all:

```xml
<NuGetAuditLevel>high</NuGetAuditLevel>
```

With `high` + `TreatWarningsAsErrors`, NU1903/NU1904 fail the build and NU1901/NU1902 are not reported at all — which is exactly SC#3, and it is also the only combination that does not make a *moderate* advisory break every developer's local build.

**Per-package allowlist with a dated expiry.** NuGet has a suppression mechanism but no expiry: `<NuGetAuditSuppress Include="https://github.com/advisories/GHSA-rvv3-g6hj-g44x" />` (`[CITED: learn.microsoft.com/en-us/nuget/concepts/auditing-packages#excluding-advisories]`; item support confirmed in `NuGet.targets` via the `CollectNuGetAuditSuppressions` target, `[VERIFIED: …/NuGet.targets:373-395]`). Measured end to end: with the suppression supplied, the same Core restore that failed in E25 **passes** at the same severity settings (E27). No expiry property exists anywhere in the mechanism — verified by reading the whole suppression target and the documentation section.

**Smallest credible expiry the build can enforce** — a target in `Directory.Build.targets`, verified to fire on both `restore` and `build`:

```xml
<Project>
  <PropertyGroup>
    <AdsAuditSuppressionAutoMapperExpires>2027-01-31</AdsAuditSuppressionAutoMapperExpires>
    <_AdsToday>$([System.DateTime]::UtcNow.ToString("yyyy-MM-dd"))</_AdsToday>
    <_AdsExpiryCompare>$([System.String]::Compare('$(AdsAuditSuppressionAutoMapperExpires)', '$(_AdsToday)'))</_AdsExpiryCompare>
  </PropertyGroup>
  <Target Name="AdsCheckAuditSuppressionExpiry" BeforeTargets="Restore;Build;PrepareForBuild">
    <Error Condition="$(_AdsExpiryCompare) &lt; 0"
           Text="NuGetAuditSuppress for AutoMapper (GHSA-rvv3-g6hj-g44x) expired on $(AdsAuditSuppressionAutoMapperExpires); today is $(_AdsToday)." />
  </Target>
</Project>
```

Measured output when expired: `error : NuGetAuditSuppress for AutoMapper (GHSA-rvv3-g6hj-g44x) expired on 2020-01-01; today is 2026-10-21.` (exit 1); with a future date the same restore is clean (exit 0). The `[System.String]::Compare` indirection is not cosmetic — the two obvious spellings both fail with `MSB4086` (a numeric comparison was attempted on a date) and are recorded as a pitfall below.

**Two pre-existing suppressions must move.** `NU1903` is currently silenced per project with `<NoWarn>$(NoWarn);NU1903</NoWarn>` in the WASM app (`[VERIFIED: src/AzureDesignStudio/AzureDesignStudio.csproj:21]`) and in Core (`[VERIFIED: src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj:12]`), each with a comment naming the same advisory. If those lines survive, the WASM and Core projects bypass the new gate entirely and the dated allowlist proves nothing. Delete both; the `NuGetAuditSuppress` item in `Directory.Build.props` replaces them and is the single allowlist entry SC#3 asks for.

**Note on suppression breadth:** per the docs, an advisory suppression applies to the advisory, not the package, so any other package sharing GHSA-rvv3-g6hj-g44x would also be silenced. That is acceptable here and should be written next to the item.

### Q7. The Windows CI job

**Job shape** (this is the shape all measurements support; see Code Examples for the full YAML):

| Element | Value | Why |
|---|---|---|
| `runs-on` | `windows-latest` | Must be Windows: `net10.0-windows10.0.19041.0`, MAUI workload, WinAppSDK PRI generation. |
| `continue-on-error` | `true` initially, **deleted** when the job becomes blocking | Job-level `continue-on-error` is what makes a job non-blocking in Actions; it is a workflow-file change. |
| `timeout-minutes` | `30` | Measured local Desktop build is 74 s without AOT; 30 min is headroom for workload restore plus runner slowness, not a target. |
| `checkout` | `fetch-depth: 0` | nbgv is live (`Version` = `1.0.0` from `src/version.json`, E30), so the shallow-clone warning is real. |
| SDK | `actions/setup-dotnet` with `global-json-file: global.json` | Installs exactly what `global.json` resolves (Q2). |
| Workloads | `dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` | The image ships MAUI **9** only (Q3). |
| Build | `dotnet build src/AzureDesignStudio.Desktop/… -c Release --no-restore -p:RunAOTCompilation=false` | Measured 74 s vs 466 s with AOT. AOT is a publish concern re-verified in Phase 11. |
| Format gate | `dotnet format … --verify-no-changes --include <changed files>` | It must run on Windows: the solution contains the Windows-only Desktop project. |
| Cache | `setup-dotnet` `cache: true` + `cache-dependency-path: '**/packages.lock.json'` | The action hashes lock files for the cache key and errors if none exist (`[CITED: setup-dotnet README]`). |
| Artefacts | `desktop-build-metrics.json` + job summary | SC#2's duration proof (Q8). |

**Workload caching: do not, initially.** The only cacheable surface is the .NET install directory (`…/packs`, `…/metadata`); on this machine `C:\Program Files\dotnet\packs` alone is **5,081 MB**, which is a bad cache payload for a job that runs per PR, and the runner image replaces that directory on image updates, so a cache key tied to `global.json` alone can resurrect packs from a different image. Measure first (Q8 gives the number on run one); add the cache only if workload restore dominates.

**Non-blocking → blocking: exactly what the workflow can and cannot do.**

| Action | Who | How |
|---|---|---|
| Run the job without failing the PR | Workflow file | `continue-on-error: true` at job level |
| Record that a run was green | Workflow file | The metrics artefact (Q8) plus the run URL |
| Require the check before merge | **Human / repository setting** | Settings → Branches (branch protection) or Rulesets: "Require status checks to pass before merging" and add the job name; equivalently `gh api -X PATCH repos/{owner}/{repo}/branches/main/protection`. `[CITED: docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches]` — "Required status checks must have a successful, skipped, or neutral status before collaborators can make changes to a protected branch." |
| Delete `continue-on-error` | Workflow file (in the same change that adds the check) | Otherwise the required check can never turn red |

Two repository facts must be verified by a human because they are not in the tree: whether branch protection exists on `main` today, and whether the required-check list already contains anything. `gh` is **not installed** on this machine (`gh: command not found`), so the planner must either require the human to check the UI or add an installation step.

Also from the same page: *"make sure that job names are unique across all workflows"* — the new job needs a distinct display name (e.g. `Desktop (windows-latest)`), since `build.yml` already has a job named `build`.

**The deliberate-compile-error proof, without ever landing a broken commit.**

1. From current `main`, create a throwaway branch `ci-proof/desktop-compile-error`.
2. Introduce one unambiguous compile error in `src/AzureDesignStudio.Desktop/MauiProgram.cs` (e.g. append the line `this line is not C#;`), commit, push the branch, open a PR **against `main` but never merge it**.
3. Observe the `Desktop (windows-latest)` job fail with a `CS` error naming `MauiProgram.cs`. Capture the run URL, the job name, and the failing log excerpt into `.planning/phases/02-reproducible-build-and-ci-baseline/02-DESKTOP-RUNS.md`.
4. Close the PR, delete both the branch and the workflow run's artefacts. `main` never contained the error — the proof is a closed PR plus a run URL.
5. Guard the invariant in the same breath: after closing, `git rev-list --count origin/main..main` = 0 and `git log --oneline main -1` unchanged.

This is CI + human evidence, not automatable locally (a local build cannot prove the *job* fails).

### Q8. Wall-clock duration for the Desktop job

The duration must be a committed artefact, produced by the job, in two independent ways:

1. **Inside the job** — a first step records the start, a final step (`if: always()`) computes the delta and writes both `$GITHUB_STEP_SUMMARY` and `desktop-build-metrics.json`:

```yaml
    - name: Start timer
      id: t0
      shell: pwsh
      run: echo "start=$([DateTime]::UtcNow.ToString('o'))" >> $env:GITHUB_OUTPUT
    # … build …
    - name: Record duration
      if: always()
      shell: pwsh
      run: |
        $start = [DateTime]::Parse('${{ steps.t0.outputs.start }}')
        $metrics = [pscustomobject]@{
          job        = '${{ github.job }}'
          run_id     = '${{ github.run_id }}'
          commit     = '${{ github.sha }}'
          started_at = $start.ToString('o')
          ended_at   = [DateTime]::UtcNow.ToString('o')
          seconds    = [int]([DateTime]::UtcNow - $start).TotalSeconds
          aot        = 'disabled'
        }
        $metrics | ConvertTo-Json | Set-Content desktop-build-metrics.json
        $metrics | ConvertTo-Json | Out-File -Append $env:GITHUB_STEP_SUMMARY
    - uses: actions/upload-artifact@v4
      if: always()
      with: { name: desktop-build-metrics, path: desktop-build-metrics.json }
```

2. **From the Actions API (authoritative, survives log expiry)** — `started_at`/`completed_at`/`conclusion` per job:

```bash
gh api repos/{owner}/{repo}/actions/runs/{run_id}/jobs \
  --jq '.jobs[] | select(.name=="Desktop (windows-latest)") | {name, started_at, completed_at, conclusion}'
```

**Where it is recorded:** `.planning/phases/02-reproducible-build-and-ci-baseline/02-DESKTOP-RUNS.md`, one row per run — run id, commit, `continue-on-error` state, conclusion, and seconds — feeding SC#5's "two consecutive green runs" decision. Local baseline to compare against: **74 s** for a clean Desktop Release build with AOT disabled (E10), and **466 s** with AOT (E9).

### Q9. Ordering and current redness

**How red is the tree today** (all measured):

| Gate | Flipped on today | Evidence |
|---|---|---|
| `TreatWarningsAsErrors` | **green** — 0 compiler warnings; 91 PRI263 tool warnings are not promoted | E7, E8, E9 |
| Locked restore | **looks green, proves nothing** — no lock file exists, so `--locked-mode` creates them and passes | E18 |
| Audit-as-error (High/Critical) | **red** — NU1903 on AutoMapper 12.0.1 | E25 |
| Format gate (no `.editorconfig`) | **red** — 316 diagnostics in 38 files | E12 |
| `global.json` pin to 10.0.401 | green locally (it is the only SDK), unverified on a runner without 10.0.401 | E1, E21 |
| Windows Desktop job | unknown — never run on a runner | — |

**Order that cannot produce a red tree at any step:**

1. **`Directory.Build.props`** with `MauiVersion = 10.0.110` only. Verify: full solution build still green (E7 baseline).
2. **`Directory.Packages.props`** with 54 `<PackageVersion>` rows (53 moved ids plus the `System.Security.Cryptography.Xml` 10.0.13 pin, E33/E34); remove every `Version=` from the 10 csproj that declare packages. Verify: `dotnet build src/AzureDesignStudio.sln -c Release` — same 91 warnings, 0 errors, and `-getProperty:MauiVersion` still `10.0.110`.
3. **Lock files**: add `RestorePackagesWithLockFile=true` to `Directory.Build.props`, run a plain restore, commit the 11 `packages.lock.json`. Verify: `git status` shows exactly those 11 files.
4. **Locked CI**: add `--locked-mode` plus the `git diff --exit-code -- '*packages.lock.json'` guard to the Linux job and the new Windows job. Verify: PR with a bumped central version fails with NU1004 (E17 shape).
5. **`global.json`** (`version` + `rollForward`, no `workloadVersion` yet). Verify: local `dotnet --version` = 10.0.401, CI green.
6. **`workloadVersion: 10.0.401.1`** as a separate line. Verify: Windows job's workload-restore step still succeeds. Revert this line alone if the set cannot be resolved.
7. **`.editorconfig`** + the one-time `dotnet format whitespace` commit (38 files). Verify: `dotnet format … --verify-no-changes` exits 0 solution-wide.
8. **Format gate** in the Windows job, scoped to changed files. Verify: PR touching a deliberately mis-indented file fails; PR touching a clean file passes.
9. **`TreatWarningsAsErrors` + `NuGetAuditLevel=high` + `NuGetAuditSuppress` + expiry target**, and delete both `NoWarn` NU1903 lines. This step is atomic: adding the audit level without the suppression is the one ordering that goes red (E25), and adding the suppression without deleting `NoWarn` leaves the gate bypassed in two projects. Verify: full build green; a probe with the expiry date set to yesterday fails the build; `dotnet restore … -p:NoWarn=` no longer warns NU1903.
10. **Windows job** (`continue-on-error: true`) with the metrics step. Then the human step: two consecutive green runs → delete `continue-on-error`, add the required status check.

**Two ordering traps that are not obvious:**

- `.editorconfig` must precede the format gate, but it must *not* precede the whitespace normalisation commit — otherwise the gate's first run is red for 38 files on the same PR that introduces it.
- `Directory.Build.props` must carry `MauiVersion` before `Directory.Packages.props` imports it (E4/E5/E20). Writing the two files in the same commit is fine; writing them in the opposite order is not, because step 2's verification would fail.

**Requirement-tracking correction.** `BUILD-01` is checked `[x]` and `Complete` in `.planning/REQUIREMENTS.md` while none of its artefacts exist (no `global.json`, no `Directory.Packages.props`, 0 lock files — all verified). The plan should include an explicit step that sets it back to pending at phase start and re-marks it complete only when steps 1–4 are green. Do not silently leave a requirement that claims something the tree disproves.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|---|---|---|---|
| SDK selection | Directory-level MSBuild config (`global.json`) | CI runner | The SDK is chosen by the nearest `global.json` for every `dotnet` invocation; the runner only supplies it. `setup-dotnet` must install what the file resolves, never overrule it. |
| Package version authority | Repo root (`Directory.Packages.props`) | — | CPM makes the root file the single source; per-project `Version=` attributes must disappear or CPM warns (NU1008). |
| Exact package graph | Per-project `packages.lock.json` | CI restored mode | Lock files are per project by design; the gate is a CI invocation flag, not a repo property (Q1). |
| Warning policy | Repo root (`Directory.Build.props`) | — | One property, inherited by all 11 projects; per-project `NoWarn` for NU codes is what must be deleted. |
| Audit policy + allowlist | Repo root (`Directory.Build.props` + `Directory.Build.targets`) | CI command line | `NuGetAuditSuppress` is an item, so it belongs where items are declared once; expiry enforcement belongs in a target so every build evaluates it. |
| Formatting policy | Repo root (`.editorconfig`) | Windows CI job | Policy is repo-wide; the *check* must run where the whole solution loads (Windows). |
| Desktop compile gate | GitHub Actions workflow (`build.yml`) | Repository settings | The workflow can build and can be non-blocking; only a repository setting can require the check. |
| Duration evidence | GitHub Actions + `.planning` artefact | — | The job writes it; the phase records it. |

---

## Standard Stack

### Core

| Tool / File | Version | Purpose | Why standard |
|---|---|---|---|
| .NET SDK | `10.0.401` (`[VERIFIED: dotnet --list-sdks]`) | Builds everything | Already the only SDK here and present on both runner images today. |
| `global.json` | schema `sdk.version` + `rollForward` + `workloadVersion` | SDK and workload-set pin | The only mechanism .NET honours for per-repo SDK selection. |
| `Directory.Build.props` | — | Repo-wide properties (MauiVersion, lock files, warnings, audit level) | Imported by every project before its own body; verified to be evaluated before `Directory.Packages.props` (E20). |
| `Directory.Packages.props` | — | 54 central `<PackageVersion>` entries (53 moved ids + the crypto pin) | Built-in CPM; no third-party tool needed. |
| `packages.lock.json` | NuGet lock format `"version": 2` | Exact graph per project | The only thing that makes "restore fails if a lock file would change" true. |
| `.editorconfig` | — | Whitespace policy | The only configuration `dotnet format` reads. |
| `dotnet format` | SDK 10.0.401 built-in | Format gate | No extra dependency; `--verify-no-changes` is the CI switch. |
| NuGet Audit | SDK 10.0.401 built-in (on by default) | Vulnerability gate | No dependency, no service, no secret — closes SC#3 without adding a supply-chain surface. |
| `actions/checkout@v4` | v4 (repo already uses it) | Source | Existing convention; `fetch-depth: 0` required by nbgv. |
| `actions/setup-dotnet@v4` | v4 (repo already uses it) | SDK install | Supports `global-json-file` and lock-file-hashed NuGet cache. |

### Supporting

| Tool | Version | Purpose | When to use |
|---|---|---|---|
| `actions/upload-artifact@v4` | v4 | Duration metrics artefact | Every Windows job run, `if: always()`. |
| `dotnet workload restore` | SDK built-in | MAUI 10 workloads on the Windows runner | First step after SDK setup in the Windows job. |
| nbgv (`Nerdbank.GitVersioning`) | 3.6.146 (already referenced by four projects) | Version stamping | Already live via `src/version.json`; no change this phase. |

### Alternatives Considered

| Instead of | Could use | Tradeoff |
|---|---|---|
| NuGet lock files | Floating versions + `Directory.Packages.props` only | CPM alone pins the *reference* but not transitive resolution; lock files are the only way to make restore fail on drift. Rejected. |
| Built-in NuGet Audit | OWASP Dependency-Check / Snyk / Dependabot | Adds a service, a token, and a second data source that can disagree with restore. Rejected for SC#3; Dependabot is complementary and out of scope. |
| `dotnet format` | CSharpier / StyleCop | CSharpier reformats everything (a 738-file diff) and ignores `.editorconfig` style; StyleCop adds analyzer noise before the warning gate is even trusted. Rejected. |
| `NuGetAuditSuppress` + MSBuild expiry | A checked-in JSON allowlist + a script | The MSBuild form is enforced by every build; a script is only enforced where it is called. Use the target; the script variant is the fallback if the team dislikes MSBuild. |
| `MauiVersion` pin in `Directory.Build.props` | Literal `10.0.110` inside `Directory.Packages.props` | Both work; the property form keeps one number for both MAUI package entries and for MAUI's implicit references. Prefer the property. |

**Installation:** none. This phase adds no new package id, no new npm package, and no new tool — `dotnet format`, NuGet Audit, lock files, CPM, `dotnet workload restore` all ship with SDK 10.0.401. The one graph change is the `System.Security.Cryptography.Xml` remediation (E33/E34): the id is already resolved transitively by the Bicep/Azure.Deployments graph at 10.0.10, and the plan raises it to 10.0.13 through a central row plus a direct reference in the two exposed projects.

**Version verification:** the 53 declared ids and their versions are already in the tree and were enumerated from it (E15, corrected); moving them changes no resolved version. The single deliberate exception is `System.Security.Cryptography.Xml` 10.0.10 → 10.0.13, which clears three high advisories and keeps BUILD-04's allowlist at one entry (E33/E34).

---

## Package Legitimacy Audit

**No packages are installed by this phase.** The work moves existing `PackageReference` versions into `Directory.Packages.props`; all 53 declared ids were already referenced by a tracked csproj before this phase, and the only row without a prior declaration is the `System.Security.Cryptography.Xml` 10.0.13 pin, whose id is already resolved transitively through the Bicep/Azure.Deployments graph at 10.0.10 (E15, corrected; E33/E34). Every id is resolved from nuget.org today.

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---|---|---|---|---|---|---|
| (none added) | — | — | — | — | — | — |

**Packages removed due to [SLOP] verdict:** none — nothing was added, so nothing had to be vetted.
**Packages flagged as suspicious [SUS]:** none.

The one package that *is* a supply-chain concern is **AutoMapper 12.0.1**, which is already in the tree (WASM app and Core) with a known High advisory — that is precisely what SC#3's dated allowlist exists for, and Phase 8 replaces it. It is not a new dependency and is not "installed" by this phase.

---

## Architecture Patterns

### System Architecture Diagram

```
                    developer / PR
                          │
                          ▼
                  git checkout (full history)
                          │
        ┌─────────────────┴──────────────────┐
        ▼                                    ▼
 global.json (SDK 10.0.401                Directory.Build.props
 + workload set 10.0.401.1)                ├─ MauiVersion 10.0.110
        │                                  ├─ RestorePackagesWithLockFile
        ▼                                  ├─ TreatWarningsAsErrors
 dotnet (pinned SDK)                       ├─ NuGetAuditLevel = high
        │                                  └─ NuGetAuditSuppress (AutoMapper)
        ▼                                            │
 dotnet workload restore (Windows only)              │
        │                                            ▼
        │                                  Directory.Packages.props (54 versions)
        │                                            │
        ▼                                            ▼
 dotnet restore ── reads ──► packages.lock.json ◄── written by first restore
        │                            │
        │  --locked-mode             │  git diff --exit-code
        ▼                            ▼
   mismatch? ──► NU1004 ──► FAIL   missing/changed? ──► FAIL
        │
        ▼
 dotnet build -c Release  (Linux: Server/WASM/Mcp/Tests; Windows: Desktop)
        │
        ├─► NU1903/NU1904 (High/Critical) ──► FAIL
        ├─► expired allowlist (Directory.Build.targets) ──► FAIL
        ├─► C# warning ──► FAIL (0 today)
        └─► PRI263 tool warning ──► still a warning (91 today)
        │
        ▼
 dotnet format --verify-no-changes --include <changed files>   (Windows job)
        │  exit 2 ──► FAIL
        ▼
 Windows job metrics ──► desktop-build-metrics.json + job summary
        │
        ▼
 human: remove continue-on-error + require the check (repository setting)
```

### Recommended Project Structure

```text
AzureDesignStudio/
├── global.json                       # SDK 10.0.401 latestPatch (+ workloadVersion)
├── Directory.Build.props             # MauiVersion, lock files, warnings, audit level, suppression
├── Directory.Packages.props          # 54 central versions (CPM)
├── Directory.Build.targets           # dated allowlist expiry enforcement
├── .editorconfig                     # whitespace policy only
├── .github/workflows/build.yml       # Linux fast gate + desktop (windows-latest)
├── src/
│   ├── AzureDesignStudio.sln
│   ├── version.json                  # nbgv — ALREADY PRESENT, do not move
│   ├── AzureDesignStudio/…/packages.lock.json
│   └── …                             # one packages.lock.json per project (11 total)
└── .planning/phases/02-…/02-DESKTOP-RUNS.md   # duration + proof ledger
```

### Pattern 1: One authority per concern

**What:** Every build input that can drift gets exactly one file that owns it — SDK in `global.json`, versions in `Directory.Packages.props`, exact graph in `packages.lock.json`, warning policy in `Directory.Build.props`, formatting in `.editorconfig`.
**When to use:** Always, in a repo with 11 projects and three workflows.
**Why it matters here:** the repo currently has three workflows each carrying their own `dotnet-version: '10.0.x'`, and one workflow's comment explaining a solution-parsing bug — duplicated policy is exactly how the `--verify-changed` phantom flag survived into the roadmap.

### Pattern 2: Gates that fail for the right reason

**What:** Each gate is written so its failure message names the cause: NU1004 for a lock mismatch, NU1903 for an advisory, `WHITESPACE` for formatting, a `CS` error for a compile break.
**When to use:** Every gate in this phase.
**Anti-example from this repo:** `<NoWarn>$(NoWarn);NU1903</NoWarn>` in two projects — it makes the advisory invisible in exactly the projects that consume AutoMapper, so a future audit gate would report green while the vulnerable package ships.

### Pattern 3: Prove the negative

**What:** For SC#2 ("a deliberate error fails the job"), the proof is a red run on a branch that is never merged — not an assertion that the wiring looks right. For SC#5, the proof is two recorded run IDs, not a claim of stability.
**When to use:** Any success criterion phrased as "fails when X".

### Anti-Patterns to Avoid

- **Flipping several gates in one commit.** Steps 4, 7, 8 and 9 each have a distinct failure signature; combined, a red run cannot be attributed.
- **Setting `RestoreLockedMode=true` in the props file.** It disables the ordinary developer loop (`dotnet add package` then restore) and forces `--force-evaluate` everywhere.
- **`dotnet-version: '10.0.x'` beside a `global.json`.** Two authorities, one of which silently wins depending on step order.
- **`EnforceCodeStyleInBuild=true` in the same phase as the format gate.** Two gates, one review.
- **Caching `C:\Program Files\dotnet\packs`.** 5 GB payload tied to image contents that GitHub replaces weekly.
- **Running the format gate on the Linux job.** The solution cannot load the Desktop project there.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---|---|---|---|
| Pinning the SDK | A script that checks `dotnet --version` | `global.json` | The SDK resolver honours it before any script could run. |
| Locking package versions | A checked-in `packages.txt` compared in CI | `RestorePackagesWithLockFile` + `--locked-mode` | NuGet already writes content hashes and resolves the full transitive graph. |
| Centralising versions | A regex script that rewrites csproj files | `Directory.Packages.props` + `ManagePackageVersionsCentrally` | Native MSBuild, NuGet-aware, fails NU1008 on stray versions. |
| Vulnerability scanning | A custom advisory scraper | NuGet Audit (on by default) | Ships with the SDK, uses the GitHub Advisory Database, honours `WarningsAsErrors`. |
| Formatting | A custom regex/whitespace script | `dotnet format --verify-no-changes` | Roslyn knows Razor code-behind, generated-file semantics and `.editorconfig` precedence. |
| Allowlist expiry | A calendar reminder | The `Directory.Build.targets` target above | A reminder does not fail a build; the target does, on every build. |
| MAUI workloads | `dotnet workload install maui` pinned to a guessed version | `dotnet workload restore <project>` (+ `workloadVersion`) | Restore reads the project's TFM and installs exactly the manifest band the SDK expects. |
| Duration measurement | Manual stopwatch in a comment | Job metrics artefact + Actions API | SC#2 asks for a recorded duration, not an estimate. |

**Key insight:** every mechanism this phase needs is already in the SDK. The phase's risk is not missing tooling; it is ordering, and the one genuinely bespoke piece (the dated expiry) is 8 lines of MSBuild.

---

## Common Pitfalls

### Pitfall 1: `--locked-mode` passes when the lock file is missing
**What goes wrong:** CI is green, nobody notices that a new project never got its lock file committed, and "restore fails if a lock file would change" is false.
**Why it happens:** measured (E18) — with no lock file present, `dotnet restore --locked-mode` **creates** one and exits 0.
**How to avoid:** pair the restore with `git diff --exit-code -- '*packages.lock.json'` so a just-created lock file is a failure.
**Warning signs:** a green run whose checkout gained untracked `packages.lock.json` entries.

### Pitfall 2: `ContinuousIntegrationBuild=true` does not imply locked mode
**What goes wrong:** someone "fixes" CI by setting the CI-build property and believes locking is active.
**Why it happens:** measured (E19) — restore exits 0 and rewrites the stale lock file.
**How to avoid:** pass `--locked-mode` or `-p:RestoreLockedMode=true` explicitly, and prove the gate with a deliberately bumped version.

### Pitfall 3: `$(MauiVersion)` is empty where CPM needs it
**What goes wrong:** `Directory.Packages.props` with `Version="$(MauiVersion)"` evaluates to an empty version and the Desktop project fails to restore.
**Why it happens:** measured (E4) — the property is only set by `BundledVersions.targets` in the inner build. `Directory.Build.props` *is* evaluated first (E20), so the fix is to define the pin there.
**How to avoid:** `<MauiVersion>10.0.110</MauiVersion>` in `Directory.Build.props`; verify with `dotnet msbuild …Desktop.csproj -p:TargetFramework=net10.0-windows10.0.19041.0 -getProperty:MauiVersion`.

### Pitfall 4: Lock-file churn on the MAUI project
**What goes wrong:** a routine SDK patch changes the MAUI workload band, `Microsoft.Maui.Controls` moves, the Desktop lock file no longer matches, and every PR fails with NU1004 at once.
**Why it happens:** `$(MauiVersion)` tracks the workload manifest unless pinned.
**How to avoid:** pin `MauiVersion` (Q1) and `workloadVersion` (Q2); when either is intentionally bumped, regenerate the lock files in the same commit and say so.

### Pitfall 5: Format-gate false positives from line endings and BOMs
**What goes wrong:** the gate goes red for hundreds of untouched files.
**Why it happens:** measured — the index is LF for all 820 files while this worktree checks out 519 as CRLF (`core.autocrlf=true`, no `.gitattributes`; E28), and 102 files carry a BOM while 718 do not (E29). Any `end_of_line` or `charset` in `.editorconfig` forces mass rewrites.
**How to avoid:** omit both keys; add `.gitattributes` with `* text=auto` only if a normalisation is intended; keep the gate scoped to changed files via `--include`.

### Pitfall 6: The format gate cannot run on the Linux job
**What goes wrong:** `dotnet format src/AzureDesignStudio.sln` on ubuntu fails to load the Desktop project.
**Why it happens:** the solution includes a `net10.0-windows10.0.19041.0` MAUI project, which is why `build.yml` deliberately builds an explicit project set rather than the solution (`[VERIFIED: .github/workflows/build.yml:27-31]` comment: "the MAUI desktop project targets net10.0-windows and cannot build on ubuntu runners").
**How to avoid:** run the format gate in the Windows job. (The exact failure text on ubuntu is unverified here — see Assumptions A4.)
**Warning signs:** a format step that passes instantly with zero files examined.

### Pitfall 7: MSBuild date comparisons fail with MSB4086
**What goes wrong:** the expiry target errors on every build instead of checking the date.
**Why it happens:** measured twice — both `$([System.DateTime]::Parse(x).Date) < $([System.DateTime]::Now.Date)` and `'2020-01-01' < '2026-10-21'` produce `error MSB4086: A numeric comparison was attempted on "…" that evaluates to "01/01/2020 00:00:00" instead of a number`.
**How to avoid:** compute `$([System.String]::Compare(...))` into a property and compare that integer to 0 (verified working).

### Pitfall 8: `TreatWarningsAsErrors` does not mean "all warnings are errors"
**What goes wrong:** someone assumes the 91 PRI263 warnings are now fatal (they are not, measured E9) or enables `MSBuildTreatWarningsAsErrors` to "finish the job" and breaks the Desktop build on resource packaging.
**Why it happens:** `TreatWarningsAsErrors` maps to Roslyn's `-warnaserror`; task warnings are governed by MSBuild switches instead.
**How to avoid:** claim what is true — 0 compiler warnings today, so the property is a no-op that prevents regressions; leave task warnings alone.

### Pitfall 9: Deleting `NoWarn NU1903` without adding the suppression
**What goes wrong:** the build goes red on AutoMapper the moment the audit gate lands (measured E25).
**Why it happens:** two of eleven projects carry a deliberate, commented suppression.
**How to avoid:** land suppression + expiry target + deletion of both `NoWarn` lines in one commit, and verify with `dotnet restore … -p:NoWarn=` that nothing but the suppression is covering the advisory.

### Pitfall 10: Shell escaping of `-p:WarningsAsErrors=NU1903;NU1904`
**What goes wrong:** `MSBUILD : error MSB1006: Property is not valid. Switch: NU1904`.
**Why it happens:** measured — the semicolon splits the argument before MSBuild sees it.
**How to avoid:** escape it as `%3B` (`-p:WarningsAsErrors=NU1903%3BNU1904`) or write the property into `Directory.Build.props`, which is where it belongs anyway.

### Pitfall 11: ubuntu vs windows divergence
**What goes wrong:** a change that is green on Linux fails on Windows or vice versa.
**Why it happens:** four independent differences measured here: the Linux job cannot load the solution (Pitfall 6); AOT for WASM needs `wasm-tools` on Linux and MAUI needs `maui` on Windows; line endings differ per checkout (Pitfall 5); and the PowerShell-driven Docker step already forces `shell: pwsh` on a Linux runner (`[VERIFIED: .github/workflows/build.yml:41-45]`).
**How to avoid:** keep the Linux job the fast gate over the Linux-safe project set; put everything solution-wide and Windows-specific in the Windows job; never duplicate a gate in both.

### Pitfall 12: Same job name in two workflows
**What goes wrong:** the required status check becomes ambiguous and blocks merges.
**Why it happens:** GitHub keys required checks by name; `build.yml` already has a job called `build`.
**How to avoid:** distinct display names (`Desktop (windows-latest)`), per the docs quoted in Q7.

### Pitfall 13: Cache poisoning by warm NuGet state
**What goes wrong:** a "clean clone builds" proof passes on a machine whose `~/.nuget/packages` already holds every package, hiding a missing lock entry or an unpinned version.
**Why it happens:** restore resolves from the global cache without touching the feed.
**How to avoid:** prove SC#1 with `NUGET_PACKAGES=$(mktemp -d)` (or a fresh container) so the cache is empty; in CI rely on the lock-file-hashed cache key rather than a hand-written one.

---

## Code Examples

### `global.json`

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

Land `workloadVersion: "10.0.401.1"` as a separate step (Q9 step 6; value from `[VERIFIED: dotnet workload --version]`).

### `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <!-- Must be here, not in Directory.Packages.props: the property is empty in the
         outer evaluation (measured) and is only filled in later by Maui's
         BundledVersions.targets. Directory.Build.props is evaluated first. -->
    <MauiVersion>10.0.110</MauiVersion>

    <!-- One lock file per project; locked mode is a CI flag, never a repo property. -->
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <DisableImplicitNuGetFallbackFolder>true</DisableImplicitNuGetFallbackFolder>

    <!-- 0 compiler warnings exist today; this prevents new ones. It does not
         promote task warnings such as PRI263. -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>

    <!-- Only High/Critical are reported at all, so only they can fail the build. -->
    <NuGetAuditLevel>high</NuGetAuditLevel>

    <AdsAuditSuppressionAutoMapperExpires>2027-01-31</AdsAuditSuppressionAutoMapperExpires>
  </PropertyGroup>

  <ItemGroup>
    <!-- The single allowlist entry SC#3 permits. Advisory-scoped, not package-scoped:
         any other package sharing GHSA-rvv3-g6hj-g44x would also be silenced.
         Expiry is enforced by Directory.Build.targets. -->
    <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-rvv3-g6hj-g44x" />
  </ItemGroup>
</Project>
```

### `Directory.Packages.props` (shape)

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="AntDesign" Version="1.6.2" />
    <!-- … 55 more, each copied verbatim from the csproj it came from … -->
    <PackageVersion Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
    <PackageVersion Include="Microsoft.AspNetCore.Components.WebView.Maui" Version="$(MauiVersion)" />
    <PackageVersion Include="Z.Blazor.Diagrams" Version="3.0.4.1" />
  </ItemGroup>
</Project>
```

### `Directory.Build.targets` (allowlist expiry)

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

### `.editorconfig` (seeded, deliberately minimal)

```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
tab_width = 4
trim_trailing_whitespace = true
insert_final_newline = true

# end_of_line and charset are intentionally unset: the index is LF while this
# worktree is CRLF (core.autocrlf=true), and 102 of 820 files carry a BOM.
# Setting either forces a whole-repo rewrite on the gate's first run.
```

### `.github/workflows/build.yml` — Linux gate (changed steps only)

```yaml
    steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0                       # nbgv reads git history (src/version.json)

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        global-json-file: global.json        # replaces dotnet-version: '10.0.x'
        cache: true
        cache-dependency-path: '**/packages.lock.json'

    - name: Restore (locked)
      run: |
        dotnet restore src/AzureDesignStudio.Server --locked-mode
        dotnet restore src/AzureDesignStudio --locked-mode
        dotnet restore src/AzureDesignStudio.Mcp --locked-mode
        dotnet restore src/AzureDesignStudio.Core.Tests --locked-mode

    - name: Lock files must be committed
      run: git diff --exit-code -- '*packages.lock.json'
```

### `.github/workflows/build.yml` — Desktop job (shape)

```yaml
  desktop:
    name: Desktop (windows-latest)
    runs-on: windows-latest
    continue-on-error: true          # DELETE this line once two runs are green (Q9 step 10)
    timeout-minutes: 30
    steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0
    - uses: actions/setup-dotnet@v4
      with:
        global-json-file: global.json
        cache: true
        cache-dependency-path: '**/packages.lock.json'
    - name: Restore workloads (image ships MAUI 9 only)
      run: dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj
    - name: Restore (locked)
      run: dotnet restore src/AzureDesignStudio.sln --locked-mode
    - name: Build Desktop
      run: >
        dotnet build src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj
        -c Release --no-restore -p:RunAOTCompilation=false
    - name: Format (changed files)
      shell: pwsh
      run: |
        git fetch --no-tags --depth=1 origin "$env:GITHUB_BASE_REF"
        $changed = git diff --name-only --diff-filter=ACMR "origin/$env:GITHUB_BASE_REF...HEAD" -- '*.cs'
        if (-not $changed) { Write-Host 'no C# changes'; exit 0 }
        dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore --include $changed
    # … Start timer / Record duration / upload-artifact as in Q8 …
```

---

## State of the Art

| Old approach | Current approach | When changed | Impact |
|---|---|---|---|
| Floating `10.0.x` SDK in CI | `global.json` + exact install | — (long-standing) | Reproducible compile; a missing band is a hard failure (measured). |
| `NoWarn` per advisory | `NuGetAuditSuppress` item | NuGet 6.11 / .NET 8.0.400 (`[CITED: auditing-packages feature table]`) | Suppression is expressible once, repo-wide, instead of per project. |
| Audit direct packages only | `NuGetAuditMode=all` default for `net10.0` | .NET 10 SDK (`[CITED: learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/nugetaudit-transitive-packages]`; confirmed in `NuGet.targets:78-82`) | Transitive advisories now surface — this is why AutoMapper shows up at all. |
| Per-project package versions | Central Package Management | — | 54 versions in one file; NU1008 catches stray `Version=` attributes. |
| `dotnet format --check`/`--verify-changed` folklore | `--verify-no-changes` | — | The other spellings do not exist in 10.0.401 (measured). |
| Job duration from the UI | Metrics artefact + Actions API | — | SC#2 asks for a recorded number; both routes are cheap. |

**Deprecated / outdated:**
- `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` and `UserSecretsId`-era patterns are unrelated to this phase but appear in `Server.csproj`; no action.
- `docker build` via a PowerShell script on a Linux runner (`build.yml:40-46`) still works but is a divergence risk — noted, not changed.

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|---|---|---|---|---|
| .NET SDK 10.0.401 | Every build | ✓ | `10.0.401` (only SDK) | — |
| `maui` workload | Desktop build | ✓ | `10.0.110/10.0.100` | `dotnet workload restore <Desktop.csproj>` |
| `wasm-tools` workload | WASM AOT (Release) | ✓ | `10.0.112/10.0.100` | `-p:RunAOTCompilation=false` |
| Emscripten / wasm-opt packs | AOT link step | ✓ | `Microsoft.NET.Runtime.Emscripten.3.1.56.*` 10.0.12 | same as above |
| xUnit suite | Regression gate | ✓ | 755 passed | — |
| `dotnet format` | Format gate | ✓ | SDK built-in | — |
| NuGet Audit | Vulnerability gate | ✓ | SDK built-in (on by default) | — |
| `gh` CLI | Inspecting repository settings / run durations | ✗ | — | Web UI, or install `gh` in a step |
| Network to nuget.org | Restore, workload restore | ✓ (assumed; all restores here succeeded) | — | — |

**Missing dependencies with no fallback:** none for the build itself.
**Missing dependencies with fallback:** `gh` — the UI or an install step covers both repository settings and the Actions API.

---

## Assumptions Log

| # | Claim | Section | Risk if wrong | Cheapest verification |
|---|---|---|---|---|
| A1 | `dotnet workload restore` for MAUI 10 on `windows-latest` downloads and completes in a few minutes | Q3, Q7 | PR latency; possibly a timeout if it is far worse | Read the "Restore workloads" step duration on the first Windows run (Q8 records it). |
| A2 | `ubuntu-latest`/`windows-latest` continue to ship SDK 10.0.401 | Q2 | Hard failure (`A compatible .NET SDK was not found`) | Already mitigated by `setup-dotnet` with `global-json-file`; check the `Set up job` log's "Image Version". |
| A3 | The workload-set version `10.0.401.1` is resolvable from nuget.org on a clean machine | Q2 | Windows workload restore fails | Land it as its own step (Q9 step 6) and revert that line alone if it fails. |
| A4 | `dotnet format` on the solution cannot load the Desktop project on ubuntu | Q5, Pitfall 6 | Gate placed in the wrong job | Run the step once on ubuntu in a throwaway branch. |
| A5 | The Linux job's WASM Release build currently needs no explicit workload step (CI is green today) | Q3 | Linux gate starts failing when SDK/image drifts | Inspect a current CI run's build log for a NETSDK1147 or a workload install line. |
| A6 | Branch protection / required checks state on `main` is unknown | Q7 | SC#5 cannot be closed without it | Human: Settings → Branches/Rulesets; or `gh api repos/{owner}/{repo}/branches/main/protection`. |
| A7 | `dotnet format`'s diagnostics are stable across OS for these 38 files | Q5 | Gate flaps between Windows-authored and CI-authored changes | Run the gate once on the Windows job and compare the count with the local 316/38. |
| A8 | `latestPatch` will accept a later 10.0.4xx patch (e.g. 10.0.402) | Q2 | A future image could satisfy `global.json` with an SDK we did not test | Re-run `dotnet --version` when the image updates; documented behaviour only. |
| A9 | Pinning `MauiVersion` to `10.0.110` matches what the runner's workload restore installs | Q1, Q3 | Desktop restore fails on the runner with a MAUI version mismatch | The first Windows run's restore output names the MAUI version it installed. |

---

## Open Questions (RESOLVED)

1. **Where should the format gate live long-term — Windows job only, or split?**
   - What we know: the solution cannot load on Linux; the Windows job exists anyway; scoped checks cost 18 s.
   - What's unclear: whether a Desktop-only PR should wait for the Windows job before the format gate speaks.
   - Recommendation: start Windows-only; split later only if the Windows job's latency becomes a complaint.
   - **(RESOLVED: Windows-job-only for this phase — plan 02-05 runs `dotnet format --verify-no-changes` on changed files inside `Desktop (windows-latest)`; no Linux format step is planned, so a split remains a later decision.)**

2. **Should `wasm-tools` be made explicit on the Linux job?**
   - What we know: the local Release build runs AOT and therefore needs it; the workflow has no workload step.
   - What's unclear: whether the ubuntu image supplies it for .NET 10 (the readme lists SDKs, not workloads).
   - Recommendation: leave the Linux job as-is this phase (it is green today) and record the question; if it goes red with NETSDK1147, add `dotnet workload restore src/AzureDesignStudio/AzureDesignStudio.csproj`.
   - **(RESOLVED: leave the Linux job untouched — plan 02-02 changes only its SDK source, restore and guard; `RunAOTCompilation=false` keeps AOT out of both jobs, and no workload step is added on Linux.)**

3. **Does the team want `TreatWarningsAsErrors` to cover `PRI263`?**
   - What we know: those 91 warnings are a resource-packaging quirk of Blazor Hybrid on Windows, not code quality, and `TreatWarningsAsErrors` correctly ignores them.
   - What's unclear: whether they should be silenced at the source (`PRI` generation options) to make the log readable.
   - Recommendation: no action this phase; they are not a gate, and silencing them risks hiding a real missing-resource defect.
   - **(RESOLVED: no action — plan 02-03 turns on only `TreatWarningsAsErrors`, forbids `MSBuildTreatWarningsAsErrors`, and asserts the 91 `PRI263` warnings are still emitted; neither silencing nor promotion is planned.)**

---

## Validation Architecture

`workflow.nyquist_validation` is `true` in `.planning/config.json`, so this section is the input for `02-VALIDATION.md`. Phase 2 delivers **no product feature**; every requirement is proven by a command or a CI run, so the "test map" is a command map.

### Test Framework

| Property | Value |
|---|---|
| Framework | xUnit 2.9.2 with `Microsoft.NET.Test.Sdk` 17.12.0 and `xunit.runner.visualstudio` 2.8.2 (`[VERIFIED: src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:15-17]`), `coverlet.collector` 6.0.2 (`:21`) |
| Config file | none — no `xunit.runner.json`, no `.runsettings` |
| Quick run command | `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` (~80 s measured) |
| Full suite command | `dotnet test src/AzureDesignStudio.Core.Tests -c Release --filter "Category!=LiveAzure"` (755 cases, 1 s measured after build) |
| Phase-2 specific gates | locked restore + lock-diff; `dotnet format --verify-no-changes --include …`; audit probe with `-p:NoWarn=`; expiry target probe |

### Phase Requirements → Verification Map

| Req ID | Behavior | Verification type | Command / artefact | Exists? |
|---|---|---|---|---|
| BUILD-01 | Clean clone restores and builds with pinned SDK + locked versions | command, local | `git clone <repo> /tmp/x && cd /tmp/x && NUGET_PACKAGES=/tmp/empty-cache dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false`; then `dotnet restore … --locked-mode` and `git diff --exit-code -- '*packages.lock.json'` | ❌ Wave 0 — no `global.json`, no lock files yet |
| BUILD-02 | Desktop builds on PRs; a deliberate error fails the job | CI + human | PR on `ci-proof/desktop-compile-error`; run URL for the red `Desktop (windows-latest)` job recorded in `02-DESKTOP-RUNS.md` | ❌ Wave 0 — job does not exist |
| BUILD-03 | Centralised versions, warnings-as-errors, format gate | command + CI | `grep -c PackageReference.*Version= src/*/*.csproj` must be 0; `dotnet build … -c Release` with 0 compiler warnings; `dotnet format … --verify-no-changes` exit 0 on the normalised tree | ❌ Wave 0 — CPM and `.editorconfig` absent |
| BUILD-04 | High/Critical fail; one dated AutoMapper allowlist entry | command, local + CI | `dotnet restore src/AzureDesignStudio.Core -p:NoWarn= -p:NuGetAuditLevel=high` (must stay clean only through the suppression); then re-point the expiry to yesterday and expect the `AdsCheckAuditSuppressionExpiry` error | ❌ Wave 0 — suppression item absent; two `NoWarn` lines present |
| BUILD-05 | Non-blocking Windows job records duration, becomes blocking after two green runs | CI + human | Two rows in `02-DESKTOP-RUNS.md` with run IDs + durations; required-check screenshot/`gh api` output after the flip | ❌ Wave 0 — job does not exist |
| Regression | Offline suite unchanged | command | `dotnet test … --filter "Category!=LiveAzure"` → `Passed: 755` | ✅ exists |

### Sampling Rate

- **Per task commit:** `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` (≈80 s) — catches CPM, lock-file and warning regressions immediately.
- **Per plan wave:** the above plus `dotnet format src/AzureDesignStudio.sln --verify-no-changes` (30–51 s measured) and the offline suite (755).
- **Phase gate:** full solution Release build (AOT on, ≈11.5 min measured), suite green, locked restore green from a clean cache, audit probe green through the suppression, expiry probe red, format check exit 0.

### Wave 0 Gaps

- [ ] `global.json` — no file exists
- [ ] `Directory.Build.props` / `Directory.Packages.props` / `Directory.Build.targets` — none exist
- [ ] `.editorconfig` — none exists
- [ ] 11 × `packages.lock.json` — zero exist
- [ ] `.github/workflows/build.yml` Desktop job + locked restore + lock-diff guard — none exist
- [ ] `02-DESKTOP-RUNS.md` — the evidence ledger for SC#2/SC#5 does not exist yet
- [ ] No framework install needed; no new test project; `D-15`-style "no new test framework this phase" holds (Phase 1 precedent)

*(Everything above is file/workflow creation, not test authoring — the only "test" artefacts are the probes written into the plan as commands with expected exit codes.)*

---

## Success Criteria → Proof Map

| SC | Criterion (abridged) | Exact proof | Automatable locally? | CI? | Human-only? |
|---|---|---|---|---|---|
| SC#1 | Clean clone, no warm caches, SDK pinned 10.0.401 `latestPatch`, locked versions; restore fails if a lock file would change | `git clone` to a temp dir; `NUGET_PACKAGES=<empty>` `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false`; then bump one central version in a scratch commit → `dotnet restore --locked-mode` must exit 1 with NU1004 (shape measured, E17); plus `git diff --exit-code -- '*packages.lock.json'` | **Yes** — all three steps are local commands with deterministic exit codes | **Yes** — the locked restore + diff guard run per PR | No |
| SC#2 | PR with a deliberate compile error in `MauiProgram.cs` fails the Desktop job; job duration recorded | Red run URL on `ci-proof/desktop-compile-error` (closed, unmerged) + `desktop-build-metrics.json` artifact + a row in `02-DESKTOP-RUNS.md` | No — a local build cannot prove the *job* | **Yes** — this is the CI proof | Partly — someone must create, push and close the throwaway PR |
| SC#3 | CI fails on any High/Critical vulnerable package; the only allowlist entry is AutoMapper with a dated expiry | `dotnet restore src/AzureDesignStudio.Core -p:NoWarn= -p:NuGetAuditLevel=high` fails without the suppression (E25) and passes with it (E27); expiry probe with yesterday's date fails with the `AdsCheck…` error; `grep -rn "NoWarn.*NU19" src/` returns nothing | **Yes** — both probes are local | **Yes** — the same restore runs in both jobs | No — the expiry date itself is a policy choice, recorded in the props file |
| SC#4 | Versions only in `Directory.Packages.props`; app projects warnings-as-errors; a changed file violating formatting fails | `grep -c 'PackageReference[^>]*Version=' src/*/*.csproj` = 0; `dotnet build … -c Release` (0 compiler warnings today, E8); `dotnet format … --verify-no-changes --include <one mis-indented file>` exits 2 (E14 shape) and exit 0 on the normalised tree | **Yes** | **Yes** — the format step runs in the Desktop job | No |
| SC#5 | After two consecutive green non-blocking Windows runs, the job becomes blocking and every merge requires it | Two rows in `02-DESKTOP-RUNS.md` (run id, commit, `continue-on-error` state, conclusion) + the commit that deletes `continue-on-error` + proof the check is required (UI screenshot or `gh api …/branches/main/protection` showing the name in `required_status_checks.contexts`) | No | Partly — CI produces the two green runs | **Yes** — the required-check setting is a repository setting, not a workflow file (Q7) |

---

## Security Domain

`security_enforcement` is `true` and `security_asvs_level` is `1` in `.planning/config.json`. This phase's security content is entirely supply chain.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard control |
|---|---|---|
| V2 Authentication | No | No auth surface changes. |
| V3 Session Management | No | — |
| V4 Access Control | No | — |
| V5 Input Validation | No | No request surface changes. |
| V6 Cryptography | No | No cryptography; signing is Phase 11. |
| V12/V14 (files/resources, configuration) — applied at level 1 as "build & dependency integrity" | Partly | Lock files + CPM + pinned SDK + NuGet Audit: the deployed artefact is only as trustworthy as its resolved dependency graph. |

### Known Threat Patterns for a .NET + GitHub Actions supply chain

| Pattern | STRIDE | Standard mitigation in this phase |
|---|---|---|
| Dependency confusion / substitution | Tampering | CPM with exact versions (no floating), lock files with content hashes, `--locked-mode` in CI. |
| Typosquatted or newly published package silently entering the graph | Tampering | .NET 10 audits transitives by default (`NuGetAuditMode=all`), so a vulnerable or newly-flagged transitive is reported without extra tooling. |
| A "fixed" lock file that pins nothing | Repudiation (of the gate) | The `git diff --exit-code -- '*packages.lock.json'` guard, because `--locked-mode` alone passes when a lock file is missing (measured). |
| Silent acceptance of a known vulnerability | Elevation of privilege | The allowlist is one advisory-URL item with a build-enforced expiry date; both per-project `NoWarn NU1903` lines must be deleted or the gate is bypassed. |
| GitHub Actions tag drift (e.g. `actions/checkout@v4` re-pointed) | Tampering | Out of scope for SC#1–SC#5, but flag: pinning actions by commit SHA is the standard hardening step and is cheap to add later. |
| Untrusted PR code executed with write credentials | Elevation of privilege | The new job must not use `pull_request_target`; a plain `pull_request` job runs with a read-only token. |
| Cache poisoning between branches | Tampering | The NuGet cache key is the lock-file hash, so a poisoned entry for a different dependency set is not restored. |

---

## Sources

### Primary (HIGH confidence — measured on this machine or read from the installed SDK)
- Installed SDK 10.0.401 and the CLI behaviour it produces: `dotnet --version`, `dotnet --list-sdks`, `dotnet workload list`, `dotnet workload --version`, `dotnet format --help`, `dotnet restore --help`.
- `C:\Program Files\dotnet\sdk\10.0.401\NuGet.targets` — audit defaults (73-82), lock-file plumbing (946-980), `NuGetAuditSuppress` collection (373-395).
- `C:\Program Files\dotnet\sdk\10.0.401\NuGet.props` — CPM import and ordering (22-38).
- `C:\Program Files\dotnet\packs\Microsoft.Maui.Sdk\10.0.110\Sdk\BundledVersions.targets:7` — `MauiVersion` default.
- Repository files read in this session: `.planning/ROADMAP.md`, `.planning/REQUIREMENTS.md`, `.planning/PROJECT.md`, `.planning/STATE.md`, `.planning/config.json`, `AGENTS.md`, the three workflows, `src/AzureDesignStudio.sln`, all 11 `src/**/*.csproj`, `src/version.json`, `.planning/phases/01-land-and-stabilize/01-VALIDATION.md`.
- Throwaway probe runs (outside the repo) for CPM ordering, lock files, locked mode, audit suppression and the MSBuild expiry target.

### Secondary (MEDIUM confidence — official documentation, not exercised here)
- `learn.microsoft.com/en-us/nuget/concepts/auditing-packages` — audit properties, warning codes, suppression, CI pipeline pattern.
- `learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1901-nu1904` — severity mapping.
- `learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format` — `--verify-no-changes`, `--include`, `--severity`.
- `learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-restore`, `…/dotnet-workload-sets` — restore semantics and the `sdk.workloadVersion` key.
- `learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props` — `EnforceCodeStyleInBuild`, `CodeAnalysisTreatWarningsAsErrors`.
- `learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli` — `WindowsPackageType` semantics.
- `raw.githubusercontent.com/actions/setup-dotnet/main/README.md` — version syntax, `global-json-file`, cache behaviour.
- `raw.githubusercontent.com/actions/runner-images/main/README.md`, `…/images/ubuntu/Ubuntu2404-Readme.md`, `…/images/windows/Windows2025-Readme.md` — image contents and label mapping.
- `docs.github.com/…/about-protected-branches` — required status checks are a branch-protection/ruleset setting.
- `learn.microsoft.com/en-us/dotnet/core/tools/global-json#rollforward` — roll-forward semantics per value.

### Tertiary (LOW confidence — training knowledge / inference, flagged in the Assumptions Log)
- Workload-restore download size and duration (A1).
- Future patch acceptance under `latestPatch` (A8).
- The runner-installed MAUI package version matching `10.0.110` (A9).

---

## Metadata

**Confidence breakdown:**
- Standard stack: **HIGH** — every mechanism is SDK-native, and each was exercised here except workload restore.
- Architecture / ordering: **HIGH** — the ordering constraints were each reproduced (E16–E20, E25–E27) rather than reasoned about.
- Warning baseline: **HIGH** — one observed integer (91), reproduced on a clean rebuild, with all 91 attributed to a single target.
- Format-gate behaviour: **HIGH locally**, MEDIUM cross-OS (A4, A7).
- Pitfalls: **HIGH** — Pitfalls 1, 2, 3, 7, 8, 9, 10 were each hit during this research.
- Runner-image facts: **MEDIUM** — read from the image readmes for the current image version, not from a live run.

**Research date:** 2026-10-21
**Valid until:** ~2026-11-20 (30 days) for the repo-derived facts; **~2 weeks** for anything that depends on GitHub runner image contents, because `ubuntu-latest` is announced to move to 26.04 in November 2026.

---

## RESEARCH COMPLETE
