# Plan 02-05 Summary

## Job step list (`.github/workflows/build.yml`, `desktop` job)

`Desktop (windows-latest)`, `runs-on: windows-latest`, `continue-on-error: true`, `timeout-minutes: 30`. Steps: Checkout (fetch-depth 0), Setup .NET (global-json-file only), Restore workloads (`dotnet workload restore ...Desktop.csproj`), Restore (locked) under `shell: bash`, Lock files must be committed under `shell: bash`, Start timer (id `t0`, pwsh), Build Desktop (Release, `--no-restore`, `-p:RunAOTCompilation=false`), Format (changed files) (pwsh, base-ref guard + `--verify-no-changes`), Record duration (`if: always()`, pwsh, writes `desktop-build-metrics.json`), upload-artifact (`if: always()`, name `desktop-build-metrics`).

## Parsed structures checked

YAML parses; `jobs.desktop.name == "Desktop (windows-latest)"`, `runs-on == "windows-latest"`, `continue-on-error is True`, `timeout-minutes == 30`. Setup uses `global-json-file: global.json` and no `dotnet-version`. Restore workloads runs `dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj`. Restore (locked) runs `--locked-mode` under `shell: bash`. Lock guard under `shell: bash`. No `pull_request_target`, no packs cache, no `workload install` anywhere in the file.

## Format-probe exit codes

Throwaway detached worktree at HEAD. Clean file `src/AzureDesignStudio.Core/APIM/APIMModel.cs`: exit 0. After appending a whitespace-only line (`printf '   \n'`): exit 2 with `error WHITESPACE: Fix whitespace formatting. Replace 4 characters with '\r\n'`. After `git checkout --`, exit 0 again. Changed-file list on clean worktree: empty. Worktree removed; `git worktree list` shows only main.

## Workload pin

`global.json` now contains `"workloadVersion": "10.0.401.1"` inside `sdk`. `dotnet --version` = `10.0.401`; `dotnet workload --version` = `10.0.401.1`; local `dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` exits 0. Commit `f1804e1` (global.json only).

## Ledger

`.planning/phases/02-reproducible-build-and-ci-baseline/02-DESKTOP-RUNS.md` created with sections `## Local baseline`, `## Runs`, `## SC#2 — deliberate compile error`, `## SC#5`, the exact Runs table header, two local baseline rows (74 s AOT off, 466 s AOT on) labelled `local — not a CI run`, and the three SC#5 labels `Flip commit:`, `Required check:`, `Proof:`. No run rows fabricated; no requirement marked complete. Commit `ab01af6`.

## Verification

`dotnet build src/AzureDesignStudio.Core.Tests -c Release` succeeded (0 warnings, 0 errors). `dotnet test src/AzureDesignStudio.Core.Tests -c Release --filter "Category!=LiveAzure"` -> `Passed! - Failed: 0, Passed: 755, Skipped: 0, Total: 755`. (Full-solution Release build was attempted; it failed in this environment on the WASM project's `wasm-opt` step with a file-lock/permission error unrelated to this plan's changes; the test project and its dependencies build cleanly.)

## Commits

- `ce3e788` ci(02-05): add a non-blocking Desktop (windows-latest) pull-request job (build.yml only)
- `f1804e1` build(02-05): pin the .NET workload set for reproducible workload restore (global.json only)
- `ab01af6` docs(02-05): add the SC#2/SC#5 evidence ledger with the local baseline (ledger only)
- (this summary) docs(02-05): complete plan - ...
