---
phase: 01-land-and-stabilize
plan: 06
subsystem: release-publishing
tags: [git, git-push, fast-forward, git-bundle, clone-verification, provenance]

# Dependency graph
requires:
  - phase: 01-land-and-stabilize (plan 01-01)
    provides: "the core land commit and the pre-land tip e9078d1 that Task 1 pushes"
  - phase: 01-land-and-stabilize (plan 01-03)
    provides: "the shell land commit with its worktree proof (755) required before any push (D-02)"
  - phase: 01-land-and-stabilize (plan 01-05)
    provides: "the docs land commit with its worktree proof (755); the third and last land commit"
provides:
  - "origin/main advanced to the pre-land tip e9078d1a2d5f9a595fe025817c32f65cb6e6bee6 by fast-forward refspec push"
  - "origin/main advanced to the local head 3127ba3a0e22b4200de0a46b9738f8b4cd58490a (LAND_PUSH_SHA) by `git push origin main`"
  - "Zero non-`.planning` paths unpushed: `git rev-list --count origin/main..HEAD -- . ':!.planning/'` = 0"
  - "Out-of-band recovery bundle C:/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle (99,616,738 bytes) that clones back to BUNDLE_SHA 3127ba3 with a clean tree"
affects: [01-07, 01-08]

actuals:
  tokens: 5986            # 23945 chars (this SUMMARY) / 4 — this plan changes no tracked source or docs path
  tasks: 3
  commits: 0              # `git rev-list --count 3127ba3..HEAD` at SUMMARY write = 0; the plan's own `.planning`-only metadata commit is created after this measurement

plan_head_before: 3127ba3a0e22b4200de0a46b9738f8b4cd58490a
plan_head_after: 3127ba3a0e22b4200de0a46b9738f8b4cd58490a

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Publish pre-existing history alone first by a single-ref refspec (`git push origin \"$PRE:refs/heads/main\"`), so a rejection is isolated to the pre-existing history and not to the land"
    - "Subject-resolved SHAs (`git log --fixed-strings --grep`) plus `git merge-base --is-ancestor`, never log adjacency, because GSD metadata commits interleave with the land"
    - "Capture status output to a file first (`git status --porcelain > \"$T\" && test ! -s \"$T\"`) so a failing git cannot read as a clean tree"
    - "Bundle-proof form: compare the clone's HEAD to the recorded `BUNDLE_SHA`, not to a freshly read local head, because the plan's own metadata commit supersedes the head after the cut"

key-files:
  created:
    - C:/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle
  modified: []

key-decisions:
  - "The pre-land tip was resolved as the parent of the core subject, not by offset or by a quoted count; it resolved to e9078d1 and the count was re-measured at 133 rather than quoted from CONTEXT (126) or RESEARCH (128)"
  - "Task 2 pushed the branch, not a refspec: `git push origin main`, which carried the GSD per-plan metadata commits along as legitimate local work (SC#2 measures head equality, not a commit count)"
  - "The bundle was cut after both pushes (never before), so the `restores that same head` claim is about the published SHA"
  - "The literal `git status --porcelain` output is recorded verbatim including the four untracked orchestrator files; the clean-tree claim is scoped to the tracked tree and to non-`.planning` paths instead of being asserted as literal emptiness"
  - "`state sync` recomputed `completed_plans` from the five summaries on disk and set `state_head` to LAND_PUSH_SHA; no plan pointer was advanced by hand"

patterns-established:
  - "Push verification triple: `git ls-remote origin refs/heads/main | cut -f1` equals `git rev-parse HEAD` at the measurement instant, `git status --porcelain` captured to a file, and `git rev-list --count origin/main..HEAD -- . ':!.planning/'` = 0"
  - "Remote-side push proof from the `origin/main` reflog (`%gs` = `update by push`), which records exactly one entry per accepted push and has no forced-update form"

requirements-completed: [SC2]

coverage:
  - id: D1
    description: "The 133 pre-existing local commits are published first, by a single-ref fast-forward push of the core commit's parent, verified by SHA equality"
    requirement: SC2
    verification:
      - kind: other
        ref: "git push origin \"e9078d1a2d5f9a595fe025817c32f65cb6e6bee6:refs/heads/main\" → `e0e3080..e9078d1  e9078d1a2d5f9a595fe025817c32f65cb6e6bee6 -> main`; git ls-remote origin refs/heads/main | cut -f1 → e9078d1a2d5f9a595fe025817c32f65cb6e6bee6"
        status: pass
    human_judgment: false
  - id: D2
    description: "The three land commits are public: origin/main equals the local head at the measurement instant (3127ba3), nothing outside .planning/ is unpushed, and the tracked tree carries no uncommitted change"
    requirement: SC2
    verification:
      - kind: other
        ref: "git push origin main → `e9078d1..3127ba3  main -> main`; git ls-remote origin refs/heads/main | cut -f1 == git rev-parse HEAD (3127ba3a0e22b4200de0a46b9738f8b4cd58490a); git rev-list --count origin/main..HEAD -- . ':!.planning/' → 0; git status --porcelain --untracked-files=no → 0 lines"
        status: pass
    human_judgment: false
  - id: D3
    description: "An out-of-band bundle restores the published head: a clone reports BUNDLE_SHA with a clean tree, and no non-.planning content changed after the cut"
    requirement: SC2
    verification:
      - kind: integration
        ref: "git clone C:/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle C:/Users/jakub/AppData/Local/Temp/ads-bundle-verify → rev-parse HEAD = 3127ba3a0e22b4200de0a46b9738f8b4cd58490a; git -C <clone> status --porcelain → 0 lines; git rev-list --count BUNDLE_SHA..HEAD -- . ':!.planning/' → 0"
        status: pass
    human_judgment: false
  - id: D4
    description: "No force flag, lease, refspec plus-prefix, rebase, or reset touched the pushed history, and no rejected push was retried"
    requirement: SC2
    verification:
      - kind: other
        ref: "git reflog -n 10 → every entry is `commit:`; git reflog show origin/main → exactly two `update by push` entries (12:20:48, 12:20:59 +0200), no forced update; push output lines use `..` (fast-forward), never `+ … (forced update)`"
        status: pass
    human_judgment: false

metrics:
  duration: 9min
  completed: 2026-10-21
status: complete
---

# Phase 01 Plan 06: Publish and Make Recoverable Summary

**Both pushes landed fast-forward — the 133 pre-existing commits by the refspec of the core commit's parent, then the head at `3127ba3` with `ls-remote` equality — and a 95 MB parent-directory bundle clones back to that exact head with a clean tree.**

## Performance

- **Duration:** 9 min
- **Started:** 2026-10-21T10:16Z (first command; baseline timestamp `2026-10-21T10:20:35Z`)
- **Completed:** 2026-10-21T10:23Z
- **Tasks:** 3 of 3
- **Files modified:** 0 tracked source/docs paths; one untracked artefact outside the repository (the bundle)

## Accomplishments

- `origin/main` moved `e0e3080 → e9078d1` (133 pre-existing commits) and then `e9078d1 → 3127ba3` (the three land commits plus their GSD metadata commits), both as fast-forwards.
- Head equality was measured, not assumed: after each push `git ls-remote origin refs/heads/main | cut -f1` was compared to the intended local SHA and matched.
- `git rev-list --count origin/main..HEAD -- . ':!.planning/'` = **0**, so every non-`.planning` path of the land is published; the residual total unpushed count is also 0.
- The recovery bundle was cut at the published head and independently proven by cloning it into `C:/Users/jakub/AppData/Local/Temp/ads-bundle-verify`; the clone's `HEAD` equals `BUNDLE_SHA` and its tree is clean.
- The three land subjects each resolve to exactly one commit and their order is proven by ancestry, not by log adjacency.

## Task 1 — pre-existing history pushed first, by refspec

**Resolution by subject (never by offset):**

```text
$ git log --format='%H %s' -n 1 --fixed-strings --grep='feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch'
078a9d746f71baf4da4e7a9f2a96767f565f7fe9 feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch
$ git log --format=%H --fixed-strings --grep='<core subject>' | wc -l
1
$ git rev-parse 078a9d746f71baf4da4e7a9f2a96767f565f7fe9^
e9078d1a2d5f9a595fe025817c32f65cb6e6bee6
```

**Pre-land tip:** `e9078d1a2d5f9a595fe025817c32f65cb6e6bee6` (subject `docs(01): record phase 1 plan set in state`).

**Re-measured pre-existing commit count** (not quoted from CONTEXT's 126 or RESEARCH's 128):

```text
$ git rev-list --count origin/main..e9078d1a2d5f9a595fe025817c32f65cb6e6bee6
133
```

**Fast-forward assertions (exit codes):**

```text
$ git merge-base --is-ancestor origin/main "$PRE"   ; echo $?
0
$ git merge-base --is-ancestor "$PRE" HEAD          ; echo $?
0
$ git rev-parse origin/main
e0e3080a92c7102ec5c6e4aac1ae175a6d640c75
```

**Exact push command and its output:**

```text
$ git push origin "$PRE:refs/heads/main"
To https://github.com/jacobeen3006/AzureDesignStudio.git
   e0e3080..e9078d1  e9078d1a2d5f9a595fe025817c32f65cb6e6bee6 -> main
PUSH_EXIT=0
```

**Post-push verification:**

```text
$ git ls-remote origin refs/heads/main | cut -f1
e9078d1a2d5f9a595fe025817c32f65cb6e6bee6
EQUAL=yes
```

## Task 2 — the three land commits pushed, head equality measured

**Preconditions:** the three worktree proofs are recorded — core 753 (`01-01-SUMMARY.md`), shell 755 (`01-03-SUMMARY.md`), docs 755 (`01-05-SUMMARY.md`); all three greps matched. `git merge-base --is-ancestor origin/main HEAD` after Task 1 returned `0`, proving the land is a fast-forward on the verified head.

**Exact push command and its output:**

```text
$ git push origin main
To https://github.com/jacobeen3006/AzureDesignStudio.git
   e9078d1..3127ba3  main -> main
PUSH_EXIT=0
```

**Measurement at the instant after the push:**

```text
$ LAND_PUSH_SHA=$(git rev-parse HEAD)
LAND_PUSH_SHA=3127ba3a0e22b4200de0a46b9738f8b4cd58490a
$ git ls-remote origin refs/heads/main | cut -f1
3127ba3a0e22b4200de0a46b9738f8b4cd58490a
EQUAL_AT_INSTANT=yes

$ git rev-list --count origin/main..HEAD -- . ':!.planning/'
0
$ git rev-list --count origin/main..HEAD
0
$ git status --porcelain --untracked-files=no | wc -l
0
```

**`git status --porcelain`, captured to a file first (`STATUS_EXIT=0`), printed exactly four lines — all pre-existing untracked orchestrator files under `.planning/`, none of them land content:**

```text
?? .planning/.omp-checkpoint.json
?? .planning/.omp-next-action.json
?? .planning/.omp-task-results.json
?? .planning/milestone.lock
```

These four paths existed before this plan began (they are OMP harness state and the GSD milestone lock, the latter holding a live pid), so they are neither land content nor the executor's to delete; they are also not ignorable within this plan's constraints, because that would require editing the tracked `.gitignore` — a fourth land commit, which D-01 forbids. The clean-tree claim is therefore recorded in its measurable form: **zero tracked modifications** (`--untracked-files=no` → 0) and **zero non-`.planning` change** (`-- . ':!.planning/'` → 0 lines). The tracked-only and non-planning forms are both empty; only the untracked harness files remain visible to plain `git status`.

**What this measurement is, and is not.** `commit_docs` is `true`, so the workflow writes `docs(01-06): complete …` after this plan and `docs(phase-1): complete phase execution` after the phase verifies. Both are `.planning`-only commits that necessarily trail `origin/main` until somebody pushes them. This plan therefore claims: every non-`.planning` path is published, the land head is an ancestor of `origin/main`, and `origin/main == HEAD` **at the measurement instant** above. It claims no durable bare equality — plan 01-08's closing push is the only step positioned after the last metadata commit and owns the literal phase-gate equality.

## Task 3 — out-of-band bundle, clone-verified

```text
$ BUNDLE_SHA=$(git rev-parse HEAD)
BUNDLE_SHA=3127ba3a0e22b4200de0a46b9738f8b4cd58490a
$ git bundle create ../AzureDesignStudio-$(git rev-parse --short "$BUNDLE_SHA")-$(date +%Y%m%d).bundle --all
BUNDLE_CREATE_EXIT=0
BUNDLE=C:\Users\jakub\Desktop\Projects\AzureDesignStudio-3127ba3-20261021.bundle
BUNDLE_SIZE_BYTES=99616738
$ git rev-parse --show-toplevel
C:/Users/jakub/Desktop/Projects/AzureDesignStudio
INSIDE_REPO=no
```

The tree did not become dirty by creating it: `git status --porcelain` still printed the same four untracked harness lines before and after.

```text
$ git clone C:/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle C:/Users/jakub/AppData/Local/Temp/ads-bundle-verify
Cloning into 'C:/Users/jakub/AppData/Local/Temp/ads-bundle-verify'...
CLONE_EXIT=0
CLONE_HEAD=3127ba3a0e22b4200de0a46b9738f8b4cd58490a
HEAD_MATCH=yes                       # compared to $BUNDLE_SHA, not to a freshly read head
CLONE_BRANCH=main
CLONE_COMMIT_COUNT=260
CLONE_STATUS:                        # git -C <clone> status --porcelain
(no output — 0 lines)
$ git -C "$CLONE" log -1 --format=%s
docs(01-05): complete docs land commit with 12-path audit and 755 proof
$ git rev-list --count "$BUNDLE_SHA"..HEAD -- . ':!.planning/'
0
CLONE_DELETED=yes
BUNDLE_RETAINED=yes
```

The local repository also reports 260 commits (`git rev-list --count HEAD`), so the clone restores the full local history, not a truncated slice. Lifecycle per D-11 is not claimed here: plan 01-08 re-cuts the bundle at the converged head, deletes this superseded one, and the phase gate removes the converged one.

## Acceptance evidence

### 1. Pre-land tip, count, ancestry, push command, post-push SHA

| Item | Observed value |
|------|----------------|
| Core commit (subject-resolved, exactly 1) | `078a9d746f71baf4da4e7a9f2a96767f565f7fe9` |
| Pre-land tip = `core^` | `e9078d1a2d5f9a595fe025817c32f65cb6e6bee6` |
| Re-measured `origin/main..PRE` count | `133` |
| `git merge-base --is-ancestor origin/main "$PRE"` | exit `0` |
| `git merge-base --is-ancestor "$PRE" HEAD` | exit `0` |
| Push command | `git push origin "$PRE:refs/heads/main"` |
| Push output | `e0e3080..e9078d1  e9078d1a2d5f9a595fe025817c32f65cb6e6bee6 -> main` |
| Post-push `ls-remote` | `e9078d1a2d5f9a595fe025817c32f65cb6e6bee6` (equal) |

### 2. Land push

| Item | Observed value |
|------|----------------|
| `LAND_PUSH_SHA` | `3127ba3a0e22b4200de0a46b9738f8b4cd58490a` |
| Push output | `e9078d1..3127ba3  main -> main` |
| `ls-remote` equality at that instant | equal (`EQUAL_AT_INSTANT=yes`) |
| `git status --porcelain` | four untracked `.planning/` harness files, quoted verbatim above — **not** literally empty |
| `git status --porcelain --untracked-files=no` | `0` lines |
| `git status --porcelain -- . ':!.planning/'` | `0` lines |
| `git rev-list --count origin/main..HEAD -- . ':!.planning/'` | `0` |
| `git rev-list --count origin/main..HEAD` | `0` |

### 3. Bundle

| Item | Observed value |
|------|----------------|
| Absolute path | `C:\Users\jakub\Desktop\Projects\AzureDesignStudio-3127ba3-20261021.bundle` |
| Size | `99,616,738` bytes (`ls -l`) |
| `BUNDLE_SHA` | `3127ba3a0e22b4200de0a46b9738f8b4cd58490a` |
| Clone path | `C:/Users/jakub/AppData/Local/Temp/ads-bundle-verify` (deleted after proof) |
| Clone `HEAD` | `3127ba3a0e22b4200de0a46b9738f8b4cd58490a` (`HEAD_MATCH=yes`) |
| Clone `status --porcelain` | `0` lines |
| `git rev-list --count "$BUNDLE_SHA"..HEAD -- . ':!.planning/'` | `0` |

### 4. No force, lease, refspec `+`, retry, or rebase

`git reflog -n 10` (verbatim — the two push operations are not reflog entries on `HEAD`, because a push does not move a local ref):

```text
3127ba3 HEAD@{0}: commit: docs(01-05): complete docs land commit with 12-path audit and 755 proof
3315b94 HEAD@{1}: commit: docs: correct project count and auth model; record publisher and signing route
199a28c HEAD@{2}: commit: docs(01-04): complete current-truth reconciliation and publisher record
9789076 HEAD@{3}: commit: docs(01-03): complete shell land commit with 35-path audit and 755 proof
ea8ac77 HEAD@{4}: commit: feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag
2b03b2b HEAD@{5}: commit: docs(01-02): complete pointer-zone repair with before/after harness transcripts
dfeac63 HEAD@{6}: commit: docs(01-01): complete core land commit and record worktree proof
078a9d7 HEAD@{7}: commit: feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch
e9078d1 HEAD@{8}: commit: docs(01): record phase 1 plan set in state
f7d21ae HEAD@{9}: commit: docs(01): resolve research questions and map validation to plans
```

The remote-side reflog is where the pushes themselves appear — exactly two entries, one per push, and no forced-update form:

```text
$ git reflog show origin/main --date=iso --format='%h %gd %cd %gs'
3127ba3 origin/main@{2026-10-21 12:20:59 +0200} 2026-10-21 12:19:48 +0200 update by push
e9078d1 origin/main@{2026-10-21 12:20:48 +0200} 2026-10-21 11:46:24 +0200 update by push
```

Corroborating facts: the push output lines use `..` (`e0e3080..e9078d1`, `e9078d1..3127ba3`), never the `+ sha...sha (forced update)` form; every `HEAD` reflog entry is a `commit:`; and the only non-commit entries anywhere in this session's window are the two `update by push` lines above. The four `commit (amend)` and three `reset` entries present under `git reflog --all` are dated 2026-10-13/14, predate this phase, and are not from this plan. No rejected push occurred, so no retry was needed.

### 5. Three land subjects, one commit each, ancestry-proven order

```text
resolves=1  078a9d7 feat(core): classify link types, add subnet form, and untrack root blobs and agent scratch
resolves=1  ea8ac77 feat(shell): land Round 2 shell components, stencil catalog, and remount-safe pointer drag
resolves=1  3315b94 docs: correct project count and auth model; record publisher and signing route

$ git merge-base --is-ancestor 078a9d7 ea8ac77 ; echo $?   → 0
$ git merge-base --is-ancestor ea8ac77 3315b94 ; echo $?   → 0
$ git merge-base --is-ancestor 078a9d7 3315b94 ; echo $?   → 0
```

`git log` adjacency is deliberately not asserted: GSD wrote `docs(01-01)`, `docs(01-02)`, `docs(01-03)`, `docs(01-04)` and `docs(01-05)` metadata commits between the land commits, and they ride along on `origin/main` as legitimate local work.

## Files Created/Modified

- `.planning/phases/01-land-and-stabilize/01-06-SUMMARY.md` — this summary (the only committed file from this plan).
- `C:/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle` — recovery bundle outside the repository, never committed.
- `.planning/ROADMAP.md`, `.planning/STATE.md`, `.planning/state.json` — GSD bookkeeping updated by `roadmap update-plan-progress`, `state sync`, `state record-session`, `state record-metric`, `state add-decision`.

## Decisions Made

- Resolved the pre-land tip as the parent of the subject-matched core commit, and re-measured the count (133) instead of quoting CONTEXT's 126 / RESEARCH's 128 — the plan's explicit instruction, and the reason the recorded count differs from both documents.
- Task 2 pushed the branch (`git push origin main`) rather than a refspec, so the metadata commits travelled with it; SC#2 measures head equality, not a commit count.
- Cut the bundle strictly after both pushes (the RESEARCH ordering hazard), so `BUNDLE_SHA` is the published head rather than a pre-push one.
- Recorded the status proof in its measurable form (tracked-only = 0, non-`.planning` = 0) with the four harness lines quoted verbatim, instead of asserting literal emptiness that the orchestrator's own files contradict.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `git status --porcelain` is not literally empty because of four orchestrator-owned untracked files**
- **Found during:** Task 2 (head-equality and clean-tree verification)
- **Issue:** The plan's `<verify>` ends with `test ! -s "$T"` on the full `git status --porcelain`. The observed output is four untracked `.planning/` files — `.omp-checkpoint.json`, `.omp-next-action.json`, `.omp-task-results.json`, `milestone.lock` — which existed before this plan started and are the OMP harness's own state plus a live GSD lock (pid recorded inside). They are not land content, and they are not the executor's to delete.
- **Fix:** Recorded the four lines verbatim (nothing suppressed), and proved the clean-tree claim in the two forms that are about the land: `git status --porcelain --untracked-files=no` → 0 lines, and `git status --porcelain -- . ':!.planning/'` → 0 lines. No `.gitignore` change was made, because that is a tracked edit and would be the fourth land commit D-01 forbids; no untracked file was deleted; no local exclude was added.
- **Files modified:** none
- **Verification:** Both scoped commands quoted with their output in §2.
- **Committed in:** n/a (no file change)

---

**Total deviations:** 1 auto-fixed (1 blocking, resolved by scoping the proof and recording the raw output)
**Impact on plan:** None on the pushes or the bundle. The deviation is confined to how the clean-tree claim is stated: the land itself is fully published and carries no uncommitted tracked change.

## Issues Encountered

- **First clone-verification attempt passed an MSYS-style path to a Windows-native `git`.** `git clone /c/Users/jakub/Desktop/Projects/AzureDesignStudio-3127ba3-20261021.bundle /c/Users/jakub/AppData/Local/Temp/ads-bundle-verify` failed with `fatal: repository '…bundle' does not exist` / `fatal: cannot change to '…'`. This was a verification-command path-format error, not a bundle defect: `ls -l` showed the 99,616,738-byte file in place throughout, and the re-run with native `C:/…` paths cloned successfully (`CLONE_EXIT=0`). No push or bundle action was affected.
- **`__pycache__`-style state drift:** `STATE.md` had `completed_plans: 0` and `state_head: e9078d1` before this plan (prior plans' metadata commits only updated it once). `state sync` recomputed `completed_plans` to the five summaries on disk and set `state_head` to `3127ba3`; the `Plan: 1 of 8` prose pointer was left to the orchestrator rather than advanced by hand out of wave order.

## User Setup Required

None — no external service configuration required. The push used the already-configured credential helper; write access, unverified in RESEARCH, was proven by the two accepted pushes.

## Next Phase Readiness

- Plan 01-07's fresh-clone audit can compare against `BUNDLE_SHA` `3127ba3a0e22b4200de0a46b9738f8b4cd58490a` (reminder: that audit should use the bundle, since the local head will have advanced by this plan's metadata commit).
- `origin/main` is at `3127ba3`; the phase's literal gate equality is re-established only by plan 01-08's closing push, after the last metadata commit.
- The bundle `AzureDesignStudio-3127ba3-20261021.bundle` is retained at the project parent; plan 01-08 owns its supersession and deletion.

## Self-Check: PASSED

- Pre-land tip `e9078d1a2d5f9a595fe025817c32f65cb6e6bee6`, count `133`, both ancestry checks exit `0`, push `e0e3080..e9078d1`, post-push `ls-remote` equal — all observed.
- `LAND_PUSH_SHA` `3127ba3a0e22b4200de0a46b9738f8b4cd58490a`, `ls-remote` equal at that instant, non-`.planning` unpushed count `0`; `git status --porcelain` observed and printed four untracked orchestrator files, with tracked-only and non-`.planning` views empty — recorded rather than elided (see Deviation 1).
- Bundle at `C:\Users\jakub\Desktop\Projects\AzureDesignStudio-3127ba3-20261021.bundle`, 99,616,738 bytes, `BUNDLE_SHA` `3127ba3`, clone `HEAD` equal, clone status `0` lines, non-`.planning` count `0`, clone deleted, bundle retained.
- `git reflog -n 10` pasted; `origin/main` reflog shows exactly two `update by push` entries; no force, lease, `+`refspec, rebase, or retry anywhere in the session.
- Three subjects each resolve to one commit; ancestry core → shell → docs proven by `git merge-base --is-ancestor` (three exit-0 results).

---
*Phase: 01-land-and-stabilize*
*Completed: 2026-10-21*
