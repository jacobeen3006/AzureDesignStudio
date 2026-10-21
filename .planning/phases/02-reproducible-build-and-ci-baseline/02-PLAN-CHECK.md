# Phase 2 — Plan Check (pre-execution)

**Phase:** 2 — Reproducible Build and CI Baseline
**Plans checked:** 7 (`02-01` … `02-07`), 17 tasks, waves 1–7 (strictly sequential)
**Round 1 verdict (historical):** ISSUES FOUND — 3 blocker(s), 7 warning(s), 4 info
**Round 2 verdict (revision 1, historical):** ISSUES FOUND — 0 blocker(s), 1 warning(s), 5 info
**Latest verdict — Round 3 (final confirmation):** VERIFICATION PASSED — 0 blocker(s), 0 warning(s), 5 carried info
(round-1 blockers all closed as properties; the round-2 warning closed in round 3; see “Round 2 — pre-execution re-check”
and “Round 3 — final confirmation” below)

Method: goal-backward from ROADMAP SC#1–SC#5 and BUILD-01…BUILD-05, then the plan-quality
dimensions. Deterministic probes consumed as given (`verify-command-paths` → `not_applicable`;
`verify-failure-directions` → `ok`, 17/17 runnable commands carry a stated failure mode), plus
`gsd-tools query verify.plan-structure` on all seven plans and live read-only measurement of the
claims that the plans turn into gates. No plan was modified, nothing was committed.

---

## 1. Success-criterion coverage (goal-backward)

| SC | What must be TRUE | Plans / tasks that make it true | Observable evidence the verifier will read | Covered |
|----|-------------------|--------------------------------|-------------------------------------------|---------|
| SC#1 | Clean clone, no warm caches, SDK `10.0.401`/`latestPatch`, locked versions; restore fails if a lock file would change | `02-01` T1 (`global.json`, `MauiVersion`), T2 (CPM), T3 (11 locks); `02-02` T1 (Linux `--locked-mode` + three-part guard), T2 (NU1004 + missing-lock probes), T3 (cold-cache clone) | clone's `dotnet --version`; 11 tracked `src/*/packages.lock.json`; `error NU1004` log; guard output non-empty after a lock is removed while `git diff` alone passes; clone build/restore/test logs with `Passed: 755`; `BUILD-01` flipped only after that clone | Yes |
| SC#2 | PR with a deliberate `MauiProgram.cs` error fails the Windows Desktop job; duration recorded | `02-05` T1 (job + metrics), `02-06` T1 (throwaway PR red run) | run URL whose `Desktop (windows-latest)` conclusion is failure with `error CS####` naming `MauiProgram.cs`; PR `closed, unmerged`; probe branch absent from `git ls-remote`; `desktop-build-metrics.json` with `seconds` | Yes |
| SC#3 | CI fails on High/Critical vulnerable packages; one AutoMapper allowlist entry with a dated expiry | `02-04` T1 (audit level, single suppression, expiry target, both `NoWarn` blocks deleted), T2 (four probes) | Probe A `error NU1903`; Probe B clean restore; Probe C `expired on 2020-01-01`; `-getProperty:NuGetAuditLevel` = `high`; exactly one `NuGetAuditSuppress` item | Yes, with a transitive-audit gap (W5) |
| SC#4 | Versions only in `Directory.Packages.props`; warnings as errors; a changed file violating formatting fails | `02-01` T2 (CPM), `02-03` T1 (TWE + `CS0169` probe), `02-03` T2 (`.editorconfig` + whitespace commit), `02-05` T1/T2 (changed-file gate + exit 0/exit 2 proof), `02-06` T2 (green runs carry the format gate line) | central file census; `error CS0169` from a probe importing the repo props; `dotnet format --verify-no-changes` exit 0 on the normalised tree and exit 2 on a mis-indented file; ledger `Format (changed files)=success` | Yes, but the census gate is wrong (B1) |
| SC#5 | After two consecutive green non-blocking Windows runs the job becomes blocking and every merge requires it | `02-05` T1 (non-blocking job + metrics), T3 (ledger), `02-06` T2 (two green runs), `02-07` T1 (delete `continue-on-error`), T2 (required check + proof) | two `Runs` rows with distinct run URLs, `true`, `success`, numeric seconds; `continue-on-error` absent; `02-REQUIRED-CHECK-PROOF.txt` naming `Desktop (windows-latest)`; settings human-check | Yes |

**Requirement IDs.** All five appear in plan frontmatter and have covering tasks:
BUILD-01 → 02-01, 02-02 · BUILD-02 → 02-05, 02-06 · BUILD-03 → 02-01, 02-03, 02-05, 02-06 ·
BUILD-04 → 02-04 · BUILD-05 → 02-05, 02-06, 02-07. No PROJECT.md requirement relevant to this
phase is silently dropped.

**Premature `Complete`.** Handled honestly: `02-01` T1 reverts `BUILD-01` to `[ ]`/`Pending` in the
same commit that starts the work, and only `02-02` T3 — after the cold-cache clone proof — re-marks
it. Every other BUILD row is flipped only by the plan that owns its evidence.

---

## 2. Dimension results

| # | Dimension | Result |
|---|-----------|--------|
| 1 | Requirement coverage | PASS for the five requirements; SC#3's "any High/Critical" is narrower than stated (W5) |
| 2 | Task completeness | All 17 tasks carry `read_first`/`action`/`verify`/`acceptance_criteria`/`done`; `<fails_when>` 17/17. Three gates cannot pass as written (**B1, B2, B3**); checkpoint elements missing (W1, W4) |
| 3 | Dependency correctness | PASS — acyclic `01→02→03→04→05→06→07`, all references exist, no forward reference, waves conservative relative to `depends_on` (prevents `REQUIREMENTS.md` races between the five plans that edit it) |
| 3b | Undeclared / temporal coupling | PASS — every plan sits in its own wave, so there are no same-wave plan pairs |
| 4 | Key links planned | PASS — each `key_links` entry is realised by an action: props→packages ordering, `--locked-mode`↔locks↔guard, TWE↔`NU190x`, `.editorconfig`↔normalisation↔CI gate, job name↔required-check context |
| 5 | Scope sanity | `estimate-check --calibrated`: 60k/55k/50k/50k/60k/40k/40k against a 100k budget — none over. Change surface above target for `02-01` and `02-03` (W7). `confidence: low` on all seven (no calibration samples) |
| 6 | Verification derivation | PASS — truths are runnable observations, not intentions; one truth is unattainable as specified (B2) |
| 7 | Context compliance | SKIPPED — no `CONTEXT.md` for this phase (standard mode); verified against ROADMAP/REQUIREMENTS/RESEARCH instead |
| 7b | Scope reduction | PASS — no `v1`/"static for now"/"future enhancement"/"will be wired later" language in any task; the AutoMapper deferral to Phase 8 is the roadmap's own mapping, not a planner invention |
| 7c | Architectural tier compliance | PASS — RESEARCH's Responsibility Map is honoured: pins in root MSBuild/Packages props, audit item in props + expiry in targets, formatting policy at root and the check on Windows, the merge gate in workflow + repository settings |
| 8 | Nyquist compliance | PASS — 8a 17/17 automated; 8b no watch mode (per-task ~80 s build latency noted, I4); 8c every wave has ≥2 of 3 tasks verified; 8d no `MISSING`; 8e `02-VALIDATION.md` present; 8f 17/17 failing directions stated |
| 9 | Cross-plan data contracts | PASS — the only shared mutable artefacts (`Directory.Build.props`, `global.json`, `build.yml`, `REQUIREMENTS.md`) are each touched by one plan per wave, in dependency order; no conflicting transforms |
| 10 | CLAUDE.md compliance | SKIPPED — no `./CLAUDE.md`. `AGENTS.md` was checked instead: the plans correct the two false conventions (line 57/67 `No Directory.Build.props`, line 74 test bullet) and the stale CI description (line 43/58) rather than leaving them lying |
| 11 | Research resolution | `02-RESEARCH.md` `## Open Questions` has no `(RESOLVED)` marker and no per-question resolution (W6) |
| 12 | Pattern compliance | PASS — every new file names its PATTERNS.md section, including the three deviations that matter (`dotnet workload restore` instead of `workload install`; `[System.String]::Compare`; no `end_of_line`/`charset`) |
| — | Verify command format sanity | B1 and W3; no `^`-anchor-on-tree-output, no `2>/dev/null || echo` feeding a comparison, no `|| true` |
| — | Verify command path resolvability | Probe `not_applicable`; manual check: every referenced existing path resolves (`MauiProgram.cs`, `Core/APIM/APIMModel.cs`, `src/version.json`, `publish.yml`, `AGENTS.md:43,57,58,67,74`, 11 `src/*/*.csproj`); new artefacts are created before they are referenced |
| — | Numeric / factual claim authority | The `74`/`58` package census is contradicted by live measurement (B1); the other numbers hold (11 csproj, 755 tests, 91 `PRI263`, 316/38 whitespace, `10.0.401`, `10.0.110`, `10.0.401.1`) |
| — | Ordering hazards | PASS — no gate is flipped stricter than the tree supports: no locked mode before the locks are committed; TWE before the audit level (so `NU1903` promotion has a suppression in place); normalisation before the CI format gate; `continue-on-error` removed only after two recorded greens |
| — | Constraint integrity | PASS — no MSIX, no new test project, no UI work, `publish.yml` and `codeql` untouched, `WindowsPackageType=None` preserved (`src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj:41`), triggers are `pull_request`/`push` only |

---

## 3. Blockers

### B1 — `02-01` T2's package census and `Version=` gate are wrong and mutually unsatisfiable

Measured on the current tree (read-only):

- `grep -h 'PackageReference[^>]*Version=' src/*/*.csproj | wc -l` → **74**, of which **5** are the
  commented-out rows in `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj:21-25`.
  Declared references carrying `Version=` → **69**.
- Distinct ids across all matching lines → **58**; distinct ids on non-commented rows → **53**.

The task's action says "Leave the five commented-out rows … exactly as they are", but its gate is
`test "$(grep -h 'PackageReference[^>]*Version=' src/*/*.csproj | wc -l)" -eq 0` and its central-file
gate is `-eq 58` rows / `-eq 58` ids. With the prohibition honoured the first gate can only reach
**5**, and an honest central file holds **53** rows. Adding the five commented ids to reach 58 still
leaves the first gate failing. `02-VALIDATION.md` (BUILD-03 row) and `02-07`'s phase gate repeat the
same impossible measure. RESEARCH E15 and PATTERNS carry the same off-by-five, so this is a stale
measurement the plan inherited rather than a new slip.

### B2 — `02-04` T2 Probe A cannot produce the diagnostic it asserts

Probe A restores Core with `-p:DirectoryBuildPropsPath=<temp props>` that deliberately omits
`AdsAuditSuppressionAutoMapperExpires`, while `DirectoryBuildPropsPath` does not replace
`Directory.Build.targets`. The repo target therefore evaluates with an empty property, and
`[System.String]::Compare('', today)` is negative.

Verified in a scratch project with the same `props`+`targets` shape:

```
Directory.Build.targets(7,5): error : EXPIRY-FIRED: expiry=[] today=2026-10-21
```

`Restore` aborts on the expiry `<Error>` before NuGet's audit runs, so `$D/A.log` never contains
`error NU1903`. The task's own `grep -q 'error NU1903' "$D/A.log"` therefore always fails, and the
`must_haves` truth "Removing the suppression makes the same restore fail with `error NU1903`" is
unattainable through the specified probe — the one probe that proves SC#3's gate is real.

### B3 — `02-05` T1's step name and its verify lookup disagree

Action: "a step named exactly `Restore workloads (image ships .NET 9 MAUI only)`".
Verify: `assert 'dotnet workload restore …' in s['Restore workloads']['run']`, where `s` is keyed by
step name. Following the action makes the lookup raise `KeyError`; following the lookup violates
"named exactly". One of the two must change, and `02-07` T1 re-parses the job but does not resolve it.

---

## 4. Warnings

**W1 — checkpoint tasks lack the elements the executor's checkpoint protocol reads.** `verify.plan-structure`
reports `missing <instructions>`, `<verification>`, `<resume-signal>` for `02-06` T1 and T2 and
`02-07` T2. These are the phase's only human-gated tasks; the surrounding prose is good
(`checkpoint:human-action`, `<human-check>`, ledger rows), but the structural contract the executor
reads is absent.

**W2 — plan bodies contain the literals their own negative gates ban.** The validator flags
`RestoreLockedMode` (`02-01`), `NU1903` and `MSB4086` (`02-04`), `pull_request_target` (`02-05`),
`this line is not C#` (`02-06`) and `continue-on-error` (`02-07`). Most are harmless because the
grep excludes `.planning/`; the live hazard is `02-04`, where the gate is `grep -rn 'NU1903' src/`
and the natural place to explain the deletion is a comment inside the same csproj — which would fail
the gate the comment describes.

**W3 — `02-04` T1's source gate reads generated artefacts.** `grep -rn 'NU1903' src/` currently
returns 17 matches: the two tracked `NoWarn` lines plus 15 hits in `obj/` (`*.nuget.dgspec.json`,
`project.assets.json`, `project.nuget.cache`, `*.assets.cache`) across seven projects, because a
dgspec embeds every project in its graph. The verify runs the grep *before* its own restore, so it
fails on a tree whose last restore predates the `NoWarn` deletion.

**W4 — `02-07` T2 parses a ledger literal its action never mandates.** The verify needs
`^Flip commit: <sha>`, but the action only says "the flip commit SHA from Task 1"; a table cell or
`Flip commit SHA:` leaves `FLIP` empty and the gate fails. `Required check:` and `Proof:` are
prescribed, this one is not.

**W5 — SC#3's "any High/Critical vulnerable package" is narrower than stated.** RESEARCH E24: the
SDK defaults `NuGetAuditMode` to `all` only for TFMs ≥ 10.0 and to `direct` otherwise.
`SourceGeneration` targets `netstandard2.0`, so a High/Critical advisory in *its* transitive
packages is never reported. No plan sets `NuGetAuditMode=all`; `02-04` only forbids downgrading it.

**W6 — RESEARCH's open questions are unresolved by marker.** `## Open Questions` has three entries
with recommendations but no `(RESOLVED)` suffix and no per-question marker. All three
recommendations are honoured by the plans (Windows-only format gate; Linux workload step untouched;
no `PRI263` action), so only the resolution state is missing.

**W7 — change surface above the per-plan target.** `02-01` lists 26 paths and `02-03` ~39 (38 `.cs`
plus `.editorconfig`). Mitigating and stated: 11 lock files are generated artefacts committed as a
unit, the ten csproj edits are mechanical attribute removals, the 38 `.cs` are whitespace-only and
proven so by `git show -w`, and every estimate is inside the 100k budget. Review cost, not build
risk.

---

## 5. Advisories (info)

**I1 — `02-01` T3's pre-staging assertion reads as "exactly 11 entries".** The same task also
modifies `Directory.Build.props`, so `git status --porcelain` shows a twelfth entry. The commit
contract (12 paths) is correct; only the intermediate sentence is ambiguous.

**I2 — ROADMAP's Phase 2 implementation note still prescribes `dotnet format --verify-changed`.**
That flag does not exist in this SDK (RESEARCH E23); the plans correctly use `--verify-no-changes`.
The roadmap line should be corrected so the phase verifier does not hold the plans to a nonexistent
flag.

**I3 — `02-07` region-scope lint.** Task 2's file-wide `! grep 'continue-on-error'` overlaps Task 1,
which must remove the same string. Benign: Task 1 commits first, so the ban is satisfied when
Task 2 verifies. Recorded so the advisory is visible rather than silently suppressed.

**I4 — per-task verify latency.** Each task's `<automated>` runs a full Release build (~80 s,
measured) against the 30 s guideline in Nyquist 8b. `02-VALIDATION.md` explicitly accepts
build-bound latency (~120 s), so this is a recorded trade-off, not a defect.

---

## 6. Structured issues

```yaml
issues:
  - plan: "02-01"
    dimension: numeric_claim_authority
    severity: BLOCKER
    required_property: >-
      The CPM census counts only declared references, and every gate derived from it is
      satisfiable on the intended end state.
    description: >-
      Task 2's gate is "grep -h 'PackageReference[^>]*Version=' src/*/*.csproj | wc -l == 0" and its
      central-file gate is "58 rows / 58 unique ids". Measured on the current tree: 74 matching lines,
      of which 5 are the commented-out rows at Core.csproj:21-25 that the same task forbids touching,
      so the intended end state is 5, never 0; declared references with Version= are 69 and distinct
      declared ids are 53, not 58. Obeying the prohibition and the count gate simultaneously is
      impossible, and 02-VALIDATION.md's BUILD-03 row and 02-07's phase gate repeat the same measure.
    fix_hint: >-
      Filter commented rows from the census (grep -v '<!--'), expect 53 central rows from 69 declared
      references, and carry the corrected figures through must_haves, 02-PATTERNS.md and 02-VALIDATION.md.
  - plan: "02-04"
    dimension: task_completeness
    severity: BLOCKER
    required_property: >-
      Each probe isolates the single mechanism it claims to prove, so the asserted diagnostic is the
      one the run actually produces.
    description: >-
      Task 2 Probe A overrides DirectoryBuildPropsPath with a temp props that omits
      AdsAuditSuppressionAutoMapperExpires, but the repository's Directory.Build.targets is still
      imported into src/AzureDesignStudio.Core. The expiry target then evaluates
      [System.String]::Compare('', today) = -1 and fails Restore before NuGet audit runs. Reproduced
      in a scratch project with the same props+targets shape: "error : EXPIRY-FIRED: expiry=[] today=2026-10-21".
      The task's own grep for "error NU1903" in that log can never match, so the must_haves truth
      "Removing the suppression makes the same restore fail with error NU1903" is unattainable as written.
    fix_hint: >-
      Keep the expiry property satisfied inside Probe A (pass a future
      -p:AdsAuditSuppressionAutoMapperExpires alongside the temp props) or make the target's Error
      conditional on the property being non-empty, so the only remaining failure is the audit one.
  - plan: "02-05"
    dimension: task_completeness
    severity: BLOCKER
    required_property: >-
      Every step name a verify looks up is the name the task's action mandates.
    description: >-
      Task 1's action requires a step "named exactly 'Restore workloads (image ships .NET 9 MAUI only)'"
      while the task's automated verify looks it up as s['Restore workloads'], where s is keyed by step
      name. Following the action raises KeyError in the verify; following the verify violates the
      action. The job cannot be completed with both instructions intact.
    fix_hint: >-
      Use one name in both places (for example "Restore workloads" with the parenthetical as a comment).
  - plan: "02-06, 02-07"
    dimension: task_completeness
    severity: WARNING
    required_property: >-
      Every checkpoint task declares the instructions, verification and resume-signal elements the
      executor's checkpoint protocol reads.
    description: >-
      verify.plan-structure returns missing <instructions>, <verification> and <resume-signal> for
      02-06 Task 1, 02-06 Task 2 and 02-07 Task 2 — the phase's three human-gated tasks. The prose,
      action and <human-check> are present, but the structural contract is not.
    fix_hint: >-
      Add the three elements to each checkpoint task, mirroring the surrounding action text.
  - plan: "02-01, 02-04, 02-05, 02-06, 02-07"
    dimension: plan_discipline
    severity: WARNING
    required_property: >-
      A plan body never instructs writing a literal that its own negative gate forbids inside the gated path.
    description: >-
      The validator flags action literals matching their own negative gates: RestoreLockedMode (02-01),
      NU1903 and MSB4086 (02-04), pull_request_target (02-05), "this line is not C#" (02-06),
      continue-on-error (02-07). Most are excluded by the gate's own --include/scope; the live hazard is
      02-04, where the gate is grep -rn 'NU1903' src/ and the natural place to explain the deletion is a
      comment in the same csproj.
    fix_hint: >-
      Rephrase by concept in the action, or mark the legitimate occurrence with the planner-discipline
      allow directive and state where the explanatory comment must live.
  - plan: "02-04"
    dimension: verify_command_format_sanity
    severity: WARNING
    required_property: >-
      A source gate measures tracked source, not generated build artefacts.
    description: >-
      Task 1's "! grep -rn 'NU1903' src/" traverses obj/. On the current tree that returns 17 matches:
      the two tracked NoWarn lines plus 15 in seven projects' generated dgspec/project.assets/cache files,
      because a dgspec embeds every project in its graph. The verify runs the grep before its own restore,
      so it fails whenever the last restore predates the NoWarn deletion.
    fix_hint: >-
      Use git grep, or restrict to --include='*.csproj', or restore before the grep.
  - plan: "02-07"
    dimension: task_completeness
    severity: WARNING
    required_property: >-
      Every literal a verify parses is mandated by the task's action or acceptance criteria.
    description: >-
      Task 2's verify requires the ledger line "^Flip commit: <sha>" (sed parse) while the action only
      asks for "the flip commit SHA from Task 1"; Required check: and Proof: are prescribed verbatim,
      this one is not, so a table cell or a different label leaves FLIP empty.
    fix_hint: >-
      State "a line `Flip commit: <sha>`" in the action, as the other two lines already are.
  - plan: "02-04"
    dimension: requirement_coverage
    severity: WARNING
    required_property: >-
      High/Critical advisories are reported for every project's full package graph.
    description: >-
      RESEARCH E24 records the SDK default NuGetAuditMode as all only for TFM >= 10.0 and direct
      otherwise; SourceGeneration targets netstandard2.0, so High/Critical advisories in its transitive
      packages are never reported. No plan sets NuGetAuditMode=all, and 02-04 only forbids downgrading it,
      so SC#3's "any High/Critical vulnerable package" is met for the net10.0 projects only.
    fix_hint: >-
      Set NuGetAuditMode=all in Directory.Build.props and record the measurement that justifies it.
  - plan: null
    dimension: research_resolution
    severity: WARNING
    required_property: >-
      02-RESEARCH.md carries no unresolved open question before planning proceeds.
    description: >-
      "## Open Questions" lists three questions with recommendations but no (RESOLVED) suffix and no
      per-question resolution marker. All three recommendations are honoured by the plans, so only the
      resolution state is missing.
    fix_hint: >-
      Mark the section (RESOLVED) and record each question's resolution inline.
  - plan: "02-01, 02-03"
    dimension: scope_sanity
    severity: WARNING
    required_property: >-
      Each plan's change surface stays reviewable within the per-plan context budget.
    description: >-
      02-01 lists 26 paths and 02-03 about 39 (38 .cs plus .editorconfig), above the 5-8 target and the
      15-path blocker line. Every estimate is inside budget (60k and 50k of 100k) and confidence is low
      because no calibration samples exist; the volume is generated lock files, mechanical attribute
      removals and provably whitespace-only edits.
    fix_hint: >-
      If review load proves real in execution, split the normalisation commit from the .editorconfig
      commit, or the lock-file generation from the version centralisation.
  - plan: "02-01"
    dimension: task_completeness
    severity: INFO
    required_property: >-
      Every intermediate assertion in an action describes the state the task actually creates.
    description: >-
      Task 3 says the pre-staging assertion is "git status --porcelain lists exactly the 11
      packages.lock.json paths as new", but the same task also modifies Directory.Build.props, so the
      listing carries a twelfth entry. The commit contract (12 paths) is correct.
    fix_hint: >-
      Say "the new entries are exactly the 11 lock paths, plus the modified Directory.Build.props".
  - plan: null
    dimension: verification_derivation
    severity: INFO
    required_property: >-
      Phase documentation does not prescribe commands that do not exist in the pinned toolchain.
    description: >-
      ROADMAP's Phase 2 implementation note still says "use dotnet format --verify-changed", which does
      not exist (RESEARCH E23). The plans correctly use --verify-no-changes; the roadmap line is stale.
    fix_hint: >-
      Correct the roadmap note so the phase verifier does not audit the plans against a nonexistent flag.
  - plan: "02-07"
    dimension: verify_command_format_sanity
    severity: INFO
    required_property: >-
      Negative gates coexist with the sibling task that must change the same construct.
    description: >-
      Task 2 bans continue-on-error file-wide while Task 1 removes it from the same file. Benign because
      Task 1 commits first, so the ban holds when Task 2 verifies; recorded so the exemption is visible.
    fix_hint: >-
      No change required; a region-scoped gate would also silence the lint.
  - plan: null
    dimension: nyquist_compliance
    severity: INFO
    required_property: >-
      Automated verification feedback latency stays within the phase's stated budget.
    description: >-
      Every task verify runs a full Release build (~80 s measured) against Nyquist 8b's 30 s guideline.
      02-VALIDATION.md explicitly accepts build-bound latency (~120 s per task), so this is a recorded
      trade-off rather than a defect.
    fix_hint: >-
      No change required for this phase; revisit if a faster scoped build becomes available.
```

---

## 7. Recommendation

Return to the planner for the three blockers. B1 and B3 are edit-only fixes to gates and one step
name; B2 needs the expiry property satisfied inside Probe A (or the target guarded), because that
probe is the only evidence that the audit gate blocks rather than merely exists. The seven warnings
should be addressed in the same revision; the infos need no action. Everything else — requirement
coverage, dependency order, key-link wiring, ordering safety, human gating for SC#2/SC#5, and the
constraint set (no MSIX, no new test project, no UI, `publish.yml`/`codeql` untouched,
`WindowsPackageType=None`, `pull_request` only) — is sound.

---

# Round 2 — pre-execution re-check (revision 1, 2026-10-21)

**Plans checked:** the same seven (`02-01` … `02-07`), 17 tasks, waves 1–7.
**Verdict:** `## ISSUES FOUND` — **0 blocker**, **1 warning**, **5 info**.
Round 1 above is kept verbatim; nothing in the plans was edited and nothing was committed.

Method: every number the revision turned into a gate was re-measured on this tree rather than read
from the revision's claim; each round-1 blocker was re-tested as a *property* (does the gate fail
when the mechanism it guards is absent?) using a throwaway MSBuild harness with the plan's exact
props/targets shape; the new material (crypto pin, `NuGetAuditMode=all`, 02-07 phase gate, ROADMAP
edit) was checked against the tree and the packaged vulnerability data. Deterministic probes were
re-run: `check verify-failure-directions 02 --raw` → `ok`, 17/17 commands carry a stated failure
mode; `check verify-command-paths 02 --raw` → `not_applicable`, 17/17 paths resolve or are created
first; `query verify.plan-structure` → `valid: true` on all seven plans. `python -c "import yaml"`
works (PyYAML 6.0.3), which three verifies require. `estimate-check --calibrated` on all seven:
60k/55k/50k/50k/60k/40k/40k against a 100k budget, `over_budget: false`, `confidence: low`.

## 1. Disputed numbers, re-measured (not taken from the revision)

| Claim under revision | Measurement (read-only, this tree) | Holds |
|---|---|---|
| 74 `PackageReference … Version=` lines in total | `grep -h 'PackageReference[^>]*Version=' src/*/*.csproj \| wc -l` → **74** | yes |
| 5 of them are commented-out, not declarations | `… \| grep -c '<!--'` → **5** (all at `Core.csproj:21-25`) | yes |
| 69 declared references | `… \| grep -v '<!--' \| wc -l` → **69** | yes |
| 53 distinct declared ids (58 only if comments counted) | `sort -u` over declared → **53**; including comments → **58** | yes |
| no id carries two versions (the move is mechanical) | id→version pairs, `sort -u`, duplicate count → **0 conflicts** | yes |
| central end state = 54 rows (53 moved + crypto pin) | 10 csproj declare packages (`AzureResources` none); crypto id absent from the declared set, so 53 + 1 = **54** | yes |
| census gate is satisfiable with the five commented rows preserved | the gate filters `<!--` first, so the intended end state is 0; the prohibited rows stay in place | yes |
| census gate is **not vacuous** | one surviving declared `Version=` yields ≥ 1 → `-eq 0` fails; `central.txt` set-equality against the declared set means a row cannot be silently dropped | yes |
| 11 csproj / 11 lock files / 11 = tracked count | `ls src/*/*.csproj \| wc -l` → **11** | yes |
| SDK `10.0.401`, workload set `10.0.401.1` | `dotnet --version` → `10.0.401`; `dotnet workload --version` → `10.0.401.1` | yes |

Figures carried through the plans (`69`, `53`, `54`) match the measurement; `02-VALIDATION.md` and
`02-PATTERNS.md` no longer contain a `58` (grep count 0 in both). RESEARCH still does — see W1.

## 2. Round-1 blockers, re-tested as properties

**B1 — "the CPM census counts only declared references, and every gate derived from it is
satisfiable on the intended end state" → HOLDS.** The census is comment-filtered (0 in the intended
end state), the central file is asserted at 54 rows and 54 unique ids whose id set equals the
declared id set, and the same corrected measure now appears in `02-01` (`must_haves`, objective,
Task 2 action/verify, `<verification>`), `02-VALIDATION.md` (BUILD-03 row), `02-PATTERNS.md`
(lines 118, 136, 141) and the 02-07 phase gate. No derived gate is unsatisfiable.

**B2 — "each probe isolates the single mechanism it claims to prove, so the asserted diagnostic is
the one the run actually produces" → HOLDS.** Reproduced in a scratch project with the plan's exact
`Directory.Build.props`/`Directory.Build.targets` shape, `AutoMapper` 12.0.1, and the target text
the action mandates:

```
no suppression + -p:AdsAuditSuppressionAutoMapperExpires=2027-01-31
  → exit 1
  error NU1903: Warning As Error: Package 'AutoMapper' 12.0.1 has a known high severity
  vulnerability, https://github.com/advisories/GHSA-rvv3-g6hj-g44x
  (no "expired on" anywhere in the log)                                  = Probe A
suppression item present                                              → exit 0, 0 warnings = Probe B
build -p:AdsAuditSuppressionAutoMapperExpires=2020-01-01
  → exit 1 "AutoMapper audit suppression expired on 2020-01-01 (advisory GHSA-rvv3-g6hj-g44x);
            replace the package before this date. Today is 2026-10-21."  (no MSB4086)  = Probe C
build with no override                                                → exit 0            = Probe C2
```

The gate is not vacuous: Probe A exits 0 (and the verify fails) if the suppression is still in
effect, and the verify rejects an expiry failure inside A, so the audit is the only mechanism under
test. Supplying the future property removes the round-1 interference exactly as intended.

**B3 — "every step name a verify looks up is the name the task's action mandates" → HOLDS.**
`02-05` T1 mandates a step named exactly `Restore workloads` and keys `s['Restore workloads']`;
`02-07` T1 re-parses the same job without looking the step up under another name.

Round-1 warnings: **W1** (checkpoint elements) — `<instructions>`, `<verification>`,
`<resume-signal>` are now present on `02-06` T1, `02-06` T2 and `02-07` T2, alongside `<human-check>`
and `<fails_when>`. **W2** (self-defeating literals) — the plan bodies now carry explicit
`planner-discipline-allow` directives, and every negative gate is scoped to the artifact it governs
(`git grep … -- src`, or a single workflow file), never to `.planning/`. **W3** (generated
artifacts) — `git grep -n 'NU1903' -- src` replaces the `grep -r` that walked `obj/`; measured, the
tracked tree holds exactly those two `NoWarn` lines today, so the gate is meaningful. **W4** — the
02-07 action now mandates a literal `Flip commit: <sha>` line. **W5** — `NuGetAuditMode=all` is
added and is measured-meaningful. **W6** — `## Open Questions (RESOLVED)` with a per-question
marker. **W7** (change surface) — see I5.

## 3. New material introduced by the revision

**`System.Security.Cryptography.Xml` 10.0.13 pin.** Verified end to end. The package resolves
`10.0.10` in exactly two `project.assets.json` files (WASM and Desktop) and nowhere else, matching
the claim. The packaged vulnerability data (`vuln_data_base.dat`, 2026.10.21 feed) shows three high
advisories covering `10.0.10` — `GHSA-7hqp-7c5m-4vq6`, `GHSA-c399-9853-6jmj`, `GHSA-r955-8q5j-3h83`
— each with the range `[10.0.0, 10.0.12]`, so `10.0.13` **is** the first clean release and no
advisory entry mentions it; the version exists on nuget.org (`…/system.security.cryptography.xml`
index lists `10.0.13`). A scratch restore of a direct `10.0.13` reference on `net10.0` and on
`net10.0-windows10.0.19041.0` with `UseMaui`/`WindowsPackageType=None` produced **no**
`NU1510`/`NU1605`/`NU190x`, so the pin does not break the "no `NU` warning" gates in `02-01` T2/T3
or the builds in `02-03`/`02-04`. The two direct references are the two graphs that resolve the
transitive, which is what the plan asserts.

**`NuGetAuditMode=all` decision.** Measured defaults: `Core` (net10.0) → `all`, `Desktop` →
`all`, `SourceGeneration` (netstandard2.0) → `direct`. The property is therefore meaningful exactly
where the plan says it is, and setting it explicitly adds nothing that the tree does not already
audit. A solution-wide restore at `NuGetAuditLevel=high`, `NuGetAuditMode=all`, `NoWarn=` reports
only the AutoMapper advisory (in 7 projects) and the three crypto advisories (WASM, Desktop) — no
other advisory exists at that severity, so "with the crypto pin, AutoMapper is the only high
advisory left" is measured, not asserted, and `all` does not blow up the single-entry rule.

**02-07 phase gate.** Every number and command in it was checked against the tree: census `0` /
`54` rows with `System.Security.Cryptography.Xml` `10.0.13`, `-getProperty:NuGetAuditMode` = `all`,
11 tracked lock files, `dotnet restore src/AzureDesignStudio.Server --locked-mode`, `Passed: 755`,
`dotnet format … --verify-no-changes` exit 0, `git grep -n 'NU1903' -- src` empty. Consistent; no
stale figure survives in it.

**ROADMAP edit.** `git diff` is additive: seven wave entries plus the `--verify-changed` →
`--verify-no-changes` correction with the E23 citation; the success criteria and the constraint
sentence (`WindowsPackageType=None`, no MSIX before Phase 11) are untouched. Waves are conservative
relative to `depends_on` (`02-03` depends on `02-01` yet sits in wave 3) — no forward reference, no
cycle, and 3b stays PASS because every plan owns its own wave.

**Hard constraints re-checked.** No MSIX (prohibited in `02-05`), no new test project (`02-VALIDATION`
keeps the Phase 1 no-new-tooling rule; no plan creates one), `WindowsPackageType=None` untouched in
`Desktop.csproj:41`, triggers remain `push`/`pull_request` on `build.yml` (two `dotnet-version`
lines today; the desktop job uses `global-json-file` only, and `02-02` asserts exactly one remains —
the `codeql` job's, recorded as drift), `publish.yml` and the `codeql` job are never in any
`files_modified`.

## 4. Remaining issues

### W1 — RESEARCH still states the superseded 58 in six places

Measured: `02-RESEARCH.md` line 382 ("58 `<PackageVersion>` rows", and "the 11 csproj" where 10
declare packages), 425 ("58 central `<PackageVersion>` entries"), 459 ("every one of the 58 ids was
already referenced"), 493 and 526 (tree diagrams, "58 versions"), 822 (decision table, "58 versions
in one file"). E15 itself is corrected (69 declared / 53 distinct / 5 commented), and the plans and
the other two documents use 53/54, so RESEARCH now contradicts itself — and the 58 population is
the one that counts five commented rows the plan forbids treating as declarations. No gate can pass
on 58 (the `02-01` verify demands 54), so this cannot ship silently; it is a source-quality defect,
not an execution risk. One warning.

### I1 — 02-07's region-scope lint persists (carried, benign)

The structure probe still flags Task 2's file-wide `! grep 'continue-on-error'` against Task 1,
which removes the same string. Task 1 commits first, so the ban holds when Task 2 verifies; a
region-scoped gate would also silence the lint. No action required.

### I2 — deterministic-probe advisories are context-appropriate (recorded)

The structure probe raises R4 ("fallible `git` in a non-final pipeline stage") on six plans and R6
("bare `git diff` … passes vacuously") on `02-02`. In both cases the construct is the intended
measurement: the guard's `git diff --exit-code -- '*packages.lock.json'` is *about* the working tree
after restore (and the plan documents why `git status --porcelain` is needed beside it for untracked
files, which 02-02 T2 proves with a non-empty output), and `git status --porcelain` inside `$( )` is
paired with commit-shape assertions that would fail if git were broken. No action; recorded so the
exemptions are visible.

### I3 — the five commented-out rows are a prohibition without a gate

`02-01` T2 forbids touching `Core.csproj:21-25`, but no automated assertion counts them (the census
deliberately filters them). The per-file `git diff -U0` review the action mandates is the control.
No action required for the phase goal; noted because a future census edit could quietly delete them.

### I4 — per-task verify latency stays build-bound (carried)

Each task's `<automated>` runs a full Release build (~80 s measured) against Nyquist 8b's 30 s
guideline. `02-VALIDATION.md` accepts ~120 s per task as a recorded trade-off. No action.

### I5 — change surface is above the files/plan target (round-1 W7, downgraded)

`02-01` lists 27 paths, `02-03` 39. The unit of change does not match the count: 11 generated lock
files (never read), 10 mechanical one-line attribute removals, 38 files that the verify proves are
whitespace-only (`git show -w` empty), plus a handful of root config/doc files. Both plans now
record the acceptance with that reasoning, both estimates are inside budget (60k and 50k of 100k),
and round 1's own remedy was conditional on review load observed during execution — which no
pre-execution check can measure. Downgraded to info so the loop is not held on a cost that cannot
be reduced without creating an inconsistent intermediate commit or a second formatter pass.

## 5. Structured issues (round 2)

```yaml
issues:
  - plan: null
    dimension: numeric_claim_authority
    severity: WARNING
    required_property: >-
      Every document the plans cite as a measurement source states the measured population:
      69 declared references across 53 distinct ids plus five commented-out rows that are not
      declarations, and a 54-row central end state.
    description: >-
      02-RESEARCH.md E15 is corrected (69 declared / 53 distinct / 5 commented) and the plans,
      02-VALIDATION.md and 02-PATTERNS.md carry 53/54, but six RESEARCH sites still assert 58:
      line 382 ("58 <PackageVersion> rows"; also "the 11 csproj" where 10 declare packages),
      425 ("58 central <PackageVersion> entries"), 459 ("every one of the 58 ids was already
      referenced by a tracked csproj"), 493 and 526 (tree diagrams, "58 versions"), and 822
      (decision table, "58 versions in one file"). Measured on this tree: 74 matching
      PackageReference lines, 5 commented, 69 declared, 53 distinct declared ids, 0 ids at two
      versions. The 58 figure is the population that counts the five commented rows the plan
      explicitly forbids treating as declarations, so the research document now states two
      contradictory populations and its "58 ids were already referenced" sentence is false.
    fix_hint: >-
      Replace the six 58s with 53 (moved ids) or 54 (rows, with the crypto pin), and correct
      "11 csproj" to "10 csproj that declare packages", or annotate each site as superseded by
      the corrected E15.
  - plan: "02-07"
    dimension: task_completeness
    severity: INFO
    required_property: >-
      A negative gate coexists with the sibling task that must change the same construct.
    description: >-
      Task 2 negative-greps continue-on-error file-wide in build.yml while Task 1 removes it from
      the same file. Benign: Task 1 commits first, so the ban holds when Task 2 verifies. Recorded
      so the exemption stays visible rather than silently suppressed.
    fix_hint: No change required; a region-scoped gate or planner-region-allow would also silence it.
  - plan: "02-01, 02-02"
    dimension: verify_command_format_sanity
    severity: INFO
    required_property: >-
      Every verify command measures the artifact it names, and probe warnings that are
      context-appropriate are recorded rather than suppressed.
    description: >-
      The structure probe raises R4 (fallible git in a non-final stage) on six plans and R6 (bare
      git diff) on 02-02. Each construct is intentional: the lock guard's bare git diff is about the
      working tree after restore, and 02-02 T2 proves the untracked-file hole with a non-empty
      git status output; the commit-shape assertions paired with git status would fail if git were
      broken.
    fix_hint: No change required for this phase.
  - plan: "02-01"
    dimension: task_completeness
    severity: INFO
    required_property: >-
      A prohibition the task depends on is enforced by something the executor can run.
    description: >-
      Task 2 forbids touching the five commented-out rows at Core.csproj:21-25, but no automated
      assertion counts them (the census deliberately filters them). The mandated per-file
      git diff -U0 review is the control, and the must_haves truth states the rows are left untouched.
    fix_hint: Optional; a count assertion on the five commented lines would make the prohibition executable.
  - plan: "02-01, 02-03"
    dimension: scope_sanity
    severity: INFO
    required_property: >-
      Each plan's change surface stays reviewable within the per-plan context budget.
    description: >-
      02-01 lists 27 paths and 02-03 39, above the 10-path warning line, but the unit of change does
      not match the count: 11 generated lock files, 10 one-line attribute removals, 38 files proven
      whitespace-only by git show -w. Both plans record the acceptance, both estimates are inside the
      100k budget (60k and 50k), and round 1's remedy was conditional on review load that cannot be
      measured before execution. Downgraded from warning on that evidence.
    fix_hint: No pre-execution action; revisit during execution if review genuinely stalls.
  - plan: null
    dimension: nyquist_compliance
    severity: INFO
    required_property: >-
      Automated verification feedback latency stays within the phase's stated budget.
    description: >-
      Every task verify runs a full Release build (~80 s measured) against the 30 s guideline;
      02-VALIDATION.md explicitly accepts ~120 s per task. A recorded trade-off, not a defect.
    fix_hint: No change required for this phase.
```

## 6. Recommendation (round 2)

One warning requires revision: the six stale `58` statements in `02-RESEARCH.md`, which contradict
the corrected E15 that `02-01` cites as its read-first authority. It is a six-line textual fix with
no plan redesign. The five infos need no action. The three round-1 blockers are closed as
properties and were re-proved by measurement rather than accepted on the revision's statement; the
new material (crypto pin at a verified-clean `10.0.13`, `NuGetAuditMode=all` where the SDK really
defaults to `direct`, the 02-07 phase gate, and the ROADMAP edit) is consistent with the tree and
with every hard constraint — no MSIX, no new test project, `WindowsPackageType=None`, `pull_request`
only, `publish.yml`/`codeql` untouched.

---

# Round 3 — final confirmation (2026-10-21)

**Scope:** confirm that the round-2 warning (six stale `58` census statements in `02-RESEARCH.md`)
is actually corrected and that nothing regressed. The full dimension sweep from rounds 1–2 was not
re-run; plans, `02-VALIDATION.md`, `02-PATTERNS.md` and `02-RESEARCH.md` were re-read only where
they carry the census, and the two deterministic probes were re-executed.

**Verdict:** VERIFICATION PASSED — 0 blocker(s), 0 warning(s). The five round-2 infos (I1–I5) are
carried unchanged and require no action.

## 1. Round-2 warning W1 — closed

Live, read-only measurement with comment-excluding commands (this session, not carried):

| Measure | Command shape | Result |
|---|---|---|
| `PackageReference` lines | `grep -h 'PackageReference[^>]*Version=' src/*/*.csproj \| wc -l` | **74** |
| Commented rows | same, `grep -c '<!--'` | **5** (`Core.csproj:21-25`) |
| Declared references | same, `grep -v '<!--'` | **69** |
| Distinct declared ids | non-commented `Include=` values, `sort -u` | **53** |
| Distinct ids counting commented rows | all `Include=` values, `sort -u` | **58** |
| csproj declaring packages | `grep -rl 'PackageReference Include' --include='*.csproj'` | **10** of 11 (AzureResources declares none) |
| Central end state | 53 moved ids + `System.Security.Cryptography.Xml` 10.0.13 | **54** rows |

`02-RESEARCH.md` now states exactly these figures at every place round 2 flagged: line 382 (54 rows,
53 moved ids, the 10 csproj that declare packages), 425 (54 central entries = 53 moved + crypto pin),
459 ("all 53 declared ids were already referenced by a tracked csproj" — the false "every one of the
58 ids was already referenced" claim is gone; all 11 csproj are tracked, verified with
`git ls-files 'src/*/*.csproj'`), 493 and 526 (tree diagrams, 54 versions), 822 (decision table, 54
versions). E15 itself (line 53) is unchanged and correct.

The single surviving `58` is at line 53 and is a *measurement*, not a population claim: "58 distinct
ids when the commented rows are counted, which are not declarations". It matches the `sort -u` count
over all 74 lines exactly, and it is explicitly marked as excluding those rows from the declaration
count. Scanning every other phase document for `\b58\b` finds only two unrelated references
(`build.yml` lines 58-66 in `02-02`; `AGENTS.md` line 58 in `02-07`). No stale population claim
remains anywhere.

## 2. Cross-document agreement — holds

- `02-01` T2 gate: comment-filtered `Version=` count `-eq 0`; `declared.txt` `-eq 54`,
  `<PackageVersion` rows `-eq 54`, `central.txt` `-eq 54`, `diff` empty; crypto pin grep present.
  This is consistent with the corrected document: the 53 pre-phase declared ids plus the direct
  crypto reference the same task adds to WASM and Desktop yield a 54-id declared set that equals
  the 54-row central set. `must_haves`, acceptance criteria and the artifacts table say 53 moved
  versions plus the pin (54).
- `02-07` phase gate: "`Directory.Packages.props` holds 54 rows, one of them
  `System.Security.Cryptography.Xml` 10.0.13" — agrees.
- `02-VALIDATION.md` BUILD-03 row: comment-filtered census = 0 and central file "holding 54 rows
  (53 moved ids + the 10.0.13 crypto pin) whose id set equals the declared id set" — agrees.
- `02-PATTERNS.md` (lines 17, 118, 136, 138, 141, 438): 74 lines / 69 declared / 53 distinct / 5
  commented, 53 rows from the declared references plus the crypto exception row — agrees, and was
  not edited in round 3.
- No contradiction was reintroduced: the planner's revision touched only `02-RESEARCH.md` (the seven
  plans, `02-VALIDATION.md`, `02-PATTERNS.md` all predate the round-2 verdict; `ROADMAP.md`'s working
  diff is still the wave listing, unchanged).

## 3. Deterministic probes — still hold

| Probe | Result |
|---|---|
| `gsd-tools check verify-failure-directions 02 --raw` | `status: ok`, `counts: {blocker: 0, warning: 0, total: 17}` — 17/17 runnable commands carry a stated failure mode |
| `gsd-tools check verify-command-paths 02 --raw` | `status: not_applicable`, `counts: {blocker: 0, warning: 0, total: 17}`, `readError: null` — no manifest, so nothing to resolve; unchanged from round 2 |

## 4. Round-3 issues

None. No blocker, no warning, no new info. The round-2 infos stand as recorded in section 4 of
“Round 2 — pre-execution re-check” (I1 region-scope lint, I2 probe advisories, I3 unasserted
commented-row prohibition, I4 per-task verify latency, I5 change surface) and need no pre-execution
action.

## 5. Recommendation (round 3)

Plans verified. Proceed with `/gsd:execute-phase 2`.
