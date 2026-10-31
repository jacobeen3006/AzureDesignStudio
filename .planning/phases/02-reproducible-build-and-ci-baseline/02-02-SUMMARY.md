# Plan 02-02 Summary: Reproducible Build and CI Baseline — Locked Restore Gate

**Status:** complete — Tasks 2 and 3 verified; Task 1 already committed as `fa62bb9`.

## Commits

| Task | SHA | Subject |
|------|-----|---------|
| 1 | `fa62bb9` | ci(02-02): restore locked on Linux and fail when a lock file is missing or stale |
| 2 | (probe only) | no repository file changes |
| 3 | (no-op: BUILD-01 already `[x]`/`Complete`) | clone proof recorded here |

## Probe A — stale lock (NU1004)

Worktree: detached at `6e4fbf1`. Command: `dotnet add src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj package AntDesign --version 1.6.1 --no-restore` (rc=0), then `dotnet restore ... --locked-mode` → rc=1.

Quoted line:
`src\AzureDesignStudio.Core\AzureDesignStudio.Core.csproj : error NU1004: The package reference AntDesign version has changed from [1.6.2, ) to [1.6.1, ).The packages lock file is inconsistent with the project dependencies so restore can't be run in locked mode.`

## Probe B — missing lock (guard)

After `git checkout -- .`, removed Core lock from index and working tree. `dotnet restore ... --locked-mode` → rc=0 and recreated `src/AzureDesignStudio.Core/packages.lock.json` (E18 measured). Guard output (`git status --porcelain -- '*packages.lock.json'`):
```
D  src/AzureDesignStudio.Core/packages.lock.json
?? src/AzureDesignStudio.Core/packages.lock.json
```
Tracked count dropped to 10 vs 11 csproj. `git diff --exit-code -- '*packages.lock.json'` alone passed (rc=0) — the reason the `git status --porcelain` line exists.

Worktree removed with `--force`; `git worktree list` shows only main; main tracked tree unchanged aside from pre-existing WIP.

## Cold-cache clone proof (BUILD-01)

Clone SHA: `6e4fbf1b289d149638241b6aa190da339ea9040e` (HEAD at proof time).
SDK: `dotnet --version` → `10.0.401`. Lock files: 11.
Logs: `C:/Users/jakub/clone-logs-02-02/build.log`, `restore.log`, `test.log`.
Build: `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` → rc=0, 0 errors, no CS/NU/IDE/CA/MSB warnings (91 pre-existing WinAppSDK PRI263 warnings remain).
Locked restore: rc=0.
Tests: `Passed!  - Failed:     0, Passed:   755, Skipped:     0, Total:   755`.

## Known drift

- `codeql` job still uses `dotnet-version: '10.0.x'` (out of scope; recorded, not fixed).
- Clone cache directory could not be fully deleted due to file locks; logs retained.
- BUILD-01 was already marked `[x]`/`Complete` in REQUIREMENTS.md at start; no file change required.