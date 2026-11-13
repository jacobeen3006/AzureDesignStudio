# Integrity verification — 2026-10-30

Status: COMPLETE (with noted unverified items).

## Scope and guardrails
- Baseline commit: `fa62bb9001fccb35bae7bea9df27499c4b31fa92` (matches `git rev-parse HEAD`).
- Scope: build/restore/test and repository integrity checks only. No source, config, lock, STATE, or ROADMAP edits; no credentials, live Azure, or user database access.
- Evidence artifacts: `.planning/audits/2026-10-30/evidence/`.

## Environment
- SDK: `dotnet --version` = `10.0.401` (matches `global.json` pin `10.0.401` / `latestPatch`).
- Workloads: `maui 10.0.110/10.0.100`, `wasm-tools 10.0.112/10.0.100` (MAUI workload present).
- OS: Windows 10.0.26300, win-x64; .NET 10.0.12 runtime.
- Branch: `main`; untracked paths preserved (`.planning/.omp-*`, `CODEBASE-REVIEW.md`, `milestone.lock`, audit output).

## Commands and results

| Check | Command | Result | Duration |
|---|---|---|---|
| Locked restore (solution) | `dotnet restore src/AzureDesignStudio.sln --locked-mode` | exit 0; 11/11 projects restored/up-to-date | 3s |
| Release build (10 non-WASM projects, no-restore) | per-project `dotnet build ... -c Release --no-restore` | all exit 0 | 21s |
| Release build (Desktop, no AOT) | `dotnet build ...Desktop -c Release ... -p:RunAOTCompilation=false` | exit 0, "Build succeeded." | 41s |
| Release build (WASM client, no AOT) | `dotnet build ...AzureDesignStudio -c Release ... -p:RunAOTCompilation=false` | exit 0, "Build succeeded." | 6s |
| Release build (solution, default AOT) | `dotnet build src/AzureDesignStudio.sln -c Release` | RESOLVED 2026-10-30 evening: succeeded, 0 errors on a clean long-budget run (`evidence/build-sln-aot-final.log`); original TIMEOUT was a tool wall-clock limit, not a compile failure | ~15min |
| Offline tests | `dotnet test Core.Tests -c Release --no-build --filter "Category!=LiveAzure" --logger trx` | Passed! Failed 0, Passed 755, Skipped 0, Total 755 | 3s |
| Vulnerability audit | `dotnet list src/AzureDesignStudio.sln package --vulnerable --include-transitive` | Only AutoMapper 12.0.1 High (expected single allowlist entry); no Critical | 22s |
| Whitespace check | `git diff --check` | exit 0, no whitespace errors | <1s |

## Integrity checks
- Projects vs lock files: 11 csproj, 11 `packages.lock.json`, all tracked (`git ls-files` = 11, matching csproj count).
- Central package versions: 0 inline `Version=` on external `PackageReference` entries; the 5 inline versions are project-to-project references (`AzureDesignStudio.AzureResources.*`), not NuGet packages.
- Lock file format: JSON `"version": 2` for all 11.
- `global.json`: SDK `10.0.401`, `rollForward: latestPatch`, `allowPrerelease: false`.
- `Directory.Build.props`: `MauiVersion 10.0.110`, `RestorePackagesWithLockFile true`.
- Vulnerability: only AutoMapper 12.0.1 High (GHSA-rvv3-g6hj-g44x) across WASM/Core/Core.Tests/Server/Desktop/Workspace/Mcp; 5 projects have no vulnerable packages. Matches Phase 2 single dated allowlist exception.
- Warnings: 0 CS/NU/MSB warnings on the 10 non-Desktop projects; Desktop shows 91 pre-existing `PRI263` WinAppSDK warnings (documented in 02-01 summary).
- Tests: TRX at `evidence/offline-tests.trx` confirms 755/755 passed, 0 failed, 0 skipped.
- Requirements integrity: 57 v1 requirement IDs, each appears exactly once in the traceability table (no duplicates); 7 v2 requirements (SRV/IAC/MCP/UX) correctly excluded from v1 traceability; roadmap lists 11 phases.
- Inline-version check and lock/catalog counts recorded above.

## Stale STATE vs 02-01/02-02 evidence (not fixed, per instruction)
- `.planning/STATE.md` reports `completed_plans: 8`, `current_phase: 02`, status `executing`, stopped at "Phase 01 complete, ready to plan Phase 2", `state_head: 9ca85f5...`.
- Actual HEAD `fa62bb9` includes commits `010d7b6` (02-01 task 3 lock files), `fdc6f3a` (02-01 summary), and `fa62bb9` (02-02 locked restore guard). `02-01-SUMMARY.md` marks 3/3 tasks complete.
- Conclusion: STATE counters are stale relative to landed 02-01/02-02 work. Not edited (owned by other worker / out of scope).

## Limitations / unverified
- Full AOT-enabled Release build of the solution subsequently completed with 0 errors on 2026-10-30 (see `evidence/build-sln-aot-final.log` and `FIX-PLAN.md` Results); the original limitation was only the ~7-minute tool budget during the audit run.
- No live Azure, database, or credential access was performed.
- `.planning/CODEBASE-REVIEW.md` staleness was not re-verified here (owned by another worker).
- Desktop build verified with `-p:RunAOTCompilation=false`; the project's default Release path enables AOT for the WASM payload it embeds, which is the timed-out step above.

## Final completeness
All planned integrity checks either passed or are explicitly marked unverified above. No source/config/lock/STATE/ROADMAP files were modified. New outputs: `INTEGRITY.md` and `evidence/` logs plus `offline-tests.trx`.
