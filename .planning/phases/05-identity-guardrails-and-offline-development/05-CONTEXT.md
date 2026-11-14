# Phase 5: Identity, Guardrails, and Offline Development - Context

**Gathered:** 2026-11-14
**Status:** Ready for planning

<domain>
## Phase Boundary

Every request carries a trustworthy user and tenant, guardrail policy and approvals can only be changed by the right role inside the right tenant, and a developer can work offline in DEBUG without weakening production. Requirements IDA-01..IDA-06, CRED-01, CRED-02. Depends on Phase 4 (EF migrations exist for the server SqlServerDesignDbContext in the Server project and the SQLite deploy.db SqliteDesignDbContext in Services; any tenant column is a new migration on both).

</domain>

<decisions>
## Implementation Decisions

### Personal accounts and guardrail administration
- A personal Microsoft account (MSA) is the administrator of its own single-user tenant scope and can manage its own guardrail policy without an app role; organizational tenants still require the `Guardrail.Admin` app role - **Reversibility:** costly - defines who may change guardrails and shapes the tenant-scoping model that stored rows depend on

### Approval and self-approval
- Approval requires `Deploy.Approver`, and self-approval is refused as a separate rule; a `Guardrail.Admin` can switch on a per-tenant "allow self-approval" setting (default off) and every change to that setting is audited - **Reversibility:** costly - adds a persisted tenant setting and an audit event type

### Anonymous RPC list
- Exactly one gRPC method may be called without authentication: `Cost.GetRetailPrices` (public Azure retail prices, as today). Every other method requires authentication; a reflection-based test enumerates all methods and fails on any other unauthenticated one - **Reversibility:** one-way - the anonymous list becomes a public contract that clients rely on

### Locked by the roadmap implementation note
- New server `Auth/AdsIdentity.TryFrom`: accepts the mapped `oid` claim and the legacy `.../objectidentifier` URI claim through `Guid.TryParse`, validates `tid` the same way, never uses `sub`, never flips `MapInboundClaims`, and answers with an in-band typed 401 rather than throwing `RpcException` (no deferred-save retry loop starts) - **Reversibility:** costly - identity derivation determines which stored rows a user sees
- `AdsPolicies` fallback plus `Guardrail.Admin` and `Deploy.Approver` app roles; a tenant column on the guardrail and approval rows added through Phase 4 migrations, with the legacy `Id=1`/NULL-tenant row retained as the inherited default policy - **Reversibility:** one-way - persisted schema change on user data, applied by migration
- `ICryptoService` with a DPAPI/Data-Protection DEBUG implementation selected by `Crypto:Provider` (Phase 3 left a one-value `KeyVault` seam); Production refuses development keys and still requires Key Vault; exclude `appsettings*.json` from the service-worker precache so role/scope changes are not sticky

### Decided autonomously
- [Identity] Q: "Where do identity and policy classes live" -> Selected: "`src/AzureDesignStudio.Server/Auth/` for AdsIdentity and the policy registration, with pure parsing kept free of ASP.NET types so it is unit-testable" - file layout is a local refactor
- [Identity] Q: "How is the 401 shaped in-band" -> Selected: "reuse the existing in-band response/status convention of the services (typed status in the response message), no new proto field unless the existing responses cannot carry it" - internal choice, proto change would be additive
- [Crypto] Q: "DEBUG crypto implementation key storage" -> Selected: "ASP.NET Core Data Protection with a file-system key ring under the dev user profile, selected only when `Crypto:Provider` is the development value and the environment is not Production" - reversible, behind the Phase 3 seam
- [Tenancy] Q: "How a request's tenant reaches the services" -> Selected: "through the same `UserState` caller mechanism (`ServiceTools.UserStateKey`) extended with the tenant id" - internal plumbing
- [Testing] Q: "How are tenant isolation and role checks tested" -> Selected: "Server.Tests with the Phase 3 `AdsServerFactory` and an extended test auth handler that can emit chosen claims" - test-local

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- Phase 3 `Server.Tests` harness (`AdsServerFactory`, `TestAuthHandler`, `GrpcTestClients`) and the `SourceGuardTests` pattern; `CryptoProvider`/`Crypto:Provider` seam; `CallerInterceptor` and `ServiceTools.GetUser(context)` in Services; Phase 4 migrations for adding the tenant column on both server contexts.

### Established Patterns
- Caller identity read from `context.UserState[ServiceTools.UserStateKey]`; services never touch HttpContext; guardrail rows today use `Id=1`; approvals are hash-bound (`WhatIfTokenGate`, plan hash).
- Central package versions; lock files in locked-restore CI; SDK 10.0.403 vs 10.0.401 pin rewrites unrelated lock files - revert drift.

### Integration Points
- Server `Program.cs` auth/policy registration, `HostedServices.cs` `[Authorize]` subclasses, `DeployService` guardrail/approval paths, Desktop `InProcessCallInvoker` (puts the caller in UserState), the WASM client's service-worker precache list.

</code_context>

<specifics>
## Specific Ideas

User chose: personal MSA accounts administer their own tenant scope; self-approval is a per-tenant audited setting (default off); only `Cost.GetRetailPrices` stays anonymous.

</specifics>

<deferred>
## Deferred Ideas

None - discussion stayed within phase scope.

</deferred>
