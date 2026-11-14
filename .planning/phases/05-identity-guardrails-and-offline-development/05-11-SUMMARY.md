---
phase: 05-identity-guardrails-and-offline-development
plan: 11
subsystem: verification
tags: [phase-gate, sqlite, sql-server, mac-catalyst, entra-claims, human-verify]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "Plans 05-01 to 05-10: typed caller, deny-by-default surface, tenant storage, role gates, scoped approvals, operator documents"
provides:
  - "Phase gate evidence: both xUnit suites, the SQL Server container run, the Node tests and the Mac Catalyst build on the finished tree"
  - "Three human verdicts (token claims, macOS pre-tenant upgrade, operator documents and policy-mode reading), each recorded as approved by the user"
  - "The guardrail override interpretation (replace default + merge, selectable per tenant, audited, set by Guardrail.Admin) recorded as CONFIRMED"
  - "An open gap: Core.Tests workspace tests share the process-wide SQLite pool and are flaky"
affects: [verify-work, phase-06]

actuals:
  tokens: 0
  tasks: 4
  commits: 0
plan_head_before: a8b429bd302659eb71b2ce1a01185898943a3d07
plan_head_after: a8b429bd302659eb71b2ce1a01185898943a3d07

tech-stack:
  added: []
  patterns: []

key-files:
  created: []
  modified: []

key-decisions:
  - "The replace/merge reading of 'Option 1 and option 2 are available to choose' is CONFIRMED by the user's approval of Task 4: both modes selectable per tenant, replace by default, set by a Guardrail.Admin, audited. No gap-closure plan is needed for it."

requirements-completed: [IDA-01, IDA-02, IDA-03, IDA-04, IDA-05, IDA-06, CRED-01, CRED-02]

coverage:
  - id: D1
    description: "Automated phase gate on the finished tree: Core.Tests offline, Server.Tests Release, SQL Server container run, Node tests, Mac Catalyst build, no lock drift"
    verification:
      - kind: other
        ref: "dotnet test Core.Tests (1828 passed, first run); dotnet test Server.Tests -c Release -p:RunAOTCompilation=false (490 passed, 11 skipped SqlServer); bash src/RunSqlServerTests.sh (11 passed); node --test (17/17); dotnet build Desktop -f net10.0-maccatalyst (0 errors)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Real personal and work account token claims confirm research assumption A1"
    verification: []
    human_judgment: true
    rationale: "Real tokens exist only on the user's machine; approved by the user in one combined answer, claim shapes not itemised"
  - id: D3
    description: "The macOS app upgrades a pre-tenant deploy.db and the Guardrails drawer saves a tenant-scoped policy with an audit event"
    verification: []
    human_judgment: true
    rationale: "Needs a GUI session and an identity; approved by the user, app output not reported"
  - id: D4
    description: "Operator documents are followable and the policy-mode interpretation is confirmed"
    verification: []
    human_judgment: true
    rationale: "Reading comprehension of the documents and the meaning of the user's own earlier answer can only come from the user"

duration: not measured
completed: 2026-11-14
status: complete
---

# Phase 5 Plan 11: Phase Gate and Human Verification Summary

**The automated gate is green on the finished phase (Core.Tests 1828, Server.Tests 490 plus 11 on a real SQL Server container, Node 17, Mac Catalyst build), the user approved the token-claims, macOS-upgrade and operator-document checks in one combined answer and thereby confirmed the replace-and-merge guardrail reading; the GitHub `sqlserver` job is pending a push and the Core.Tests workspace suite has an open flakiness gap.**

## Performance

- **Tasks:** 4 (1 automated, 3 human-verify checkpoints)
- **Files changed:** none (the plan holds no implementation); no package or lock file changed

## Task 1: Phase gate (automated, PASSED)

| Command | Result |
|---|---|
| `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` | 1828 passed, 0 failed on the first run (but see the flakiness gap below) |
| `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` | 490 passed, 0 failed, 11 skipped (all `Category=SqlServer`, they need the container) |
| `bash src/RunSqlServerTests.sh` | 11 passed, 0 failed, 0 skipped (Docker 29.8.2; container removed afterwards). This is the first real container proof of the `TenantGuardrails` and `TenantApprovals` migrations on SQL Server. |
| `node --test src/AzureDesignStudio/JsTests/*.test.cjs` | 17 passed, 0 failed |
| `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst` | 0 errors, 1 pre-existing PublishFolderType warning |
| `git status --porcelain -- '*packages.lock.json'` | empty (no lock drift) |
| GitHub `sqlserver` job | pending push - not run on GitHub (the branch has no upstream; tip `a8b429b`) |

A scratch `deploy.db` at the Baseline migration only (one legacy `AdsGuardrailPolicies` row with Id 1, one `AdsPlanApprovals` row, no `TenantId` column) was prepared in a `mktemp` folder outside any real application data folder for Task 3.

## Task 2: Real token claims (assumption A1) - human-verified

**Approved by the user** in a single combined answer ("All three approved"). No claim shapes were reported back to the orchestrator, so none are recorded here: the verdict is "approved by the user, shapes not itemised". It is not recorded which account types were tried, nor whether a hosted server with a real `Server.Access` registration was available. That residual risk (a `Server.Access` token for a real app registration was not separately evidenced) is therefore not closed by this record.

## Task 3: macOS upgrade of a scratch pre-tenant deploy.db - human-verified

**Approved by the user** in the same combined answer. No query output, backup listing, drawer text or audit-event contents were reported, so none are recorded. For transparency: when the scratch folder was cleaned up it still held only the unmodified Baseline-only `deploy.db` and no `backups/deploy` folder, and no app process was running. That means the folder prepared by Task 1 shows no trace of an app launch; the approval is recorded as given, but the migration result itself is not independently evidenced here. The offline proof remains the automated tests plus the SQL Server container run above.

## Task 4: Operator documents and the policy-mode interpretation - human-verified

**Approved by the user** in the same combined answer. The documents were not reported as needing fixes. The interpretation to confirm is recorded as **CONFIRMED**: a tenant's guardrail policy relates to the inherited default in BOTH modes, selectable per tenant by a `Guardrail.Admin`, audited - replace (default) and merge. The two consequences stated in `docs/identity-and-guardrails.md` were shown with it and approved: approvals pending at upgrade must be re-approved, and on a hosted server with default settings approve-then-deploy stops at approval until a second `Deploy.Approver` approves or an administrator allows self-approval. The GitHub `sqlserver` job status was not reported by the user: **pending push - not run on GitHub**; the commit to check after pushing is the branch tip (`a8b429b` at the time of the gate, plus the docs commit for this summary).

## Open gap: Core.Tests workspace tests are flaky (deferred item)

Across 5 runs of one build, runs 1 and 3 were green; runs 2, 4 and 5 each failed exactly one DIFFERENT Phase 4 workspace test, all sharing the process-wide `Microsoft.Data.Sqlite` connection pool that `ClearPools` clears:

| Run | Failing test | Error |
|---|---|---|
| 2 | `WorkspaceBackupTests.TheBackupSourceStemIsHonoured_AndListIgnoresOtherStems` | SQLite Error 5, user-function active statements, via `ClearPools` |
| 4 | `AdsWorkspaceOpenTests.TheQueryOnlyRung_CanReadButNeverWrite` | `ObjectDisposedException` on `SQLitePCL.sqlite3` |
| 5 | `WorkspaceRestoreTests.ACandidateWhoseManifestRowCountDiffers_IsRejected` | database is locked |

Phase 5 changed nothing under `src/AzureDesignStudio.Workspace`; the cause is test parallelism over the shared pool. Suggested fix (a separate follow-up, not done here per the plan's no-implementation rule): put every workspace test that calls `ClearPools` into one xUnit collection (`[Collection(...)]` with `DisableParallelization`) so they serialize, or stop calling `ClearPools` in them (dispose the connections they open instead). Until then a red Core.Tests run on a workspace test should be re-run before it is blamed on a change.

## Deviations from Plan

None - plan executed exactly as written. The plan's only branch (a failing gate) did not occur; the flakiness is a pre-existing Phase 4 test-isolation defect, logged above rather than fixed.

## Cleanup

The scratch folder and nothing else was removed (`/var/folders/yf/v86zmqxn5b7470sb8c4qm5hh0000gn/T//ads-verify-deploy.tbAYzX`). Its `.app.log` did not exist and no `AzureDesignStudio` process was running. `~/Library` and real user data were not touched.

## Known Stubs

None. This plan created no source.

## Threat Flags

None. T-05-35 (no launch against real data, scratch folder outside Application Support), T-05-36 (no token or claim value recorded) and T-05-37 (assumption and interpretation resolved by a human, not shipped as fact) are implemented as planned.

## Follow-ups before the phase is called fully verified

- Push the branch and confirm the GitHub `sqlserver` job is green (pending push).
- Fix the Core.Tests workspace flakiness (gap above).
- Optionally re-run the macOS pre-tenant upgrade and record the query output, since none was reported.

## Self-Check: PASSED

- FOUND: this file at `.planning/phases/05-identity-guardrails-and-offline-development/05-11-SUMMARY.md`
- No task commits exist for this plan (no code change); `commits: 0` is measured and legitimate (HEAD unchanged from `plan_head_before`, working tree clean before this file).
