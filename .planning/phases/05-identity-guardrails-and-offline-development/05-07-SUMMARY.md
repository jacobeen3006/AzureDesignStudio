---
phase: 05-identity-guardrails-and-offline-development
plan: 07
subsystem: auth
tags: [tenant-settings, self-approval, guardrails, policy-mode, audit, grpc, client-wrapper, xunit]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "AdsCaller.CanAdministerGuardrails and the typed caller (05-01, 05-02); TenantSettingsModel, SecurityEventModel, GuardrailPolicyModes, SecurityEventTypes and scope-keyed guardrail storage (05-04); the Guardrail.Admin gate and the GuardrailPolicyChanged audit pattern (05-06)"
provides:
  - "GetTenantSettings and SetTenantSettings on the Deploy service (additive proto): allow-self-approval (default off) and guardrail policy mode (replace default, merge), Guardrail.Admin / personal account / single-user host only, 403 TENANT_SETTINGS_ADMIN_REQUIRED otherwise"
  - "One SelfApprovalChanged / PolicyModeChanged event per changed setting, in the same SaveChanges as the settings row; nothing changed writes nothing; a failed audit insert stores nothing"
  - "GuardrailPolicy.Merge (pure ordered case-insensitive union, inherited rules first) and mode-aware DeployService.LoadGuardrailPolicy: replace (default, no row) = own-else-default-else-empty, merge = default + own, unrecognised stored mode = merge, unreadable body on either side fails closed"
  - "DeployGrpcService.GetTenantSettingsAsync / SetTenantSettingsAsync: thin non-UI wrappers (null on a transport break, every server status passed through) so an administrator can reach both settings before the Phase 9 settings UI"
affects: [05-08, 05-09, 05-10, 05-11]

actuals:
  tokens: 14500
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Settings Set replaces both values (read, edit, write back); the diff against the stored (or default) values decides which events are written, so a no-op Set is truly a no-op"
    - "Mode resolution happens in the one LoadGuardrailPolicy, so WhatIf, CreateDeployment, the stack preparation and ExecuteDecomWindow all honor the mode without a caller change"
    - "Fail-safe unknown: an unrecognised stored mode is evaluated as the stricter merge, while an unrecognised mode on write is a 400"

key-files:
  created:
    - src/AzureDesignStudio.Server.Tests/Hosting/TenantSettingsRpcTests.cs
    - src/AzureDesignStudio.Core.Tests/GuardrailPolicyMergeTests.cs
  modified:
    - src/AzureDesignStudio.SharedModels/deploy_model.proto
    - src/AzureDesignStudio.Services/DeployService.cs
    - src/AzureDesignStudio.Discovery/Unmanaged/GuardrailPolicy.cs
    - src/AzureDesignStudio/Services/DeployGrpcService.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs
    - src/AzureDesignStudio.Server.Tests/InProcessTests.cs
    - src/AzureDesignStudio.Server.Tests/ClientWrapperTests.cs

key-decisions:
  - "INTERPRETATION TO CONFIRM (carried from the plan, D-05): the user's 'Option 1 and option 2 are available to choose' is read as both modes selectable per tenant by an administrator, audited, replace by default. Implemented exactly so; the markers in the plan are untouched and plan 05-10 documents it for correction"
  - "GetGuardrailPolicy is left on the replace-semantics FindEffectiveGuardrailRow (the editor shows and edits the scope's own row, else the default); only enforcement (LoadGuardrailPolicy) honors the mode, so the editor never writes a merged union back as the tenant's own policy"
  - "A merge-mode scope with a blank own body counts that side as empty (default only); in replace mode a blank own body is still the empty policy, not the default (05-04 behaviour kept)"
  - "Event Detail is the plain 'old -> new' text (bool as True/False, mode as replace/merge); never a policy body"

patterns-established:
  - "Tests that need a scope's mode seed TenantSettingsModel directly (SeedMode) and assert through LoadGuardrailPolicy, the single choke point all enforcement paths share"

requirements-completed: []

coverage:
  - id: D1
    description: "A Guardrail.Admin, a personal account or the single-user host reads (defaults when no row) and changes both settings; the mode is normalized, an unknown mode is 400 TENANT_SETTINGS_INVALID and stores nothing; everyone else gets 403 TENANT_SETTINGS_ADMIN_REQUIRED, no identity 401; one scope's settings are invisible to another; over the real host the role gives 200 and a role-less token gets 403 in the response"
    requirement: "IDA-06"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantSettingsRpcTests.cs#TenantSettingsRpcTests"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantSettingsRpcTests.cs#TenantSettingsHostTests"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs#TheTableListsTwentyTwoRpcs"
        status: pass
    human_judgment: false
  - id: D2
    description: "Each changed setting appends exactly one SelfApprovalChanged or PolicyModeChanged event (Detail 'old -> new', principal and user id from the caller, TenantId = scope); both changed = two, nothing changed, refused, invalid or Get = none; a failing audit insert stores no settings row"
    requirement: "IDA-06"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantSettingsRpcTests.cs#WhenTheAuditInsertFailsTheSettingRowIsNotStored"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantSettingsRpcTests.cs#ChangingBothWritesTwoEventsAndChangingNothingWritesNone"
        status: pass
    human_judgment: false
  - id: D3
    description: "GuardrailPolicy.Merge is the case-insensitive ordered union (inherited first, no duplicates, key-only and keyed tags distinct), pure, identity on the empty policy, and protects exactly what either side protects"
    requirement: "IDA-05"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Core.Tests/GuardrailPolicyMergeTests.cs#GuardrailPolicyMergeTests"
        status: pass
    human_judgment: false
  - id: D4
    description: "LoadGuardrailPolicy honors the scope's mode: no row or replace = own-else-default-else-empty; merge = default + own with a missing side empty; an unrecognised stored mode is merge; an unreadable body on either side fails closed for that scope only; setting the mode through the RPC (service level and through the in-process generated client as the single-user caller) governs the next evaluation"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#MergeModeJudgesTheUnionOfTheDefaultAndTheOwnPolicy"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#AnUnrecognisedStoredModeIsJudgedAsMerge"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/InProcessTests.cs#TheModeSetThroughTheGeneratedClientGovernsTheNextEvaluation"
        status: pass
    human_judgment: false
  - id: D5
    description: "The client wrappers send an empty Get and a two-value Set, return the server's answer untouched (403 text, 400, 401) and answer null, never throw, on RpcException, InvalidOperationException and HttpRequestException; no .razor file references either method"
    requirement: "IDA-06"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/ClientWrapperTests.cs#DeployGrpcServiceTests"
        status: pass
    human_judgment: false
  - id: D6
    description: "Whether 'replace by default, merge selectable per tenant' is what the user meant by 'Option 1 and option 2 are available to choose'"
    requirement: "IDA-05"
    verification: []
    human_judgment: true
    rationale: "The user's wording was ambiguous; the plan recorded the reading for confirmation. It is cheap to correct (one default constant and the mode semantics in LoadGuardrailPolicy)."

duration: 35min
completed: 2026-11-14
status: complete
plan_head_before: ef00318d47ae86f19630c4c6448ec4bc8794f4bf
plan_head_after: fa11daf094861d7fb767a2e19367373f950d5554
commits: 3
---

# Phase 5 Plan 07: Audited Tenant Settings and Guardrail Policy Mode Summary

**A Guardrail.Admin can now read and change two per-tenant settings, allow-self-approval (off by default) and guardrail policy mode (replace by default, merge selectable), every change is one audit event in the same commit as the row, and guardrail evaluation honors the mode through a pure `GuardrailPolicy.Merge`.**

## Performance

- **Duration:** about 35 min
- **Tasks:** 3 (1 tracer, 2 TDD)
- **Files:** 10 (2 created, 8 modified); no package or lock file changed

## Accomplishments

- Task 1 (tracer): `GetTenantSettings` / `SetTenantSettings` added to the proto (additive, fields 1-6 as planned) and implemented in `DeployService`: `TryGetCaller` (401), the `CanAdministerGuardrails` gate (403 `TENANT_SETTINGS_ADMIN_REQUIRED`), mode normalization through `GuardrailPolicyModes.TryNormalize` (400 `TENANT_SETTINGS_INVALID`), a diff against the stored or default values, and one `SaveChangesAsync` carrying the row and one event per changed setting. A concurrent first write by the same scope retries once after `ChangeTracker.Clear`, like the guardrail upsert. Nothing is added to `HostedServices.cs` or `MauiProgram.cs`: `HostedDeployService` inherits the methods. The `IdentityRpcTests` table grew to 22 RPCs and its completeness fact passes.
- Task 2 (TDD, RED then GREEN): `GuardrailPolicy.Merge` plus the mode-aware `LoadGuardrailPolicy` (replace keeps the 05-04 resolution; merge unions the default row and the own row; unrecognised mode = merge). Eight pure Merge tests, nine mode facts in `TenantIsolationTests` (including one that sets the mode through the RPC and then loads) and two `InProcessTests` facts through the generated client as the single-user caller.
- Task 3 (TDD, RED then GREEN): the wrapper pair on `DeployGrpcService`, using the same `IsCallFailure` filter as `ApprovePlanAsync`. `FakeDeployClient` got scripted overrides for the two generated calls; tests cover the empty Get request, the two-value Set request, the 403 / 400 / 401 pass-through and null for all three transport-break exception types.

## Task Commits

1. **Task 1: audited per-tenant settings for self-approval and guardrail policy mode** - `29a64a8` (feat)
2. **Task 2: guardrail policy merge mode and mode-aware policy loading** - `9624cd5` (feat)
3. **Task 3: thin client wrappers for the tenant settings RPCs** - `fa11daf` (feat)

Tracer gate: the Task 1 verify (94 passed, 0 failed) was re-run before expanding. TDD order: tests were written and run RED first in all three tasks (29 failing in Task 1, a compile failure for the missing `Merge` plus 6 failing in Task 2, a compile failure for the missing wrappers in Task 3), then GREEN. The plan prescribes one commit per task, so RED and GREEN share each commit.

## Verification

- Core.Tests `GuardrailPolicyMergeTests|GuardrailEnforcementTests`: 23 passed.
- Server.Tests filters of all three tasks: pass (94, 73 and 96 tests).
- Full Server.Tests in Release (`-p:RunAOTCompilation=false`): 432 passed, 9 skipped (pre-existing skips), 0 failed.
- Acceptance greps: `TenantSettings` in the proto 4 (>= 4); `public static GuardrailPolicy Merge` 1; `SetTenantSettingsAsync` in `DeployGrpcService.cs` 2 (>= 1); `.razor` files referencing `TenantSettingsAsync` 0; `git status --porcelain -- '*packages.lock.json'` empty after every task.

## Deviations from Plan

None - plan executed exactly as written. The principal text in tests is `Name <oid>` (the 05-06 format), which I first assumed was the bare name; a test expectation fix only, not a code change.

## Issues Encountered

- A full offline `Core.Tests` run (not part of this plan's verify) showed one failure, `WorkspaceConnectionTests.PooledConnectionsCarryThePragmas_AndAColdWorkspaceOpensReadOnly`; it passes when run alone. It exercises the SQLite workspace connection pool, which this plan does not touch, so it is treated as a parallel-load flake and left alone (out of scope).

## Known Stubs

None.

## Threat Flags

None. The two new RPCs are exactly the surface in the plan's threat model (T-05-15, T-05-16, T-05-17); no request field names a tenant, and no event holds a policy body.

## User Setup Required

None.

## Next Phase Readiness

Plan 05-09 can read `AdsTenantSettings.AllowSelfApproval` for the approval rules, and plan 05-10 can document the administrator call recipe (the wrapper pair or a direct gRPC call) and the interpretation of the mode wording for confirmation.

## Self-Check: PASSED

- Created files present: `TenantSettingsRpcTests.cs`, `GuardrailPolicyMergeTests.cs`.
- Commits `29a64a8`, `9624cd5`, `fa11daf` are ancestors of HEAD.
