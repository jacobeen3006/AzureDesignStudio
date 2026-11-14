---
phase: 05-identity-guardrails-and-offline-development
plan: 03
subsystem: auth
tags: [authorization, fallback-policy, grpc, deny-by-default, reflection-test, xunit]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "Typed caller on every Design and Deploy RPC (plans 05-01, 05-02)"
provides:
  - "Authenticated-user FallbackPolicy on the Server host (AddAdsAuthorization)"
  - "MapAdsGrpcService: every gRPC service is mapped protected unless anonymous is stated; Cost is the one exception"
  - "AuthorizationSurfaceTests: reflection test over the real host's endpoints, on the Testing and the Development host"
affects: [05-04, 05-05, 05-06, 05-07, 05-08, 05-09, 05-10]

actuals:
  tokens: 9500
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns:
    - "Anonymous is written at the mapping site (allowAnonymous: true) and as a literal list in the test, never inferred from a missing attribute"
    - "Static assets, Razor pages and the index.html fallback are explicitly AllowAnonymous under the fallback policy"

key-files:
  created:
    - src/AzureDesignStudio.Server/Auth/AdsPolicies.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs
  modified:
    - src/AzureDesignStudio.Server/Program.cs

key-decisions:
  - "D-03 implemented: the only anonymous gRPC method is Cost.GetRetailPrices; the list is a literal in the test file, not a production constant"
  - "D-09 implemented: FallbackPolicy requires an authenticated user; roles stay in the service bodies, no named role policies on endpoints"
  - "The mapping helper gets its own unit test (RetailPriceService has no [Authorize], so the helper's metadata is observable); on the real host the Hosted classes' [Authorize] attributes would otherwise mask a removed RequireAuthorization()"

patterns-established:
  - "Surface test enumerates EndpointDataSource for GrpcMethodMetadata and asserts IAuthorizeData / IAllowAnonymous per endpoint"
  - "Unauthenticated probing of every protected method through a pass-through-marshaller Method<byte[],byte[]> built from the endpoint metadata"

requirements-completed: []

coverage:
  - id: D1
    description: "Exactly one gRPC method is anonymous (Cost.GetRetailPrices); a reflection test over the host's endpoints fails when any other method is anonymous, when the list has no matching endpoint, or when a SharedModels service has no mapped endpoint"
    requirement: "IDA-03"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#EveryGrpcEndpointIsProtectedExceptTheLiteralAnonymousList"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#TheAnonymousEndpointsEqualTheLiteralListExactly"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#EveryServiceOfTheSharedModelsAssemblyHasAMappedEndpoint"
        status: pass
    human_judgment: false
  - id: D2
    description: "The host has an authenticated-user FallbackPolicy; the SPA root, index.html, static assets and Razor pages are explicitly anonymous so the app still boots for an unauthenticated browser"
    requirement: "IDA-03"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#TheHostHasAnAuthenticatedUserFallbackPolicy"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#TheSpaIsNotChallengedForAnAnonymousBrowser"
        status: pass
    human_judgment: false
  - id: D3
    description: "An unauthenticated call to every protected unary and server-streaming method answers Unauthenticated on the Testing host and on a Development host that also maps reflection, whose endpoints require authorization"
    requirement: "IDA-03"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#AnUnauthenticatedCallToEveryProtectedMethodIsRefused"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#TheDevelopmentHostMapsReflectionAndEveryReflectionEndpointRequiresAuthorization"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs#ATokenWithoutTheScopeIsPermissionDeniedOnProtectedMethods"
        status: pass
    human_judgment: false

duration: 20min
completed: 2026-11-14
status: complete
plan_head_before: ce3e31ad45503cb542cafd60a63915b03da1130b
plan_head_after: d7beb8542ec1f800e81581b6e2922522d333f3fa
commits: 2
---

# Phase 5 Plan 03: Deny-by-default gRPC Surface Summary

**The Server host now carries an authenticated-user FallbackPolicy and maps every gRPC service through `MapAdsGrpcService`, so `Cost.GetRetailPrices` is the only anonymous method, and a reflection test over the real host (Testing and Development) pins that.**

## Performance

- **Duration:** about 20 min
- **Tasks:** 2
- **Files modified:** 3 (2 created, 1 modified)

## Accomplishments

- `Auth/AdsPolicies.cs`: `AddAdsAuthorization()` registers the FallbackPolicy (`RequireAuthenticatedUser`); `MapAdsGrpcService<T>(allowAnonymous = false)` maps with gRPC-Web and applies `RequireAuthorization()` or `AllowAnonymous()`.
- `Program.cs`: Design and Deploy map as protected, `RetailPriceService` maps with `allowAnonymous: true`; `MapStaticAssets()`, `MapRazorPages()` and `MapFallbackToFile("index.html")` are `AllowAnonymous()`. Reflection mapping, Hosted attributes and the interceptor are unchanged; no endpoint added.
- `AuthorizationSurfaceTests` (15 test cases): fallback policy present; anonymous Design call Unauthenticated; anonymous Cost call reaches the service (400); `/` and `/index.html` are not 401; every gRPC endpoint protected except the literal `/Cost/GetRetailPrices` on both hosts; anonymous set equals the literal exactly; reflection endpoints carry IAuthorizeData; every SharedModels service (Cost, Deploy, Design) has a mapped endpoint; unauthenticated calls to all protected unary/server-streaming methods (at least 20) are refused on both hosts; a token without the scope is PermissionDenied; the mapping helper alone.

## Task Commits

1. **Task 1: Tracer, fallback policy and explicit mapping** - `2ea4098` (feat)
2. **Task 2: Reflection test of the whole authorization surface** - `d7beb85` (test)

The tracer gate was re-run end to end before expanding (19 passed across the plan filter, including the untouched `ServerHostTests`). The SPA test was mutation-checked: dropping `AllowAnonymous()` from the static assets and file fallback makes both `/` and `/index.html` answer 401 and fail the test.

## Decisions Made

- The anonymous list stays a literal in the test (one occurrence of `/Cost/GetRetailPrices`), so a production widening breaks the test.
- A dedicated helper-level test was added because of the mutation finding below.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical functionality] The plan's mutation check could not fail on the real host alone**
- **Found during:** Task 2 (mutation run)
- **Issue:** The acceptance criterion says removing `RequireAuthorization()` from the mapping helper must make `AuthorizationSurfaceTests` fail. On the real host the `[Authorize]` attributes on `HostedDesignService`/`HostedDeployService` and the fallback policy already protect those endpoints, so the host-level assertions stay green and the helper's contribution is unobserved.
- **Fix:** Added `TheMappingHelperProtectsAServiceUnlessAnonymousIsStated`, which maps the attribute-free `RetailPriceService` through the helper on a throwaway `WebApplication` and asserts IAuthorizeData (default) or IAllowAnonymous (`allowAnonymous: true`). With the helper mutated, exactly this test fails. A second mutation (Design mapped `allowAnonymous: true`) failed 8 tests.
- **Files modified:** src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs
- **Commit:** d7beb85

Both mutations were reverted; `git status` showed only the intended test file before the commit.

Otherwise the plan was executed as written.

## Issues Encountered

- Disposing the shared class-fixture factory through `using var host` failed other tests in the class (ObjectDisposedException, then Internal on an unrelated call). Fixed with a `HostLease` that disposes only a Development host the test created.
- A first mutation attempt (deleting the `RequireAuthorization()` line) left a dangling `else` and did not compile; redone as an empty block.
- No lock-file drift appeared (`git status --porcelain -- '*packages.lock.json'` empty).

## Known Stubs

None.

## Threat Flags

None. T-05-06, T-05-07 and T-05-08 mitigations are implemented as planned; no new endpoint, auth path or schema.

## Verification

- `dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false --filter "FullyQualifiedName~AuthorizationSurfaceTests"`: 15 passed.
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 322 passed, 0 failed, 8 skipped (pre-existing SQL Server skips).
- `grep -c AllowAnonymous Program.cs` = 3; `grep -c SetFallbackPolicy AdsPolicies.cs` = 1.

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Server/Auth/AdsPolicies.cs
- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs
- FOUND commits: 2ea4098, d7beb85
