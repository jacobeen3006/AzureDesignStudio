---
phase: 01-land-and-stabilize
plan: 01
subsystem: repository-integrity
tags: [git, gitignore, worktree, xunit, tracer]

requires: []
provides:
  - "Core land commit a2b934b4fa21ce41574965ce2ee2d39b3b27eb5c on top of the pre-land tip e9078d1a2d5f9a595fe025817c32f65cb6e6bee6"
  - "Four .gitignore hygiene rules (root-anchored /*.dll, .omo/, .swarm/, .planning/.omp-ui-state.json)"
  - "Forward-only removal of bd2.dll, bd_served.dll, .omo/run-continuation/ses_….json, .swarm/plan.md, .swarm/spec.md"
  - "Proven worktree cycle (add → full solution build incl. Desktop → offline suite 753 → remove --force) reused by plans 01-03 and 01-05"
affects: [01-03, 01-05, 01-06, 01-07]

actuals:
  tokens: 12923
  tasks: 3
  commits: 1

tech-stack:
  added: []
  patterns:
    - "Land a topic as one explicit-path commit, then prove the exact SHA in a throwaway worktree before pushing"
    - "Root-anchored /*.dll ignore rule so src/** build output stays visible to git"

key-files:
  created:
    - src/AzureDesignStudio.Core/Common/LinkTypeClassifier.cs
    - src/AzureDesignStudio.Core/Network/SubnetForm.razor
    - src/AzureDesignStudio.Core.Tests/LinkTypeTests.cs
    - src/AzureDesignStudio.Core.Tests/SubnetPlacementTests.cs
  modified:
    - .gitignore
    - src/AzureDesignStudio.Core/Components/AzureGroupComponent.razor.css
    - src/AzureDesignStudio.Core/Components/AzureNodeComponent.razor.css
    - src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs
    - src/AzureDesignStudio.Core/DataModelFactory.cs
    - src/AzureDesignStudio.Core/Network/AzureFirewallForm.razor
    - src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs
    - src/AzureDesignStudio.Core/Network/BastionsForm.razor
    - src/AzureDesignStudio.Core/Network/BastionsModel.cs
    - src/AzureDesignStudio.Core/Network/SubnetModel.cs
    - src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs

key-decisions:
  - "Core commit stages 20 explicit paths only; StencilTests.cs stays out because its catalog assertion travels with the shell commit (D-03), which is why the core suite is 753 and not 755"
  - "The five tracked artefacts are removed with git rm at the tip — no --cached untracking, no history rewrite (D-05/D-06)"
  - "The SC#1 ignore probe for src DLLs uses a src path outside bin/obj because the stock VisualStudio [Bb]in/ rule already matches any build-output DLL"

patterns-established:
  - "Per-commit proof: git worktree add ../ads-land-core HEAD → dotnet build src/AzureDesignStudio.sln → dotnet test … --filter \"Category!=LiveAzure\" → git worktree remove --force, strictly sequential"

requirements-completed: [SC1, SC3]

coverage:
  - id: D1
    description: "Tracked tree stops lying: no root .dll blob and no .omo//.swarm/ entry in git ls-files, while .planning/ stays tracked"
    requirement: "SC1"
    verification:
      - kind: other
        ref: "git ls-files | grep -E '\\.dll$|^\\.(omo|swarm)/' → empty (exit 1); git ls-files .planning | wc -l → 32"
        status: pass
    human_judgment: false
  - id: D2
    description: "Core commit restores into a clean worktree at its own SHA and builds the full solution including the Windows Desktop host"
    requirement: "SC3"
    verification:
      - kind: integration
        ref: "worktree at a2b934b: dotnet build src/AzureDesignStudio.sln -v m → 0 Error(s), log line 54 'AzureDesignStudio.Desktop -> …'"
        status: pass
    human_judgment: false
  - id: D3
    description: "Offline suite green at the core commit: Passed: 753, Failed: 0 (753 + 2 StencilTests facts = 755 from the shell commit on)"
    requirement: "SC3"
    verification:
      - kind: unit
        ref: "dotnet test src/AzureDesignStudio.Core.Tests --filter \"Category!=LiveAzure\" → Passed! - Failed: 0, Passed: 753, Skipped: 0, Total: 753"
        status: pass
    human_judgment: false
  - id: D4
    description: "Core commit reads as one topic and carries no credential-shaped or binary-archive content"
    requirement: "SC3"
    verification: []
    human_judgment: true
    rationale: "Reviewability is a judgement over the diff (git show --stat), not an assertion; the automated path-set, deletion-count, secret and binary scans all pass, but the topic call stays with the phase reviewer."

metrics:
  duration: 16min
  completed: 2026-10-21
status: complete
---

# Phase 01 Plan 01: Land Mechanism End-to-End Summary

**One reviewable core commit (a2b934b) lands the Core domain slice, four root-anchored .gitignore rules, and five forward-only artefact removals — and that exact SHA restores into a clean worktree, builds the full solution including the Windows Desktop host (0 errors), and runs the offline suite at Passed: 753 / Failed: 0.**

## Performance

- **Duration:** 16 min (dominated by two full solution build cycles, measured 181 s and 190 s wall clock)
- **Started:** 2026-10-21 ~09:45Z (first baseline command)
- **Completed:** 2026-10-21T09:59:58Z
- **Tasks:** 3 of 3
- **Files modified:** 20 committed paths (15 source/config + 5 deletions)

## Baseline (recorded before any mutation)

```
$ git rev-parse --short HEAD
e9078d1

$ git rev-list --count origin/main..HEAD
133

$ git status --porcelain | wc -l
60

$ git ls-files 'src/**/*.csproj' | wc -l
11

$ dotnet --version
10.0.401
```

`git rev-parse HEAD` (full): `e9078d1a2d5f9a595fe025817c32f65cb6e6bee6` — this is the pre-land tip.
`.gitignore` was 361 lines, CRLF, ending `/.planning/graphs/.last-build-snapshot.json\r\n`; the append added 6 lines and `git diff --stat` reported `1 file changed, 6 insertions(+)` (no whole-file re-write).

## The land commit

| Field | Value |
|-------|-------|
| Full SHA | `a2b934b4fa21ce41574965ce2ee2d39b3b27eb5c` |
| Tree SHA | `fa6cb068a305ab080f066e1833584cabb585ee37` |
| Parent (pre-land tip) | `e9078d1a2d5f9a595fe025817c32f65cb6e6bee6` |
| Subject | `feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch` |

`git show --name-status --format= HEAD` (exactly 20 entries, 5 of them `D`):

```
M	.gitignore
D	.omo/run-continuation/ses_0dbd65604ffe8SHcgoBM9S6Jw2.json
D	.swarm/plan.md
D	.swarm/spec.md
D	bd2.dll
D	bd_served.dll
A	src/AzureDesignStudio.Core.Tests/LinkTypeTests.cs
A	src/AzureDesignStudio.Core.Tests/SubnetPlacementTests.cs
A	src/AzureDesignStudio.Core/Common/LinkTypeClassifier.cs
M	src/AzureDesignStudio.Core/Components/AzureGroupComponent.razor.css
M	src/AzureDesignStudio.Core/Components/AzureNodeComponent.razor.css
M	src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs
M	src/AzureDesignStudio.Core/DataModelFactory.cs
M	src/AzureDesignStudio.Core/Network/AzureFirewallForm.razor
M	src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs
M	src/AzureDesignStudio.Core/Network/BastionsForm.razor
M	src/AzureDesignStudio.Core/Network/BastionsModel.cs
A	src/AzureDesignStudio.Core/Network/SubnetForm.razor
M	src/AzureDesignStudio.Core/Network/SubnetModel.cs
M	src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs
```

`D` entry count: **5** (`git show --name-status --pretty=format: HEAD | awk -F'\t' '$1=="D"{n++} END{print n+0}'` → `5`). Commit stat: `20 files changed, 484 insertions(+), 373 deletions(-)`.

## Pre-commit gates (literal output)

```
GATE1: count of D entries:
5
GATE2: StencilTests staged count:
0
GATE3: shell/docs/planning paths (should be empty):
(none)
GATE4: credential-shaped scan (should be empty):
(none)
GATE5: csproj/props (should be empty):
(none)
```

Gate commands and their result:

| Gate | Command | Output |
|------|---------|--------|
| Five deletions | `git diff --cached --name-status \| awk -F'\t' '$1=="D"{n++} END{print n+0}'` | `5` |
| No `StencilTests.cs` | `git diff --cached --name-only \| grep -c 'Core.Tests/StencilTests.cs'` | `0` |
| No shell/docs/planning path | `git diff --cached --name-only \| grep -E '^(src/AzureDesignStudio/\|src/AzureDesignStudio\.Desktop/\|README\.md\|docs/\|\.planning/)'` | `(none)` |
| No credential-shaped content | `git diff --cached -U0 \| grep -Ein 'clientsecret\|client_secret\|password=\|api[_-]?key\|BEGIN [A-Z ]*PRIVATE KEY\|AccountKey=\|SharedAccessKey'` | `(none)` |
| No csproj/props | `git diff --cached --name-only \| grep -E '\.csproj$\|Directory\.(Packages\|Build)\.props$'` | `(none)` |

Post-commit re-audit (Task 3, `git show HEAD`) repeated the credential scan, the added-binary-archive scan, and the cross-topic scan — all `(none)`; `git ls-files | grep -i harness` → `(none)`.

## Worktree proof

The plan's `<verify>` block was executed verbatim (worktree created from `HEAD`, build logged to `/c/Users/jakub/AppData/Local/Temp/ads-land-core-build.log`, suite tee'd to `/c/Users/jakub/AppData/Local/Temp/ads-land-core-test.log`), and it exited clean:

```
+ git worktree add ../ads-land-core HEAD
Preparing worktree (detached HEAD a2b934b)
HEAD is now at a2b934b feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch
+ rc=0
+ git worktree remove --force ../ads-land-core
verify rc=0
final=0
```

Build (cold restore, full solution):

```
54:  AzureDesignStudio.Desktop -> C:\Users\jakub\.omp\wt\tae7a0bce6\ads-land-core\src\AzureDesignStudio.Desktop\bin\Debug\net10.0-windows10.0.19041.0\win-x64\AzureDesignStudio.Desktop.dll
240:    91 Warning(s)
241:    0 Error(s)
Time Elapsed 00:03:00.62
```

Offline suite at the same SHA:

```
Passed!  - Failed:     0, Passed:   753, Skipped:     0, Total:   753, Duration: 1 s - AzureDesignStudio.Core.Tests.dll (net10.0)
```

`git worktree list` afterwards shows only the main checkout — no worktree or directory left behind.

**Why 753 is correct here:** SC#3's 755 is `753 + 2`. `StencilTests.cs` gains exactly two facts (`EveryCategoryHasAtLeastOneStencil`, `EveryImplementedResourceTypeHasAStencil`) and ships in the **shell** commit per D-03, because `AzureDesignStudio.Core.Tests.csproj:34` links `..\AzureDesignStudio\wwwroot\ads-stencils.json` and the new assertions need the categories-0–9 catalog. A 755 here would mean the test leaked out of the shell commit; a value below 753 would mean a test did not travel with its subject.

## SC#1 tracked-tree audit

```
$ git ls-files | grep -E '\.dll$|^\.(omo|swarm)/'
[exit 1 — no output, PASS condition]

$ git check-ignore -v bd2.dll .omo/run-continuation .swarm/plan.md .planning/.omp-ui-state.json
.gitignore:364:/*.dll	bd2.dll
.gitignore:365:.omo/	.omo/run-continuation
.gitignore:366:.swarm/	.swarm/plan.md
.gitignore:367:.planning/.omp-ui-state.json	.planning/.omp-ui-state.json

$ git ls-files .planning | wc -l
32

$ git ls-files bd2.dll bd_served.dll .omo .swarm
[no output — all five removals are untracked]
```

Root anchoring proven in both directions:

```
$ git check-ignore -v foo.dll
.gitignore:364:/*.dll	foo.dll          # root DLL ignored by the new rule

$ git check-ignore -v src/AzureDesignStudio.Core/foo.dll
[exit 1 — no rule matches; a DLL in the source tree stays visible]
```

The appended group occupies `.gitignore:362-367` — blank line, comment, then `/*.dll` (364), `.omo/` (365), `.swarm/` (366), `.planning/.omp-ui-state.json` (367). `.planning/` is still tracked at 32 files, and `git status --porcelain` no longer lists `.gitignore` as modified.

## Tasks

1. **Task 1 — Land mechanism end-to-end** (`a2b934b`): baseline recorded, five `git rm` removals, four ignore rules appended, 20 explicit paths staged, five pre-commit gates green, commit created, worktree proof run and torn down.
2. **Task 2 — SC#1 predicate and ignore direction** (no commit): command audit above, all assertions pass.
3. **Task 3 — Topic isolation and secret-free content** (no commit): 20-path count, 5 deletions, no cross-topic path, no credential string, no added archive, no `harness` path — all clean.

## Deviations from Plan

**1. [Rule 3 - Blocking] The src-DLL ignore probe path was adjusted**
- **Found during:** Task 2 (SC#1 audit)
- **Issue:** The plan's literal probe `git check-ignore -v src/AzureDesignStudio/bin/Debug/net10.0/ads.dll` returns a match (`exit 0`) — but from the pre-existing stock VisualStudio template rule `.gitignore:32:[Bb]in/`, not from the new `/*.dll` rule. The plan's fails_when reads that as "a DLL under src/ is ignored".
- **Fix:** Probed a src path outside build output instead — `src/AzureDesignStudio.Core/foo.dll` → no match, exit 1 — and added a positive root probe (`foo.dll` → `.gitignore:364:/*.dll`) to prove the anchor. No file change was needed; `[Bb]in/` correctly ignoring build output is pre-existing and unrelated to D-07.
- **Files modified:** none
- **Verification:** Both probes quoted above; the real SC#1 gate (`git ls-files | grep -E '\.dll$|^\.(omo|swarm)/'`) is empty.

**2. [Rule 1 - Bug] Baseline counts differ from the planning documents**
- **Found during:** Task 1 (baseline)
- **Issue:** RESEARCH/CONTEXT cite 126/128 unpushed commits and 56 dirty paths; the observed baseline is 133 unpushed commits and 60 dirty paths (the extra dirty paths are the phase's own planning artifacts plus the orchestrator's `.planning` state files).
- **Fix:** Recorded the measured integers above instead of the stale ones; no action needed.
- **Files modified:** none

---

**Total deviations:** 2 (1 blocking check adjustment, 1 stale-input correction)
**Impact on plan:** None on the delivered commit — both are measurement/reporting corrections. The commit's path set, subject, ignore rules and removal set are exactly as planned.

## Issues Encountered

- The isolated workspace checkout is on a **detached HEAD** (harness-created at `e9078d1`), so the commit `a2b934b` is not referenced by a branch here. That is expected: the orchestrator creates the authoritative commit in the parent checkout and compares tree SHAs, and this plan does not push.
- `git add` warned `LF will be replaced by CRLF` for the four new files (`core.autocrlf=true`). Expected on this machine; the committed blobs carry LF and the working tree CRLF.
- No `.csproj`, `Directory.Packages.props` or `Directory.Build.props` change was made, so the dependency set is unchanged (`T-01-SC-01` remains `accept`).

## Threat Flags

None — the commit adds no network endpoint, auth path, or schema surface. It removes tracked artefacts and grows `.gitignore`.

## Next Phase Readiness

- The core commit's parent `e9078d1a2d5f9a595fe025817c32f65cb6e6bee6` is the pre-land tip that plan 01-06 pushes first (D-10).
- The worktree cycle is proven end-to-end on this SHA; plans 01-03 (shell, expect 755) and 01-05 (docs, expect 755) repeat it sequentially.
- `git status --porcelain` still lists every untouched shell and docs path — proof that the slice was not over-staged.

## Self-Check: PASSED

- `a2b934b4fa21ce41574965ce2ee2d39b3b27eb5c` exists and is HEAD; tree `fa6cb068a305ab080f066e1833584cabb585ee37`.
- All 20 committed paths and 5 deletions observed in `git show --name-status`.
- Worktree build (`0 Error(s)`, Desktop line present) and suite (`Passed: 753`, `Failed: 0`) observed; worktree removed.
- SC#1 predicate empty; four ignore rules matched; root anchoring proven; `.planning/` still tracked at 32 files.
- Build log `/c/Users/jakub/AppData/Local/Temp/ads-land-core-build.log` and test log `/c/Users/jakub/AppData/Local/Temp/ads-land-core-test.log` exist on disk.
