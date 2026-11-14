---
phase: 05-identity-guardrails-and-offline-development
plan: 06
subsystem: auth
tags: [guardrails, authorization, app-roles, audit, what-if, token-gate, tenant-isolation, xunit]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "AdsCaller.CanAdministerGuardrails, the typed caller on every Deploy RPC (05-01, 05-02); SecurityEventModel/AdsSecurityEvents and scope-keyed guardrail storage (05-04)"
provides:
  - "GetGuardrailPolicy and SetGuardrailPolicy answer 403 GUARDRAIL_ADMIN_REQUIRED in-band unless the caller holds Guardrail.Admin, is a personal account (own scope) or is the single-user host"
  - "One GuardrailPolicyChanged AdsSecurityEvents row per Set, in the same SaveChanges as the policy row; Detail = byte length + SHA-256 prefix, never the body"
  - "WhatIfTokenGate entries bound to RequestedByUserId and ScopeId; scope-aware TryPeekPlan (yields the requester) and scope-aware ValidateAndConsume/ValidateAndConsumeForStack"
  - "GuardrailAuthorizationTests, WhatIfTokenGateScopeTests, audit and gate-scope facts in TenantIsolationTests"
affects: [05-07, 05-08, 05-09, 05-10, 05-11]

actuals:
  tokens: 8300
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Authorization refusal is an in-band status + coded ErrorMessage (403 GUARDRAIL_ADMIN_REQUIRED), never an RpcException, so the client does not read it as offline"
    - "Gate scope binding answers a foreign scope with the unknown-token Stale result before any kind/payload/expiry check, so existence is not revealed and the token is not burned"
    - "Newest-preview ordering by a gate-local monotonic sequence, not the clock, so previews issued in the same tick still order deterministically"

key-files:
  created:
    - src/AzureDesignStudio.Server.Tests/Hosting/GuardrailAuthorizationTests.cs
    - src/AzureDesignStudio.Core.Tests/WhatIfTokenGateScopeTests.cs
  modified:
    - src/AzureDesignStudio.Services/DeployService.cs
    - src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/ServerHostTests.cs
    - src/AzureDesignStudio.Server.Tests/DeployServiceTests.cs
    - src/AzureDesignStudio.Server.Tests/StackRpcTests.cs

key-decisions:
  - "A token issued without a scope is Stale for any scoped consume and invisible to the scope-aware peek (fail closed); the scope-less overloads and callers are unchanged"
  - "The audit event reuses row.UpdatedAtUtc as AtUtc and is added inside UpsertGuardrailRowAsync, so the concurrent-first-write retry (ChangeTracker.Clear, upsert again) re-adds exactly one event"
  - "ApprovePlan discards the requester returned by the scope-aware peek (out _) for now; plan 05-08 stores it on the approval row and plan 05-09 applies the approver rules"

patterns-established:
  - "Tests that seed the gate directly must issue with the scope of the caller they then call as (TestTenants.Contoso for TestServerCallContext.ForUser/ForSingleUserHost defaults)"

requirements-completed: []

coverage:
  - id: D1
    description: "A caller without the Guardrail.Admin role gets 403 from Get and SetGuardrailPolicy; with the mapped or short roles claim 200; other casings and other roles 403; a personal account and the single-user host need no role; no identity is still 401; a refused Set stores nothing; over the real host the role header gives 200 and a role-less token gets 403 in the response with no RpcException"
    requirement: "IDA-04"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/GuardrailAuthorizationTests.cs#GuardrailAuthorizationTests"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/GuardrailAuthorizationTests.cs#GuardrailAuthorizationHostTests"
        status: pass
    human_judgment: false
  - id: D2
    description: "Every Set appends exactly one GuardrailPolicyChanged event for the writer's scope with principal and user id; Get and refused/malformed Sets append none; Detail holds a length and hash prefix and never the policy; a failing audit insert stores no policy row (one commit)"
    requirement: "IDA-04"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#EachSetAppendsOneEventForTheWritersScopeWithThePrincipal"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#TheEventDetailHoldsALengthAndAHashPrefixNeverThePolicy"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#WhenTheAuditInsertFailsThePolicyRowIsNotStored"
        status: pass
    human_judgment: false
  - id: D3
    description: "A what-if preview, its plan hash and its token are bound to the scope and requester that created them: another scope's peek finds nothing, its consume is Stale without burning the token, its ApprovePlan answers 400 WHAT_IF_STALE; the owner's approval works; a deployment preview is preferred over a stack preview, then the newest wins"
    requirement: "IDA-05"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Core.Tests/WhatIfTokenGateScopeTests.cs#WhatIfTokenGateScopeTests"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#AnotherScopesApprovalOfAPreviewIsStaleAndTheOwnersSucceeds"
        status: pass
    human_judgment: false

duration: 35min
completed: 2026-11-14
status: complete
plan_head_before: 66230329a2c8d0c9932c0570261b881cf8fa5984
plan_head_after: 5c4b9546db4cec4daca0191769ccec654c80f661
commits: 3
---

# Phase 5 Plan 06: Guardrail Role Gate, Audit and Scope-Bound What-If Gate Summary

**Reading or changing a tenant's guardrail policy now needs Guardrail.Admin (or a personal account's own scope, or the single-user host), every change is recorded atomically as a GuardrailPolicyChanged event holding only a length and hash prefix, and a what-if preview, plan hash and token belong to the scope and requester that created them.**

## Performance

- **Duration:** about 35 min
- **Tasks:** 3 (1 tracer, 2 TDD)
- **Files:** 8 (2 created, 6 modified); no package or lock file changed

## Accomplishments

- Task 1 (tracer): both guardrail RPCs refuse with 403 `GUARDRAIL_ADMIN_REQUIRED` (names the role and the "sign out and back in" hint, since app roles are not retroactive) right after `TryGetCaller`. `GuardrailAuthorizationTests` covers the whole matrix at service level and over the real host. Existing guardrail tests were moved onto an administering caller (`Admin` role in `DeployServiceTests`, `ForSingleUserHost` for `StackRpcTests.Me`, the role header in the two `ServerHostTests` calls that only prove a service answers).
- Task 2: `UpsertGuardrailRowAsync` adds the `SecurityEventModel` (`TenantId = ScopeId`, caller principal and user id, `bytes=N; sha256=<16 hex>`) before the single `SaveChangesAsync`. A `SaveChangesInterceptor` test that throws on a security event proves the policy row is not stored when the audit insert fails.
- Task 3: `WhatIfTokenGate` stores requester and scope per entry; `Issue` (both overloads) and `IssueForStack` take them as optional trailing parameters; a scope-aware `TryPeekPlan` and an optional `scopeId` on both consume methods. `DeployService` passes the caller's scope at `WhatIf`, `StackWhatIf` (via `prepared.Caller`), `ApprovePlan`, `CreateDeployment` and `CreateStack`.

## Task Commits

1. **Task 1: guardrail policy read and change need Guardrail.Admin, a personal account or the single-user host** - `08a4f59` (feat)
2. **Task 2: audit every guardrail policy change in the same commit** - `65f80ca` (feat)
3. **Task 3: bind what-if previews and tokens to scope and requester** - `5c4b954` (feat)

Tracer gate: the Task 1 verify (85 passed, 0 failed) was re-run before expanding. TDD order: the Task 1 and Task 3 tests were written and run RED first (7 failures; compile errors for the missing gate parameters). Task 2's implementation was written before its tests, so no separate RED run exists for it; the interceptor test and event assertions fail without the event by construction. The plan prescribes one commit per task, so RED and GREEN share each commit.

## Decisions Made

See `key-decisions` above.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] StackRpcTests deployment-token fact had to issue with the caller's scope**
- **Found during:** Task 3
- **Issue:** `CreateStack_WithDeploymentWhatIfToken_IsBlocked` issued a deployment token through the scope-less overload and expected `WHAT_IF_PAYLOAD_CHANGED`. With the scoped consume (by design, fail closed) an unscoped token is Stale, so the assertion saw `WHAT_IF_STALE`.
- **Fix:** the test passes `scopeId: TestTenants.Contoso` (the caller's scope), keeping its meaning (a deployment token is refused by the stack validate).
- **Files modified:** `src/AzureDesignStudio.Server.Tests/StackRpcTests.cs`
- **Commit:** `5c4b954`

Otherwise none: plan executed as written. `StackRpcTests` is in the plan's `files_modified` list.

## Verification

- Task 1 filter: 85 passed, 0 failed. Task 2 filter: 51 passed. Task 3 Core filter (gate scope, StackTokenGate, WhatIf, RevertPlan, GuardrailEnforcement): 117 passed; Task 3 Server filter: 50 passed.
- Full `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 385 passed, 0 failed, 9 skipped (the Category=SqlServer facts, skipped by design without `ADS_TEST_SQLSERVER`/Docker; the SQL Server container proof was not re-run, no schema changed in this plan).
- Full `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1812 passed, 0 failed.
- Acceptance: `GUARDRAIL_ADMIN_REQUIRED` in `DeployService.cs` >= 1; `GuardrailPolicyChanged` in `DeployService.cs` >= 1; `ScopeId` in `WhatIfTokenGate.cs` >= 4; `git status --porcelain -- '*packages.lock.json'` empty throughout (no SDK drift appeared).

## Issues Encountered

None.

## Known Stubs

None.

## Threat Flags

None. T-05-09 (role gate), T-05-13 (atomic audit, no body) and T-05-25 (scope-bound peek and consume) are mitigated as planned.

## Next Phase Readiness

Ready for plan 05-07 onward. Plan 05-08 should consume the requester the scope-aware peek returns (currently discarded in `ApprovePlan`); plan 05-09 owns the approver role and self-approval rules.

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/GuardrailAuthorizationTests.cs
- FOUND: src/AzureDesignStudio.Core.Tests/WhatIfTokenGateScopeTests.cs
- FOUND commits: 08a4f59, 65f80ca, 5c4b954
