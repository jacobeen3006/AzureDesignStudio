---
phase: 05-identity-guardrails-and-offline-development
plan: 10
subsystem: docs
tags: [service-worker, entra, app-roles, operator-docs, source-guards, grpcurl]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "Typed caller, deny-by-default surface, tenant storage, development crypto, role gates, tenant settings RPCs, scoped approvals and approver rules (05-01 to 05-09)"
provides:
  - "appsettings*.json excluded from the WASM service worker precache, pinned by a Node test"
  - "docs/entra-setup.md section 6: appRoles manifest, role assignment, not-retroactive rule, caller kinds"
  - "docs/identity-and-guardrails.md: operator guide with every response code, the tenant settings call recipe (grpcurl and C#), the policy-mode interpretation to confirm, upgrade consequences, development crypto"
  - "docs/database-operations.md addendum for the TenantGuardrails and TenantApprovals migrations"
  - "IdentityRunbookTests drift guard and three new SourceGuardTests facts (marker, claim mapping, single oid/tid reader)"
  - "AGENTS.md identity conventions, pitfalls and the pointer to the operator guide"
affects: [05-11]

actuals:
  tokens: 11500
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Node vm test runs the real worker's onInstall against manifest-style urls with stubbed caches"
    - "Operator document guarded by a file-reading xUnit test (required statements, cited paths exist, no credential or JWT-shaped text, token only via environment variable)"
    - "Source guards scan text for the single-user marker sites, MapInboundClaims and the one claim-interpretation file"

key-files:
  created:
    - src/AzureDesignStudio/JsTests/service-worker-precache.test.cjs
    - docs/identity-and-guardrails.md
    - src/AzureDesignStudio.Core.Tests/IdentityRunbookTests.cs
  modified:
    - src/AzureDesignStudio/wwwroot/service-worker.published.js
    - docs/entra-setup.md
    - docs/database-operations.md
    - AGENTS.md
    - src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs

key-decisions:
  - "The precache exclusion is /^appsettings(\\..+)?\\.json$/ only; the test proves lookalike names stay cached"
  - "The call recipe says plainly that grpcurl's header expansion is visible in the process list on a shared machine and points to the C# example there; the token is only ever an environment variable"
  - "SourceGuardTests.SourceFiles gained an optional pattern list so the marker guard can also scan .razor files of non-test projects"

patterns-established:
  - "INTERPRETATION TO CONFIRM heading appears exactly once in the operator guide (the test counts it)"

requirements-completed: []

coverage:
  - id: D1
    description: "The service worker no longer precaches appsettings*.json while other json, html, dll and css assets stay cached, the worker script stays excluded, lookalike names are not over-excluded, and wwwroot/appsettings.json is still a published file"
    requirement: "IDA-06"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio/JsTests/service-worker-precache.test.cjs"
        status: pass
    human_judgment: false
  - id: D2
    description: "entra-setup.md defines Guardrail.Admin and Deploy.Approver in an appRoles fragment with assignment steps, the not-retroactive rule, personal-account and Desktop behavior, and the link to the operator guide"
    requirement: "IDA-04"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Core.Tests/IdentityRunbookTests.cs#EntraGuide_DefinesTheAppRoles"
        status: pass
    human_judgment: false
  - id: D3
    description: "The operator guide holds every required statement, one INTERPRETATION TO CONFIRM notice, the settings call recipe with an environment-variable token, only existing cited paths, and no credential or JWT-shaped text; AGENTS.md points to it and states the conventions"
    requirement: "CRED-01"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Core.Tests/IdentityRunbookTests.cs"
        status: pass
    human_judgment: true
    rationale: "Whether the prose is accurate and the recipe works against a real Entra-protected server (grpcurl flags, az token acquisition with an authorized client) was checked against the proto and code but not executed against a live server."
  - id: D4
    description: "Source guards fail when a Server file references the single-user marker, when the Server mentions MapInboundClaims, or when any file other than AdsIdentity reads oid or tid"
    requirement: "IDA-04"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs"
        status: pass
    human_judgment: false

duration: 40min
completed: 2026-11-14
status: complete
plan_head_before: 08b94e35966ceec7eae0a25705d0126caa0a88d9
plan_head_after: 4f227a881097228de483b276eee415edfc883cc0
commits: 3
---

# Phase 5 Plan 10: Precache Exclusion, Operator Documents and Source Guards Summary

**The browser no longer caches `appsettings*.json` offline, an operator now has the appRoles setup, a full identity and guardrails guide (response codes, a grpcurl and C# call recipe for the two tenant settings, the policy-mode interpretation to confirm, upgrade consequences, development crypto), and source guards pin the marker, the claim mapping and the single oid/tid reader.**

## Performance

- **Duration:** about 40 min
- **Tasks:** 3 (1 tracer, 2 auto)
- **Files:** 8 (3 created, 5 modified); no package or lock file changed

## Accomplishments

- Task 1 (tracer): `offlineAssetsExclude` gained `/^appsettings(\..+)?\.json$/`. The Node test loads the real worker in a `vm`, feeds a manifest-style url list and runs the worker's own `onInstall` against a stubbed `caches`/`Request`: the three appsettings names are not cached, `service-worker.js` still is excluded, `ads-stencils.json`, `index.html`, `_framework/blazor.boot.json`, a `.dll` and a `.css` still are cached, lookalikes (`myappsettings.json`, `settings.json`) are not over-excluded, and `wwwroot/appsettings.json` still exists. Mutation: removing the new pattern failed the "not precached" test (1 of 17); reverted.
- Task 2: `docs/entra-setup.md` section 6 (appRoles fragment with placeholder GUIDs, assignment steps, group caveats, roles not retroactive, personal account and Desktop behavior). `docs/identity-and-guardrails.md` new: identification (oid/tid, both spellings, scope key, shared personal tenant), caller-kind table, every response code with remedy, tenant settings and audit events, the `Changing the tenant settings (administrators)` recipe (read then write both, grpcurl for both calls against `src/AzureDesignStudio.SharedModels/deploy_model.proto`, C# over `Deploy.DeployClient` and the `DeployGrpcService` wrapper pair, token only through `ADS_TOKEN`, reading the answer, the Desktop host), the `INTERPRETATION TO CONFIRM` section with the three places to change it, upgrade consequences, the anonymous surface, the browser cache note, and the development crypto provider. `docs/database-operations.md` names `TenantGuardrails` and `TenantApprovals`, the empty-GUID backfill, unchanged rollback, and the same two migrations on `deploy.db`. `IdentityRunbookTests` guards the guide (7 facts at this commit). Mutation: appending a JWT-shaped string failed `Guide_ContainsNoCredentialAndNoJwtShapedText`; reverted.
- Task 3: three `SourceGuardTests` facts (marker only in `ServiceTools.cs` and `InProcessCallInvoker.cs` across non-test projects, including `.razor`; no `MapInboundClaims` in the Server; only `Auth/AdsIdentity.cs` holds the claim URIs, the claim-name constants or `FindFirst("oid"|"tid")` in Services and Server). A temporary Server file naming the marker, `MapInboundClaims` and `FindFirst("oid")` made exactly those three facts fail; removed. AGENTS.md: identity WHERE TO LOOK row, updated Auth, Caller identity and Azure credentials conventions, the dev-profile note on the dev-server command, and NOTES bullets (tenant settings without a screen, personal-account scope, roles not retroactive, `TenantApprovals` backfill, hosted self-approval consequence, migration pair and container proof, development key ring, service worker exclusion). A further `AgentsMd_StatesTheIdentityConventions` fact keeps those statements in place.

## Task Commits

1. **Task 1: exclude appsettings from the service-worker precache** - `06d9d59` (feat)
2. **Task 2: appRoles, operator document with the settings call recipe, and runbook addendum** - `45a2bee` (docs)
3. **Task 3: AGENTS.md identity conventions and source guards** - `4f227a8` (docs)

The tracer gate (Task 1) was re-run end to end with `node --test src/AzureDesignStudio/JsTests/*.test.cjs` (17 passed across 2 files) before the documents were written. Task 3 is marked `tdd="true"`, but the guards assert properties the earlier plans already satisfy, so they passed on first run; their bite was shown by mutation instead of a RED run, and the plan prescribes one commit per task.

## Decisions Made

See `key-decisions` above. The guide states the facts the operator needs from earlier plans: macOS and Linux development keys are plaintext XML in a user-only folder and switching providers makes linked subscriptions unreadable until relinked; approvals pending at upgrade are backfilled to the empty-GUID scope and must be re-approved; on a hosted server one-click approve-then-deploy needs a second `Deploy.Approver` or a `Guardrail.Admin` enabling self-approval, and a personal account that ran the What-If enables that setting for itself before approving; the Desktop host is unaffected.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug in my own work] Mutation revert discarded the uncommitted worker change**
- **Found during:** Task 1 mutation run
- **Issue:** reverting the mutation with `git checkout -- service-worker.published.js` restored HEAD, which did not yet contain the (uncommitted) exclusion.
- **Fix:** re-applied the one-line change and re-ran the Node tests (17 passed) before committing. The committed diff is the single intended line.
- **Commit:** 06d9d59

### Corrected during writing (documentation accuracy)

- `docs/identity-and-guardrails.md` first said an unknown or other-tenant plan hash "fails as unknown or stale"; the code answers 400 `WHAT_IF_STALE` for approval, so the guide names that code.
- The grpcurl example cannot hide the token from the process list (the shell expands `$ADS_TOKEN` before exec); instead of claiming otherwise, the guide says so and recommends the C# example on shared machines.

**Total deviations:** 1 self-inflicted and repaired, 2 accuracy corrections before commit. **Impact:** none on committed behavior.

## Issues Encountered

- SDK 10.0.403 vs pinned 10.0.401: no `packages.lock.json` drift appeared in any run (`git status --porcelain -- '*packages.lock.json'` empty at every commit).
- The recipe was verified against `deploy_model.proto` (`Deploy` has no package; fields `allow_self_approval`, `guardrail_policy_mode`; response `status_code`, `error_message`, `updated_by`, `updated_at`) and the service code, not against a live Entra-protected server.

## Known Stubs

None. The documents state that no tenant settings screen exists before Phase 9; that is a documented absence, not a stub.

## Threat Flags

None. No endpoint, auth path or schema was added; T-05-32 (precache exclusion plus test), T-05-33 and T-05-39 (placeholders only, token via environment variable, scanned by `IdentityRunbookTests`) and T-05-34 (three source guards) are implemented as planned.

## Verification

- `node --test src/AzureDesignStudio/JsTests/*.test.cjs`: 17 passed, 0 failed (2 test files).
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "FullyQualifiedName~IdentityRunbookTests|FullyQualifiedName~DatabaseRunbookTests"`: 15 passed, 0 failed.
- `dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false --filter "FullyQualifiedName~SourceGuardTests"`: 9 passed, 0 failed.
- Acceptance greps: `Guardrail.Admin` in entra-setup 4, `Deploy.Approver` >= 1, `INTERPRETATION TO CONFIRM` in the guide 1, `TenantApprovals` in database-operations 3, `Deploy/SetTenantSettings` and `grpcurl` >= 1, `identity-and-guardrails` in AGENTS.md >= 1, `Guardrail.Admin` in AGENTS.md >= 1, JWT-shaped text 0, credential patterns 0.
- Not run: the full Server.Tests and Core.Tests suites (this plan changes documents, one JS asset and text-reading tests only).

## Open item for the owner

The `replace`/`merge` reading of "Option 1 and option 2 are available to choose" is an interpretation to confirm; the guide lists exactly where to change it if the reading is wrong.

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio/JsTests/service-worker-precache.test.cjs, docs/identity-and-guardrails.md, src/AzureDesignStudio.Core.Tests/IdentityRunbookTests.cs
- FOUND commits: 06d9d59, 45a2bee, 4f227a8
