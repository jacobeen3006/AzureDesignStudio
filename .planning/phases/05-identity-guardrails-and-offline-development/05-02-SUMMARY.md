---
phase: 05-identity-guardrails-and-offline-development
plan: 02
subsystem: auth
tags: [claims, oid, tid, grpc, identity, in-band-401, xunit]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "AdsIdentity/AdsCaller, ServiceTools.TryGetCaller, header-driven TestAuthHandler (plan 05-01)"
provides:
  - "All 20 Design and Deploy RPCs resolve the typed caller first and answer an in-band 401"
  - "The legacy object-id accessor is removed; AdsIdentity is the only derivation of the user id"
  - "IdentityRpcTests table over all 20 RPCs with a completeness fact against the host's endpoints"
  - "LegacyClaimTests and the client no-retry contract tests"
affects: [05-03, 05-04, 05-05, 05-06, 05-07, 05-08, 05-09, 05-10]

actuals:
  tokens: 12600
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "First statement of every RPC is ServiceTools.TryGetCaller; failure is status 401 in the response (plus IDENTITY_INVALID text where the response has error_message)"
    - "Stored approver/author ids written as caller.UserIdText and compared with AdsIdentity.SameUser"
    - "RPC table + EndpointDataSource completeness fact so a new RPC cannot skip the identity rule"

key-files:
  created:
    - src/AzureDesignStudio.Server.Tests/Hosting/LegacyClaimTests.cs
  modified:
    - src/AzureDesignStudio.Services/DesignService.cs
    - src/AzureDesignStudio.Services/DeployService.cs
    - src/AzureDesignStudio.Services/ServiceTools.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs
    - src/AzureDesignStudio.Server.Tests/DesignServiceTests.cs
    - src/AzureDesignStudio.Server.Tests/ServerPlumbingTests.cs
    - src/AzureDesignStudio.Server.Tests/InProcessTests.cs
    - src/AzureDesignStudio.Server.Tests/ClientWrapperTests.cs

key-decisions:
  - "ServiceTools.IdentityInvalidMessage holds the one IDENTITY_INVALID text; Design responses and three Deploy responses (SaveSubInfo, LoadSubInfo, GetRgs) have no error_message field, so they carry the status only"
  - "StackPlan carries the AdsCaller instead of the claim text; PrepareStackAsync resolves the caller first so StackWhatIf and CreateStack share the 401 path"
  - "CreateStack's Design/Deploy 401 reaches the client through the existing (status, error) tuple of PrepareStackAsync"

patterns-established:
  - "Caller moved ahead of request validation in WhatIf, CreateDeployment, GetStack and PrepareStackAsync so an unauthenticated caller learns no validation rules"

requirements-completed: []

coverage:
  - id: D1
    description: "Every one of the 20 Design and Deploy RPCs answers status 401 inside the response (no RpcException) for a token with no tid and for a non-GUID short oid; CreateDeployment streams it as its first message"
    requirement: "IDA-01"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs#EveryRpcAnswers401InTheResponseForATokenWithoutTid"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs#TheTableCoversEveryGrpcMethodOfTheHost"
        status: pass
    human_judgment: false
  - id: D2
    description: "A Desktop-style client that sees the 401 queues nothing and does not retry, against a fake server (one server call) and over the real host (zero offline saves)"
    requirement: "IDA-01"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/ClientWrapperTests.cs#ADesktopClientDoesNotQueueOrRetryA401InAResponse"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs#ADesktopWrapperOverARealHostQueuesNothingFor401"
        status: pass
    human_judgment: false
  - id: D3
    description: "A token with only the legacy URI object-id claim saves, lists, loads, deletes and links as before; either spelling addresses the same rows and the same 10-design quota"
    requirement: "IDA-02"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/LegacyClaimTests.cs"
        status: pass
    human_judgment: false

duration: 35min
completed: 2026-11-14
status: complete
plan_head_before: c811580494f4789738e4a80e0d1bb752d109e375
plan_head_after: 1ddb91318d51e32bb26068b13e89cc22d3812fcf
commits: 3
---

# Phase 5 Plan 02: Typed Caller on Every Design and Deploy RPC Summary

**All 20 Design and Deploy RPCs now resolve the AdsIdentity caller as their first statement and answer an identity failure as an in-band 401; the old user-id accessor is deleted, and a table over the real host plus a legacy-claim and a no-retry client contract prove it.**

## Performance

- **Duration:** about 35 min
- **Tasks:** 3
- **Files modified:** 10 (1 created, 9 modified)

## Accomplishments

- Save, Load and Delete (Design) and the 15 Deploy sites convert to `ServiceTools.TryGetCaller`; `caller.UserId` replaces the string-to-Guid construction, `caller.UserIdText` is what approvals and the guardrail row store, `caller.Principal` is the display text. WhatIf, CreateDeployment, GetStack and the shared stack preparation moved the caller ahead of request validation.
- The three approver comparisons keep their codes (PLAN_APPROVER_MISMATCH, DECOM_APPROVER_MISMATCH) and compare through `AdsIdentity.SameUser`.
- Both `GetUserId` overloads and their private claim constant are gone; the two grep gates print 0.
- `IdentityRpcTests`: 20-RPC table run for a missing tid and a non-GUID short oid (no RpcException), plus a fact that the table equals the host's gRPC method set minus `Cost.GetRetailPrices`.
- `LegacyClaimTests`: URI-only token CRUD and subscription link, and one user presenting both spellings sees the same ten designs and gets the same 507 on the eleventh.
- Client contract: a Desktop `DesignGrpcService` receiving 401 returns 401, leaves the offline store empty and calls the server once (fake), and records zero offline saves over the real host.

## Task Commits

1. **Task 1: Tracer, Design service resolves the typed caller** - `2360c31` (feat)
2. **Task 2: Every Deploy RPC resolves the caller; legacy accessor removed; 20 RPCs proved** - `75bbf0d` (feat)
3. **Task 3: Legacy claim and no-retry contract** - `1ddb913` (test)

The tracer gate (Task 1) was re-run end to end with the plan filter before expanding (29 passed). The Task 3 tests passed on first run because the behavior they pin was built in Tasks 1 and 2 (characterization of the compatibility promises, not new behavior).

## Decisions Made

- One shared `ServiceTools.IdentityInvalidMessage` constant for the IDENTITY_INVALID text.
- Responses without an `error_message` field (all four Design responses, SaveSubInfo, LoadSubInfo, GetRgs) carry the 401 status only; adding a proto field was ruled out by D-12.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] TestAuthHandler doc comment named the removed accessor**
- **Found during:** Task 2 (grep gate)
- **Issue:** `Hosting/TestAuthHandler.cs` had a `<c>ServiceTools.GetUserId</c>` doc mention, so the "GetUserId" gate could not print 0; the file is not in the plan's `files_modified`.
- **Fix:** reworded the comment.
- **Files modified:** src/AzureDesignStudio.Server.Tests/Hosting/TestAuthHandler.cs
- **Commit:** 75bbf0d

**2. [Scope note] ServiceToolsTests partially deleted, not the whole class**
- The plan says delete `ServiceToolsTests`; the class also holds the design-name and subscription-name validation facts, which have nothing to do with the accessor. Only the four legacy object-id facts were removed (covered by `AdsIdentityTests`: either spelling, non-GUID, empty, no claim); the validation facts stay.

Otherwise the plan was executed as written.

## Issues Encountered

None. No lock-file drift appeared (`git status` clean of `packages.lock.json`).

## Known Stubs

None.

## Threat Flags

None. No new endpoint, auth path or schema; T-05-02 and T-05-38 mitigations are implemented as planned.

## Verification

- `dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false`: 307 passed, 0 failed, 8 skipped (pre-existing SQL Server skips).
- `grep -rn "GetUserId" src --include='*.cs'` (excluding obj/bin) = 0; `grep -rn "new Guid(userIdClaim)"` = 0.
- Desktop (MAUI) project was not built on this machine; it has no reference to the removed accessor (grep over src is 0).

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/LegacyClaimTests.cs
- FOUND commits: 2360c31, 75bbf0d, 1ddb913
