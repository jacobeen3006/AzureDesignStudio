---
phase: 05-identity-guardrails-and-offline-development
plan: 01
subsystem: auth
tags: [claims, oid, tid, grpc, identity, tenant-scope, xunit]

requires:
  - phase: 03-server-test-host
    provides: AdsServerFactory and the header-driven TestAuthHandler
provides:
  - AdsIdentity (pure claim-to-identity derivation, Services project)
  - AdsCaller (identity plus single-user host marker and the guardrail/approval rules)
  - ServiceTools.TryGetCaller and SingleUserHostKey
  - Design.GetSaved resolving its caller first with a typed in-band 401
  - Test helpers that state tenants, roles, missing claims and the host marker in one line
affects: [05-02, 05-03, 05-04, 05-05, 05-06, 05-07, 05-08, 05-09, 05-10]

actuals:
  tokens: 10000
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns:
    - "Pure identity type in Services; hosts declare themselves through UserState, never claims"
    - "Identity failure answers in-band status 401, never RpcException"

key-files:
  created:
    - src/AzureDesignStudio.Services/Auth/AdsIdentity.cs
    - src/AzureDesignStudio.Services/Auth/AdsCaller.cs
    - src/AzureDesignStudio.Server.Tests/AdsIdentityTests.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs
  modified:
    - src/AzureDesignStudio.Services/ServiceTools.cs
    - src/AzureDesignStudio.Services/DesignService.cs
    - src/AzureDesignStudio.Services/InProcess/InProcessCallInvoker.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/GrpcTestClients.cs
    - src/AzureDesignStudio.Server.Tests/TestSupport.cs
    - src/AzureDesignStudio.Server.Tests/InProcessTests.cs

key-decisions:
  - "AdsIdentity lives in AzureDesignStudio.Services (no ASP.NET Core type) so the Desktop in-process host can run it (D-11)"
  - "Scope key is tid for organizational tokens and the user's own oid for the shared personal-account tenant (D-17)"
  - "A repeated claim of one type must agree with itself, in addition to the two-spelling agreement rule (stricter than the plan's text)"
  - "The single-user marker is set only in InProcessServerCallContext and counts only as boolean true (D-04)"

patterns-established:
  - "TestClaims record on CreateGrpcChannel states a caller's tenant, roles, missing claims and extra claims"
  - "TestServerCallContext.ForUser/ForPersonalAccount/ForSingleUserHost/WithoutTenant factories"

requirements-completed: []

coverage:
  - id: D1
    description: "Over the real host, GetSaved answers 200 for a token with GUID oid and tid and in-band 401 (no RpcException) for a missing, non-GUID or empty oid or tid"
    requirement: "IDA-01"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs"
        status: pass
    human_judgment: false
  - id: D2
    description: "Either spelling of oid and tid derives the same caller; disagreeing spellings and subject-only tokens are refused; scope key follows D-17"
    requirement: "IDA-02"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/AdsIdentityTests.cs"
        status: pass
    human_judgment: false
  - id: D3
    description: "The single-user host is told apart from a server call by UserState alone; no claim or string value can declare it"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/InProcessTests.cs#InProcessCallsCarryTheSingleUserMarker"
        status: pass
    human_judgment: false

duration: 25min
completed: 2026-11-14
status: complete
plan_head_before: 7eb062a5adbf3e227ab063cd5d364ba674682a38
plan_head_after: 49b47783c584c930ef8e27cc3f7bb1afa9f20981
commits: 2
---

# Phase 5 Plan 01: Typed Caller Identity Summary

**One pure AdsIdentity/AdsCaller pair derives user, tenant, scope key and roles from the validated principal (oid/tid in either spelling, never sub), proved over the real host through Design.GetSaved with an in-band typed 401, plus the UserState single-user host marker and the test helpers the rest of the phase writes its callers with.**

## Performance

- **Duration:** about 25 min
- **Tasks:** 2
- **Files modified:** 11 (4 created, 7 modified)

## Accomplishments

- `AdsIdentity.TryFrom` accepts the legacy URI claims and the short `oid`/`tid`, parses every value as a non-empty GUID, refuses disagreeing spellings, never reads the subject claim, and returns log-safe failure text with no claim value. Scope is the tenant, or the user's own oid for the shared personal-account tenant.
- `AdsCaller` carries the host marker and the rules `CanAdministerGuardrails`, `CanApprove`, `MaySelfApprove`, and a `Principal` text capped at 320 characters.
- `ServiceTools.TryGetCaller` and `SingleUserHostKey`; `DesignService.GetSaved` converted as the tracer (the other 19 sites and the legacy object-id accessor are untouched for plan 05-02).
- `InProcessServerCallContext` sets the marker; the server never references it.
- Test helpers: header-driven `TestAuthHandler` (tenant, no-tid, no-oid, role, arbitrary claim), `TestClaims`, `TestTenants`, and `TestServerCallContext` factories.

## Task Commits

1. **Task 1: Tracer, typed caller through DesignService.GetSaved over the real host** - `2f77785` (feat)
2. **Task 2: Identity derivation tables and the in-process single-user marker** - `49b4778` (feat)

The tracer feedback gate (Task 1) was verified end to end with the plan's filter before expanding (34 passed). In Task 2 the in-process marker test was observed RED before the marker was set, then GREEN; tests and implementation were committed together as the plan specifies.

## Decisions Made

- A claim type that appears more than once (for example two `oid` claims) must agree with itself; this extends the plan's two-spelling rule and closes a multi-valued-claim gap.
- Test default tenant is `TestTenants.Contoso` for every helper, so existing Design tests and host tests pass unchanged.

## Deviations from Plan

None - plan executed exactly as written (the repeated-claim agreement check above is a small tightening inside the specified function, not a scope change).

## Issues Encountered

- xUnit1025 duplicate InlineData in the first draft of the subject-claim theory (`ClaimTypes.NameIdentifier` equals the xmlsoap URI); replaced the duplicate with the short `nameid` spelling.

## Known Stubs

None.

## Threat Flags

None. No new network endpoint, auth path or schema; the plan implements T-05-01, T-05-03, T-05-04 and T-05-05 mitigations. A server-side source guard that `SingleUserHostKey` is never referenced under the Server project is deferred to plan 05-10 as the threat register says (currently 0 references, checked by grep).

## Verification

- `dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false`: 263 passed, 0 failed, 8 skipped (pre-existing skips).
- `grep -c Microsoft.AspNetCore AdsIdentity.cs` = 0; `SingleUserHostKey` count in InProcessCallInvoker.cs = 2; in Server = 0.
- No `packages.lock.json` drift (`git status` clean of lock files).

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Services/Auth/AdsIdentity.cs, AdsCaller.cs, Server.Tests/AdsIdentityTests.cs, Hosting/IdentityRpcTests.cs
- FOUND commits: 2f77785, 49b4778
