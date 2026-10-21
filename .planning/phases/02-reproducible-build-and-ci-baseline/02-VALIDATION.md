---
phase: "2"
slug: "reproducible-build-and-ci-baseline"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-10-21"
---

# Phase 2 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

This phase delivers no product feature: every requirement is proven by a command or a CI run, so the
per-task map below is a **command map**, not a test-authoring plan. No new test framework, no new
test project — the Phase 1 no-new-tooling discipline (D-15) carries forward.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit 2.9.2 · `Microsoft.NET.Test.Sdk` 17.12.0 · `xunit.runner.visualstudio` 2.8.2 · `coverlet.collector` 6.0.2 (`src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj:15-21`) |
| **Config file** | none — no `xunit.runner.json`, no `.runsettings` |
| **Quick run command** | `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` (~80 s measured; AOT off drops the Desktop path from 466 s to 74 s) |
| **Full suite command** | `dotnet test src/AzureDesignStudio.Core.Tests -c Release --filter "Category!=LiveAzure"` (755 cases; ~1 s after build) |
| **Phase-specific gates** | locked restore + lock-file diff guard; `dotnet format --verify-no-changes --include …`; NUGET audit probe with `-p:NoWarn=` and a satisfied expiry property; suppression-expiry probe; comment-filtered central-package census |
| **Estimated runtime** | quick ~80 s · full wave ~2 min · phase gate ~12 min (AOT on) |

---

## Sampling Rate

- **After every task commit:** `dotnet build src/AzureDesignStudio.sln -c Release -p:RunAOTCompilation=false` — catches CPM, lock-file and warning regressions immediately.
- **After every plan wave:** the quick build plus `dotnet format src/AzureDesignStudio.sln --verify-no-changes` (measured 30–51 s) and the offline suite (755).
- **Before `/gsd:verify-work`:** full suite green; locked restore green from an empty package cache; audit probe green only through the suppression; expiry probe red; format check exit 0.
- **Max feedback latency:** ~120 s per task (build-bound).

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 02-01-01 | 01 | 1 | BUILD-01 | T-02-01 | SDK pinned, no floating versions | command | `dotnet --version` + `global.json` grep | ✅ exists | ⬜ pending |
| TBD | TBD | TBD | BUILD-02..05 | TBD | TBD | command | TBD — filled by the planner's tasks | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

Every item is a **file/workflow artefact**, not a test:

- [ ] `global.json` — absent today; must pin 10.0.401 with `rollForward: latestPatch`
- [ ] `Directory.Build.props` / `Directory.Packages.props` / `Directory.Build.targets` — none exist; `MauiVersion` must be defined in `Directory.Build.props` because it is empty in the outer evaluation; the central file must also carry the `System.Security.Cryptography.Xml` 10.0.13 pin that WASM and Desktop reference directly, because the 10.0.10 the Bicep/Azure.Deployments graph resolves carries three high advisories and BUILD-04 permits only the AutoMapper allowlist entry
- [ ] `.editorconfig` — absent; must omit `end_of_line` and `charset` (index is LF for all 820 files while the worktree checks out 519 as CRLF and 102 carry a BOM)
- [ ] 11 × `packages.lock.json` — zero exist
- [ ] `.github/workflows/build.yml` Desktop job + locked restore + lock-diff guard — none exist
- [ ] `02-DESKTOP-RUNS.md` — the SC#2/SC#5 evidence ledger does not exist
- [ ] Delete the two per-project `NoWarn NU1903` lines, or the audit gate is bypassed
- [ ] No framework install needed; no new test project

---

## Requirements → Verification Map

| Req ID | Behaviour | Verification type | Command / artefact |
|--------|-----------|-------------------|--------------------|
| BUILD-01 | Clean clone restores and builds with pinned SDK + locked versions | command, local | clone to temp with `NUGET_PACKAGES` pointing at an empty dir, then `dotnet build … -p:RunAOTCompilation=false`; then bump a central version → `dotnet restore --locked-mode` must exit 1 with NU1004; plus `git diff --exit-code -- '*packages.lock.json'` |
| BUILD-02 | Desktop builds on PRs; a deliberate `MauiProgram.cs` error fails the job | CI + human | red run URL recorded in `02-DESKTOP-RUNS.md` |
| BUILD-03 | Centralised versions, warnings-as-errors, format gate | command + CI | comment-filtered census `grep -h 'PackageReference[^>]*Version=' src/*/*.csproj \| grep -v '<!--' \| wc -l` = 0, with `Directory.Packages.props` holding 54 rows (53 moved ids + the 10.0.13 crypto pin) whose id set equals the declared id set; Release build with 0 compiler warnings (baseline: 91 warnings, all `PRI263`, none compiler); `dotnet format --verify-no-changes` exit 0 on the normalised tree |
| BUILD-04 | High/Critical fail; one dated AutoMapper allowlist entry | command, local + CI | `-p:NuGetAuditMode=all` and `-p:NuGetAuditLevel=high`: a temp props without the suppression fails with `NU1903` while a future `AdsAuditSuppressionAutoMapperExpires` is supplied (so the expiry target cannot fire first), and the real props pass with it; the `System.Security.Cryptography.Xml` transitive is pinned to 10.0.13 in WASM and Desktop rather than suppressed; expiry re-pointed to the past fails with the `AdsCheckAuditSuppressionExpiry` error |
| BUILD-05 | Non-blocking Windows job records duration, becomes blocking after two green runs | CI + human | two ledger rows with run IDs + durations; required-check proof via `gh api …/branches/main/protection` |
| Regression | Offline suite unchanged | command | `Passed: 755` |

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Two consecutive green non-blocking Windows runs, then flip to blocking | BUILD-05 | GitHub produces the runs, and `continue-on-error` removal plus the required-check setting are repository state, not local commands | Watch two consecutive PR runs with `continue-on-error: true`; record run id/commit/duration in `02-DESKTOP-RUNS.md`; commit the removal of `continue-on-error`; then set the check as required via repository settings or `gh api` and record the proof |
| Deliberate compile error proves the job fails | BUILD-02 | Requires a throwaway branch pushed to the remote and closed unmerged; a local build cannot prove the *job* | Push `MauiProgram.cs` with a deliberate error on `ci-proof/desktop-compile-error`, observe the red Desktop job, record the URL, close the PR unmerged |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
