---
phase: 05-identity-guardrails-and-offline-development
plan: 09
subsystem: auth
tags: [approvals, deploy-approver, self-approval, requester, decommission, grpc, xunit]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "AdsCaller.CanApprove / MaySelfApprove, derived roles and the single-user host marker (05-01, 05-02)"
  - phase: 05-identity-guardrails-and-offline-development
    provides: "Audited TenantSettingsModel.AllowSelfApproval via SetTenantSettings (05-07)"
  - phase: 05-identity-guardrails-and-offline-development
    provides: "Scope-keyed approvals with RequestedByUserId and the scope-aware peek that yields the requester (05-06, 05-08)"
provides:
  - "ApprovePlan requires Deploy.Approver (PLAN_APPROVER_ROLE_REQUIRED), checked before any hash, preview or setting lookup"
  - "Self-approval as a separate second rule (PLAN_SELF_APPROVAL_REFUSED) governed by the per-tenant setting; the single-user host always may"
  - "CreateDeployment and CreateStack accept the recorded requester (PLAN_REQUESTER_MISMATCH otherwise); a row with no requester keeps the approver-only rule (PLAN_APPROVER_MISMATCH)"
  - "ApproveDecomWindow requires Deploy.Approver (DECOM_APPROVER_ROLE_REQUIRED) with no self-approval rule"
  - "ApprovalRulesTests: the role, self-approval, requester and decommission matrix, plus an in-process proof for the Desktop host"
affects: [05-10, 05-11]

actuals:
  tokens: 12300
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "One DeployerRefusal helper shared by CreateDeployment and CreateStack so the two deploy paths cannot drift"
    - "Role gate placed immediately after TryGetCaller so a refusal reveals nothing about previews or hashes"
    - "A deploy path is driven past its approval comparison by sending no what-if token: it then stops at the token check, before anything reaches Azure"

key-files:
  created:
    - src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs
  modified:
    - src/AzureDesignStudio.Services/DeployService.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs
    - src/AzureDesignStudio.Server.Tests/InProcessTests.cs

key-decisions:
  - "The role check precedes the hash-format check, so a roleless caller gets the same 403 for a live, an unknown and a malformed hash"
  - "The self-approval comparison uses the requester recorded with the live preview (gate peek), and a preview with no recorded requester cannot be self-approved by anyone"
  - "A recorded requester that is not a usable id matches nobody (SameUser is false) - fail closed"
  - "Decommission approvals get no self-approval rule (D-06); ExecuteDecomWindow is unchanged"

patterns-established:
  - "Matrix theory (caller kind x requester relation x tenant setting) with the setting turned on through the audited SetTenantSettings RPC, so the real path is exercised"

requirements-completed: []

coverage:
  - id: D1
    description: "An organizational caller without Deploy.Approver cannot approve a plan: 403 PLAN_APPROVER_ROLE_REQUIRED, identical whether a preview exists, the hash is unknown or malformed; the mapped and short role claims approve, other casings and Guardrail.Admin do not; personal accounts and the single-user host approve without a role; no identity is 401"
    requirement: "IDA-06"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#ApprovalRulesTests"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#ApprovalRulesHostTests"
        status: pass
    human_judgment: false
  - id: D2
    description: "Self-approval is its own rule with its own code, checked after the role: org approver and personal account who requested the review are refused unless the tenant setting is on (another tenant's setting does not lift it); the single-user host approves its own; the approval row keeps the preview's requester through a replacement"
    requirement: "IDA-06"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#SelfApprovalAndRequesterTests.TheApprovalMatrix"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#SelfApprovalAndRequesterTests.AnotherTenantsSettingDoesNotLiftTheRefusal"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#SelfApprovalAndRequesterTests.ASecondApproverReplacingTheRowKeepsTheRequesterOfTheLivePreview"
        status: pass
    human_judgment: false
  - id: D3
    description: "The requester deploys what someone else approved on both deploy paths; the approver and any third party get 403 PLAN_REQUESTER_MISMATCH without consuming the approval; a row with no requester keeps the approver-only rule (PLAN_APPROVER_MISMATCH)"
    requirement: "IDA-06"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#SelfApprovalAndRequesterTests.TheRequesterDeploysAStackSomeoneElseApprovedAndNobodyElseDoes"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#SelfApprovalAndRequesterTests.TheRequesterGetsPastTheCheckForADeploymentSomeoneElseApprovedAndNobodyElseDoes"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#SelfApprovalAndRequesterTests.AStackApprovalWithNoRequesterKeepsTheOldApproverOnlyRule"
        status: pass
    human_judgment: false
  - id: D4
    description: "Decommission approvals need Deploy.Approver (DECOM_APPROVER_ROLE_REQUIRED, before the hashes are read) and carry no self-approval rule; execute keeps its approver-only ownership check and scope-keyed lookup"
    requirement: "IDA-06"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs#DecommissionApprovalRulesTests"
        status: pass
    human_judgment: false
  - id: D5
    description: "The Desktop in-process host still approves and deploys its own plans through the generated client (single-user marker implies the role and self-approval); the stored row has requester equal to approver and the deploy attempt passes the approval comparison"
    requirement: "IDA-06"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/InProcessTests.cs#TheSingleUserCallerApprovesItsOwnPlanAndGetsPastTheRequesterCheck"
        status: pass
    human_judgment: false

duration: 35min
completed: 2026-11-14
status: complete
plan_head_before: ffce9c805dacec80c2bbcc52dd44e5dba0a71b18
plan_head_after: 996b81c3ff0548eeee149ba3d9cda6ccc7759301
commits: 3
---

# Phase 5 Plan 09: Approver Role, Self-Approval and Requester Rules Summary

**Approving a plan needs Deploy.Approver, self-approval is a separate refusal lifted only by the audited tenant setting (or the single-user Desktop host), the recorded requester deploys someone else's approval, and decommission approvals need the role.**

## Performance

- **Duration:** about 35 min
- **Tasks:** 3 (1 tracer, 2 TDD)
- **Files:** 4 changed (1 created, 3 modified)

## Accomplishments

- Task 1 (tracer): `ApprovePlan` refuses `403 PLAN_APPROVER_ROLE_REQUIRED` straight after `TryGetCaller` when `!caller.CanApprove`, ahead of the hash check, the preview peek and every setting lookup. `ApprovalRulesTests` proves it at service level (live preview, unknown hash and malformed hash all answer the same refusal; mapped role, short `roles` claim, wrong casing, Guardrail.Admin, personal account, single-user host, no identity) and `ApprovalRulesHostTests` proves it over the real host: the test resolves the host's `WhatIfTokenGate` from `factory.Services`, seeds a preview in the Contoso scope, and the role header gives 200 while its absence gives a 403 in the response.
- Task 2 (TDD): `IsSelfApprovalAllowedAsync(scopeId)` reads the scope's `TenantSettingsModel.AllowSelfApproval` (false with no row). After the role check, the hash check and the scope-aware peek, `ApprovePlan` refuses `403 PLAN_SELF_APPROVAL_REFUSED` when the preview's recorded requester is the caller and `!caller.MaySelfApprove(setting)`. `DeployerRefusal` replaces the approver comparison in `CreateDeployment` and `CreateStack`: the recorded requester proceeds, anyone else gets `PLAN_REQUESTER_MISMATCH`, and a row without a requester keeps `PLAN_APPROVER_MISMATCH`. The 13-row matrix (caller kind x requester relation x setting) turns the setting on through the audited `SetTenantSettings` RPC; it includes "role plus requester, setting off" (self-approval code) and "no role plus requester" (role code), which assert different codes.
- Task 3 (TDD): `ApproveDecomWindow` refuses `403 DECOM_APPROVER_ROLE_REQUIRED` straight after `TryGetCaller`, before the hashes are read, with no self-approval rule. `ExecuteDecomWindow` is untouched. `DecommissionApprovalRulesTests` covers the role matrix, an approver executing their own approval (not refused for self-approval), another principal refused with `DECOM_APPROVER_MISMATCH`, and the scope-keyed lookup. `InProcessTests` adds the Desktop proof through `Deploy.DeployClient(invoker)`: what-if, approve (200, stored row has requester equal to approver), then `CreateDeployment` stops at the missing what-if token with a 400 that is neither a mismatch nor `PLAN_NOT_APPROVED`.

## Task Commits

1. **Task 1: approving a plan requires Deploy.Approver** - `1132a91` (feat)
2. **Task 2: self-approval as a separate rule and the requester deploys** - `fcbdfe4` (feat)
3. **Task 3: decommission approvals need Deploy.Approver; the Desktop host still approves its own plans** - `996b81c` (feat)

The tracer gate (Task 1 filter, 65 passed, then re-run in Task 2's wider filter) passed before expanding. The plan prescribes one commit per task, so there is no separate `test(...)` RED commit. RED was observed in the working tree before each GREEN: Task 2 failed 7 of 22 new tests on the planned assertions (e.g. org approver who is the requester got 200 where 403 `PLAN_SELF_APPROVAL_REFUSED` was expected; a third party got 400/200 where `PLAN_REQUESTER_MISMATCH` was expected), and Task 3 failed 4 decommission tests on the planned 403 (got 200, or 400 for the roleless malformed hash). `tdd_mode` is not enabled and the plan type is `execute`, so the `gsd_run check tdd-red-evidence` classifier was not run (it supports no dotnet report format).

## Decisions Made

See `key-decisions` above. Operator consequence for plan 05-10's docs: on a hosted server the one-click approve-then-deploy flow now needs either a second person holding Deploy.Approver (the requester then deploys) or a Guardrail.Admin turning on "allow self-approval" for the tenant; a user holding only Deploy.Approver cannot approve their own what-if by default. Personal accounts and the Desktop host are unaffected except that a personal account who ran the what-if must turn the setting on (it administers its own scope) to approve it.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug in tests] Existing isolation tests assumed the old approval rules**
- **Found during:** Tasks 1 and 2
- **Issue:** `TenantIsolationTests` approved with Guardrail.Admin-only callers (role now required) and with one principal as both requester and approver (self-approval now refused), and deployed a stack with the approver (the requester now deploys).
- **Fix:** callers that approve are `Approver(...)`, previews are requested by a distinct principal, and the requester makes the deploy call (a Fabrikam requester `Rob` was added for the cross-scope stale-token step). No assertion about isolation changed.
- **Files modified:** `src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs` (not named in the plan's `files_modified`)
- **Commits:** 1132a91, fcbdfe4

**2. [Rule 3 - Blocking] `InProcessTests.Host` took a `ScriptedHttp`**
- **Issue:** the in-process proof needs a route-table HTTP double.
- **Fix:** the `http` parameter is now `IHttpClientFactory?` (both doubles implement it); existing callers are unchanged.
- **Commit:** 996b81c

**3. [Note] `PLAN_REQUESTER_MISMATCH` acceptance count**
- The message literal lives once, in the shared `DeployerRefusal` helper; the plan's grep wanted at least 2. Each call site carries a comment naming the code, so `grep -c` prints 3. The behavior is the plan's: both paths are tested.

**Total deviations:** 2 auto-fixed, 1 note. **Impact:** none on behavior.

## Issues Encountered

- The in-process test first failed with "Azure could not be reached with the subscription's credential" because the file-local `AmbientUser` defaults to a `RefusingCredential`; the test passes a `FixedCredential` instead.
- SDK 10.0.403 vs pinned 10.0.401: no `packages.lock.json` drift appeared (`git status --porcelain -- '*packages.lock.json'` is empty after every run).

## Known Stubs

None.

## Threat Flags

None. The plan only tightens existing authorization checks (T-05-27, T-05-28, T-05-29, T-05-31 mitigated; T-05-30's source guard is plan 05-10's).

## Verification

- Task 1 filter (ApprovalRulesTests, StackRpcTests, TenantIsolationTests): 65 passed, 0 failed.
- Task 2 filter (ApprovalRulesTests, SelfApprovalAndRequesterTests, ApprovalRulesHostTests, StackRpcTests, TenantSettingsRpcTests, TenantIsolationTests): 110 passed, 0 failed.
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 487 passed, 0 failed, 11 skipped (the Category=SqlServer facts, skipped by design without `ADS_TEST_SQLSERVER`; unchanged by this plan).
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1820 passed, 0 failed.
- Acceptance greps: `PLAN_APPROVER_ROLE_REQUIRED` 1, `PLAN_SELF_APPROVAL_REFUSED` 1, `PLAN_REQUESTER_MISMATCH` 3, `DECOM_APPROVER_ROLE_REQUIRED` 1 in `DeployService.cs`; no `packages.lock.json` changes; no file deletions in any commit.
- The Windows MAUI Desktop target cannot be compiled on this Mac and was not built. Desktop code is untouched; the in-process host path is exercised by `InProcessTests`.

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs
- FOUND: 1132a91
- FOUND: fcbdfe4
- FOUND: 996b81c
