---
phase: 02-reproducible-build-and-ci-baseline
plan: 02
subsystem: ci
tags: [github-actions, nuget, locked-mode, lock-files, reproducibility, clean-clone]

requires:
  - phase: 02-reproducible-build-and-ci-baseline
    plan: 01
    provides: global.json (SDK 10.0.401), Directory.Build.props (RestorePackagesWithLockFile), 11 committed packages.lock.json
provides:
  - Linux fast gate installs the SDK from global.json and restores four projects with --locked-mode
  - three-part lock guard (diff, working tree, tracked-count) that fails for a stale, a rewritten, and a missing lock file
  - NU1004 and missing-lock probe evidence captured from a detached worktree
  - cold-cache clean-clone proof at 213c5c5 (SDK 10.0.401, 11 locks, Release build, locked restore, 755 tests)
  - BUILD-01 marked Complete
affects: [02-03, 02-04, 02-05, 02-06, 02-07]

actuals:
  tokens: 963
  tasks: 3
  commits: 3
  plan_head_before: b435bc3990b7fb6b9482ddd412501880760289ba
  plan_head_after: 213c5c5f909af81265a4ac399143316fb61a54c2

tech-stack:
  added: []
  patterns:
    - "A lock gate needs three assertions: NU1004 catches drift, `git status --porcelain` catches a file restore wrote, tracked-count equality catches a project with no lock"
    - "SDK-shipped packs resolve from the SDK's library-packs fallback folder before any feed, so their lock hashes must be generated from there"
    - "Prove a proof: run the claim on a clone with an empty NUGET_PACKAGES, never on the working tree"

key-files:
  created: []
  modified:
    - .github/workflows/build.yml
    - src/AzureDesignStudio/packages.lock.json
    - .planning/REQUIREMENTS.md

decisions:
  - "The CI cache key is the lock-file set (`cache-dependency-path: '**/packages.lock.json'`), so a cache entry from a different dependency graph is not reused"
  - "The three guard lines are kept separate rather than collapsed: each names a different failure mode and each is proven with its own negative"
  - "The WASM SDK pack is locked to the SDK fallback copy (library-packs) because the SDK folder is pinned by global.json and is what every clean machine resolves"
  - "The codeql job's floating `dotnet-version: '10.0.x'` is left in place and recorded as drift; it is not a gate this plan owns"

patterns-established:
  - "Negative-first gate proof: each assertion is shown failing before the gate is trusted"
  - "Evidence lives in named log files; the summary quotes the failure line verbatim"

requirements-completed: [BUILD-01]

status: complete
---

# Phase 2 Plan 02: Locked Restore and the Lock-File Guard Summary

**The Linux gate now installs the SDK from `global.json`, restores its four projects in `--locked-mode` behind a three-part lock guard, both failure modes are proven negatively, and a cold-cache clone at `213c5c5` builds in Release and passes 755 offline tests — BUILD-01 is Complete on that evidence.**

## Performance

- **Duration:** ~26 min (21:19Z → 21:45Z)
- **Tasks:** 3
- **Files modified:** 3 (`build.yml`, one `packages.lock.json` line, `REQUIREMENTS.md`)

## Accomplishments

- `Setup .NET` in the `build` job resolves the SDK from `global.json` (no second authority) and caches by lock-file hash.
- `Restore (locked)` restores exactly the four Linux-safe projects — `Server`, the WASM `AzureDesignStudio`, `Mcp`, `Core.Tests` — with `--locked-mode`; the Desktop project is never named.
- `Lock files must be committed` asserts three separate things, so a stale lock, a lock the restore wrote, and a project with no lock all fail the step.
- Probe A proved drift: `dotnet add package AntDesign --version 1.6.1 --no-restore` against the central file, then a locked restore → **exit 1, `error NU1004`**.
- Probe B proved the hole: with the Core lock deleted, the same locked restore **exits 0 and recreates the file** (E18), `git diff --exit-code` alone **passes**, and only the working-tree and count assertions catch it.
- A cold-cache clone proved BUILD-01 at branch head: SDK `10.0.401`, 11 lock files, Release build `0 Error(s)` with 91 `PRI263` warnings and zero `CS`/`NU`/`IDE`/`CA`/`MSB` warnings, locked restore exit 0, `Passed: 755, Failed: 0`.
- The cold-cache proof caught a real defect that the warm tree hid: the WASM SDK pack's lock hash had been recorded from a nuget.org copy, while every clean machine resolves the SDK's `library-packs` copy → `NU1403`. Fixed by regenerating that one lock line from an empty cache.

## Task Commits

| Task | Name | Commit |
|------|------|--------|
| 1 | Locked restore + three-part lock guard in the Linux job | `2bd7072` |
| 2 | Negative probes (throwaway worktree only, no repository change) | — (no commit; nothing in the repository changed) |
| 3 | Regenerate the WASM lock from the SDK fallback copy (deviation, Rule 1) | `a5dd664` |
| 3 | Mark BUILD-01 Complete | `213c5c5` |

`git rev-list --count b435bc3..HEAD` = **3**; the branch is `gsd/02-02-02`, head `213c5c5f909af81265a4ac399143316fb61a54c2`.

## The guard, verbatim (`build.yml`)

```yaml
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        # global.json is the one SDK authority; the CodeQL job below still
        # pins '10.0.x' and is recorded drift, not fixed here.
        global-json-file: global.json
        # Cache key is the lock-file set, so a cache from a different graph
        # is never reused (a stale restore would look like a fresh one).
        cache: true
        cache-dependency-path: '**/packages.lock.json'

    - name: Restore (locked)
      # Only the four Linux-safe projects: the solution carries the
      # net10.0-windows Desktop project, which cannot restore on ubuntu.
      run: |
        dotnet restore src/AzureDesignStudio.Server --locked-mode
        dotnet restore src/AzureDesignStudio --locked-mode
        dotnet restore src/AzureDesignStudio.Mcp --locked-mode
        dotnet restore src/AzureDesignStudio.Core.Tests --locked-mode

    - name: Lock files must be committed
      # --locked-mode alone is not a gate: line 1 catches a committed lock
      # that no longer matches the graph (NU1004 normally beats us to it);
      # line 2 catches a lock the restore above created or rewrote, which
      # `git diff` misses because it ignores untracked files; line 3 catches
      # a project with no committed lock at all, where --locked-mode exits 0
      # and writes one.
      run: |
        git diff --exit-code -- '*packages.lock.json'
        test -z "$(git status --porcelain -- '*packages.lock.json')"
        test "$(git ls-files '*packages.lock.json' | wc -l)" -eq "$(ls src/*/*.csproj | wc -l)"
```

The `Build`, `Test (offline)` and `Build the Docker image` steps are unchanged, including their comments and explicit project set (`git diff` shows 28 insertions, 1 deletion, all inside the setup/restore region).

## Evidence (command → observed)

### Task 1 — structure and the green path

| Command | Result |
|---|---|
| Python `yaml.safe_load` + structural asserts (global-json-file present, no `dotnet-version`, guard has `git status --porcelain --`, `git ls-files`, restored step has exactly 4 `--locked-mode`, no Desktop) | `yaml ok` |
| `grep -c "dotnet-version: '10.0.x'" build.yml` | `1` — the `codeql` job, recorded as drift |
| `dotnet restore <Server\|AzureDesignStudio\|Mcp\|Core.Tests> --locked-mode` | `rc=0` ×4 (re-run after the lock fix: still `0,0,0,0`) |
| `git diff --exit-code -- '*packages.lock.json'` | `rc=0` |
| `test -z "$(git status --porcelain -- '*packages.lock.json')"` | `rc=0` |
| tracked locks vs csproj count | `tracked=11 csproj=11` |

### Task 2 — the negatives (`git worktree add --detach`, then `--force` removed)

Probe A — stale lock through NuGet's own tooling:

```
dotnet add src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj package AntDesign --version 1.6.1 --no-restore   → rc=0
git diff --stat                                    → Directory.Packages.props | 5 ++---
dotnet restore src/AzureDesignStudio.Core/... --locked-mode → rc=1
```
```
src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj : error NU1004: The package reference AntDesign version has changed from [1.6.2, ) to [1.6.1, ).The packages lock file is inconsistent with the project dependencies so restore can't be run in locked mode. Disable the RestoreLockedMode MSBuild property or pass an explicit --force-evaluate option to run restore to update the lock file.
```

Probe B — the missing-lock hole `--locked-mode` cannot close:

```
git rm --cached src/AzureDesignStudio.Core/packages.lock.json && rm -f ...   → lock absent
dotnet restore src/AzureDesignStudio.Core/... --locked-mode                  → rc=0
lock recreated: yes                       (E18 reproduced, not assumed)
git diff --exit-code -- '*packages.lock.json'                                → rc=0   ← false pass; git diff ignores untracked files
git status --porcelain -- '*packages.lock.json'
D  src/AzureDesignStudio.Core/packages.lock.json
?? src/AzureDesignStudio.Core/packages.lock.json
tracked=10 csproj=11                     ← count assertion fails
```

Cleanup: `git worktree remove --force` → `git worktree list` names only the working tree; `git status --porcelain --untracked-files=no` = 0 lines.

### Task 3 — cold-cache clean clone

Two runs: the first at `a5dd664` (immediately after the lock fix) and the confirming run at branch head `213c5c5`. Both used `git clone --no-hardlinks` and a `NUGET_PACKAGES` directory that did not exist before the run.

| Check | `a5dd664` | `213c5c5` |
|---|---|---|
| `cache pre-exists` | `no` | `no` |
| `dotnet --version` | `10.0.401` | `10.0.401` |
| `src/*/packages.lock.json` | `11` | `11` |
| `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` | `rc=0`, `91 Warning(s)`, `0 Error(s)`, 2:59 | `rc=0`, `91 Warning(s)`, `0 Error(s)`, 3:30 |
| `warning (CS\|NU\|IDE\|CA\|MSB)` in build log | `0` | `0` |
| `dotnet restore src/AzureDesignStudio.sln --locked-mode` | `rc=0` | `rc=0` |
| `git status --porcelain -- '*packages.lock.json'` after restore | 0 lines | 0 lines |
| `dotnet test src/AzureDesignStudio.Core.Tests -c Release --no-build --filter "Category!=LiveAzure"` | `Passed!  - Failed: 0, Passed: 755, Skipped: 0, Total: 755` | same |

Log paths (kept, outside the repository):

- `C:\Users\jakub\AppData\Local\Temp\p202\probeA-NU1004.log` — Probe A restore log
- `C:\Users\jakub\AppData\Local\Temp\p202\probeB-missing-lock.log` — Probe B restore log
- `C:\Users\jakub\AppData\Local\Temp\p202\clone-build.log`, `clone-locked-restore.log`, `clone-test.log` — run at `a5dd664`
- `C:\Users\jakub\AppData\Local\Temp\p202\clone-head-build.log`, `clone-head-locked-restore.log`, `clone-head-test.log` — run at `213c5c5` (head)
- `C:\Users\jakub\AppData\Local\Temp\p202\wasm-lock-committed.json` — the pre-fix WASM lock entry kept for the NU1403 comparison
- `C:\Users\jakub\AppData\Local\Temp\p202\warm-locked-after-purge.log`, `warm-locked-force.log` — the developer-tree restores after the cache purge

The clone directories and their empty package caches were deleted after the runs.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] The committed WASM lock hash was machine-state dependent (`NU1403` in a cold clone)**

- **Found during:** Task 3, first cold-cache clone build (exit 1)
- **Issue:** `src/AzureDesignStudio/packages.lock.json` recorded `contentHash` `MIeBVeWk…` for `Microsoft.NET.Sdk.WebAssembly.Pack` 10.0.12, the value NuGet produced from a **nuget.org** copy that happened to sit in the developer's global cache. A machine without that cache entry resolves the same id/version from the SDK's `library-packs` fallback folder first — `C:\Program Files\dotnet\library-packs` ships `Microsoft.NET.Sdk.WebAssembly.Pack.10.0.12.nupkg`, and the cold restore recorded source `C:\Program Files\dotnet\library-packs` with `contentHash` `DbQBhmNA…`. The two disagree, so restore failed:

  ```
  src/AzureDesignStudio/AzureDesignStudio.csproj : error NU1403: Package content hash validation failed for Microsoft.NET.Sdk.WebAssembly.Pack.10.0.12. The package is different than the last restore.
  ```
  A plain (non-locked) restore failed identically, so the lock file — not the flag — was wrong. Verified the two copies really differ: nuget.org's nupkg SHA-512 is `3XajuLX9…`, the SDK's is `XjVNjIav…` (both 4.5 MB, same id/version, different bytes/signature). Only `src/AzureDesignStudio/packages.lock.json` carried this entry (`grep -l WebAssembly.Pack src/*/packages.lock.json`).
- **Fix:** Regenerated the lock for that project from an empty `NUGET_PACKAGES` so the recorded hash is the one every machine with SDK `10.0.401` computes (the SDK folder is pinned by `global.json`, so this is the deterministic choice — nuget.org resolution cannot be guaranteed once a fallback folder exists). One line changed. The stale global-cache folder `~/.nuget/packages/microsoft.net.sdk.webassembly.pack/10.0.12` (machine-local cache, not repository state) was then removed so the developer tree resolves the same copy; `dotnet restore src/AzureDesignStudio --locked-mode --force` now exits 0 and leaves the tree clean.
- **Files modified:** `src/AzureDesignStudio/packages.lock.json`
- **Commit:** `a5dd664`
- **Note for the orchestrator:** any machine that had already restored the nuget.org copy of that pack will hit the same `NU1403` until that one cache folder is cleared — NuGet's standard remedy for a content-hash mismatch.

**2. [Process — order] The proof ran before the requirement flip, then again at head**

- The first cold-cache proof ran at `a5dd664`; `BUILD-01` was marked Complete only after it (`213c5c5`); then the whole proof was re-run on a clean clone at `213c5c5` so the acceptance claim ("a cold-cache clone at your branch head") is literally true. The re-run's only delta is `REQUIREMENTS.md`.

**3. [Environment — dispatch defect, per the shared contract] Executed in a clone inside the assigned worktree**

- The isolated worktree `C:\Users\jakub\.omp\wt\t8f1a953de\m` is a checkout of another repository. Per the dispatch contract, the project was cloned into `./work` (`git clone C:/Users/jakub/Desktop/Projects/AzureDesignStudio ./work`) and the plan ran on branch `gsd/02-02-02` at `b435bc3`. Nothing outside the worktree was modified; the original checkout was not touched.

### Not-deviations, recorded

- **`codeql` job keeps `dotnet-version: '10.0.x'`** — the plan forbids editing it; it is recorded here as known drift (one floating SDK authority remains, in the `codeql` job only).
- **No GitHub Actions run** — this host cannot execute `ubuntu-latest`. The workflow's restore and guard commands were run byte-for-byte on the Windows host (bash), and the clone proof ran the same `dotnet` commands; no CI run is claimed.
- **Task 2 has no commit** — it changes no repository file, as specified.
- **`dotnet add package` succeeded under CPM** (`Directory.Packages.props` rewritten), so no hand edit was substituted.

## Issues Encountered

- The first cold clone failed in a way the warm tree never could (the `NU1403` above) — the exact class of defect BUILD-01 exists to catch.
- A `rm -rf` of the stale cache entry was blocked twice by MSBuild node-reuse holding pack DLLs; solved with `dotnet build-server shutdown` and a retry (a concurrent build in another worktree was not killed).
- NuGet ran `core.autocrlf=true` on the clone; the lock change is one line, so no line-ending rewrite occurred.

## Known Stubs

None.

## Threat Flags

None — the change surface is CI configuration, one generated lock hash, and a requirement status. No endpoint, auth path, or data boundary was added.

| Threat ID | Status |
|---|---|
| T-02-05 (lock guard repudiation) | Mitigated and proven negatively: NU1004 on drift, working-tree assertion for a written lock, count equality for a missing lock |
| T-02-06 (dependency substitution) | Mitigated: cache key is the lock-file hash; `RestoreLockedMode` exists only as a command-line flag |
| T-02-07 ("clean clone builds" claim) | Mitigated: clone at `213c5c5` with a non-existent `NUGET_PACKAGES`, logs named above |
| T-02-SC (package installs) | Accepted: no repository package changed; `dotnet add` ran only inside the deleted detached worktree |

## Next Phase Readiness

- 02-03 (formatting gate) can rely on the fast gate being green only when the lock files are honest.
- 02-04 (audit expiry) and 02-05 (Desktop job) must use `global-json-file: global.json` and the lock-file cache key; the Desktop job must not reuse the Linux locked restore.
- The `codeql` floating SDK line is still the one remaining second authority in `.github/workflows/build.yml`.

## Self-Check: PASSED

- `build.yml`, `packages.lock.json`, `REQUIREMENTS.md` exist at `213c5c5`; the tree is clean (`git status --porcelain` → 0 lines).
- Commits exist on `gsd/02-02-02`: `2bd7072`, `a5dd664`, `213c5c5` (measured: `git rev-list --count b435bc3..HEAD` = 3).
- Each commit touches only its declared files (`git show --stat` above).
- `BUILD-01` reads `- [x] **BUILD-01**` (line 50) and `| BUILD-01 | Phase 2 | Complete |` (line 164); no other requirement row changed (`git diff` = 2 insertions, 2 deletions).

---
*Phase: 02-reproducible-build-and-ci-baseline*
*Completed: 2026-10-22*
