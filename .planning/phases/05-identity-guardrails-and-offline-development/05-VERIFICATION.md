---
phase: 05-identity-guardrails-and-offline-development
verified: 2026-11-14T17:00:00Z
status: passed
score: 5/5 must-haves verified
covered_files:
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-01-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-01-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-02-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-02-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-03-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-03-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-04-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-04-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-05-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-05-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-06-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-06-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-07-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-07-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-08-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-08-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-09-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-09-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-10-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-10-SUMMARY.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-11-PLAN.md"
  - ".planning/phases/05-identity-guardrails-and-offline-development/05-11-SUMMARY.md"
  - "src/AzureDesignStudio.Server.Tests/Hosting/AuthorizationSurfaceTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/IdentityRpcTests.cs"
  - "src/AzureDesignStudio.Server.Tests/Hosting/LegacyClaimTests.cs"
  - "src/AzureDesignStudio.Server/Auth/AdsPolicies.cs"
  - "src/AzureDesignStudio.Server/Program.cs"
  - "src/AzureDesignStudio.Server/Services/CryptoProvider.cs"
  - "src/AzureDesignStudio.Server/Services/DevelopmentCryptoService.cs"
  - "src/AzureDesignStudio.Server/Services/HostedServices.cs"
  - "src/AzureDesignStudio.Services/Auth/AdsCaller.cs"
  - "src/AzureDesignStudio.Services/Auth/AdsIdentity.cs"
  - "src/AzureDesignStudio.Services/DeployService.cs"
  - "src/AzureDesignStudio.Services/DesignService.cs"
  - "src/AzureDesignStudio.Services/ServiceTools.cs"
covered_digest: "v4:sha256:1b578a5bf2cd3963a9f7279a06de7434a44be92454b5b756135e8a555a57a97f"
behavior_unverified: 0
overrides_applied: 0
re_verification: none
---

# Phase 5: Identity, Guardrails, and Offline Development Verification Report

**Phase Goal:** Every request carries a trustworthy user and tenant, guardrail policy and approvals can only be changed by the right role inside the right tenant, and a developer can work offline in DEBUG without weakening production.
**Verified:** 2026-11-14
**Status:** passed (with residual risks listed below; none contradicts a success criterion)
**Re-verification:** No - initial verification

## Goal Achievement

### Observable Truths (the five ROADMAP success criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A token with a missing `oid`/`tid` or a non-GUID claim gets a typed in-band 401; no request path throws; no deferred-save retry loop starts | VERIFIED | `AdsIdentity.TryFrom` (Services/Auth/AdsIdentity.cs) rejects missing, non-GUID, `Guid.Empty`, conflicting-spelling and multi-valued-disagreeing claims and never reads `sub`. `ServiceTools.TryGetCaller` is the first statement of all 22 Design and Deploy RPCs (17 direct calls; `StackWhatIf`/`CreateStack` go through `PrepareStackAsync` at DeployService.cs:1227). Each answers `StatusCode = 401` in the response, never an `RpcException`. `IdentityRpcTests` is a table of all 22 RPCs, with a test that the table equals the host's mapped gRPC methods; it asserts 401 in-band for a token without `tid` and for a non-GUID short `oid` on every RPC. Client contract: `DesignGrpcService` defers a save only for `Unavailable`/`HttpRequestException`/`Unauthenticated` exceptions, so an in-band 401 is returned as is; `ClientWrapperTests.ADesktopClientDoesNotQueueOrRetryA401InAResponse` pins it. Server.Tests ran green. |
| 2 | A token carrying only the legacy `.../objectidentifier` URI claim still reads existing designs, subscriptions and quota | VERIFIED | `AdsIdentity` accepts the URI spelling and the short spelling and treats them as one identity. `LegacyClaimTests` (3 tests, run by me, passing): a URI-only token saves/lists/loads/deletes designs; links a subscription and a short-`oid` token of the same user reads it back; both spellings share the same 10-design quota (11th refused with 507, replace allowed). `DesignModel.UserId` is a `Guid`, so no row re-keying is needed. |
| 3 | A reflection-based test enumerates every gRPC method and fails on any unauthenticated callable method outside the explicit anonymous list (only `Cost.GetRetailPrices`) | VERIFIED | Production: `AddAdsAuthorization` sets a `FallbackPolicy` of `RequireAuthenticatedUser`; `MapAdsGrpcService<T>(allowAnonymous)` is the only mapping path (`RequireAuthorization()` unless the flag is written); only `RetailPriceService` is mapped with `allowAnonymous: true`; Development reflection is `.RequireAuthorization()`; static/Razor/fallback are explicit `.AllowAnonymous()`. Test: `AuthorizationSurfaceTests` enumerates `EndpointDataSource` `GrpcMethodMetadata` in Testing and Development hosts against a literal `["/Cost/GetRetailPrices"]` held in the test file (so widening production breaks the test), asserts exact equality of the anonymous set, asserts every proto service in SharedModels has a mapped endpoint, and makes a real unauthenticated call to every unary/server-streaming protected method expecting `Unauthenticated`. |
| 4 | No `Guardrail.Admin` = 403 on guardrail read/mutation; two tenants cannot read or overwrite each other's policies/approvals; approval needs `Deploy.Approver`, self-approval refused as a separate rule | VERIFIED | Roles: `GetGuardrailPolicy`/`SetGuardrailPolicy`/tenant-settings RPCs check `caller.CanAdministerGuardrails` (role, personal-account scope, or the in-process single-user host marker, per CONTEXT decisions) and answer in-band 403 `GUARDRAIL_ADMIN_REQUIRED` before reading the body. `ApprovePlan` checks `CanApprove` first (403 `PLAN_APPROVER_ROLE_REQUIRED`, before any hash or preview lookup), then self-approval as its own rule (403 `PLAN_SELF_APPROVAL_REFUSED`, honouring the tenant setting, default off). Decom approval needs `Deploy.Approver` and has no self-approval rule (CONTEXT). Tenancy: guardrail rows are read and upserted by `caller.ScopeId` (tid for orgs, oid for personal accounts); approvals use composite key `[ScopeId, PlanHash]` in every `FindAsync` (lines 917, 1122, 1498, decom 2640/2737); the what-if gate is scope-bound and fails closed (WR-01). Tests (all passing): `TenantIsolationTests` (two orgs, two personal accounts, org vs personal, null-tenant default never written, legacy empty-scope approvals found by no real caller), `GuardrailAuthorizationTests`, `ApprovalRulesTests` (role matrix, self-approval matrix), `TenantSettingsRpcTests`. SQL Server migrations proven on a real container (below). |
| 5 | A DEBUG developer registers and exercises a subscription with no Key Vault and no Azure credentials; Production refuses development keys and still requires Key Vault | VERIFIED | `CryptoProvider.Register` selects `DevelopmentCryptoService` (Data Protection, file key ring, `dev1:` prefix) only when `Crypto:Provider=Development`; Production throws at startup even with the opt-in; any environment other than Development needs the explicit `Crypto:AllowDevelopmentProvider=true` (WR-02 allow-list). Server `launchSettings.json` sets `Development` environment plus `Crypto__Provider=Development` on its profiles; checked-in appsettings and the Dockerfile never select it (guarded by `DevelopmentCryptoTests` source guards). Default is the lazy Key Vault service. `DevelopmentCryptoTests.TracerDevelopmentProviderStoresListsAndDecryptsWithNoVaultAndNoCredential` registers, lists and decrypts a subscription on a real host with no vault; `ProductionHostWithTheDevelopmentProviderDoesNotStart` and `ProductionHostWithKeyVaultAndNoVaultBootsAndAnswers503InBand` cover the Production side; `CryptoProviderTests` covers the allow-list. |

**Score:** 5/5 truths verified, 0 present-but-behavior-unverified. Every behavior-dependent truth (single-use/scope-bound gate, approval non-takeover, audit-in-same-commit, write-failure in-band 503) has a passing behavioral test.

### Requirements Coverage

All eight IDs appear in the plans' `requirements` frontmatter and in REQUIREMENTS.md (traceability rows map each to Phase 5). No orphaned Phase 5 IDs (CRED-03/04/05 are mapped to Phase 9).

| Requirement | Source Plans | Status | Evidence |
|-------------|--------------|--------|----------|
| IDA-01 | 05-01, 05-02, 05-11 | SATISFIED | Criterion 1 |
| IDA-02 | 05-01, 05-02, 05-11 | SATISFIED | Criterion 2 |
| IDA-03 | 05-03, 05-11 | SATISFIED | Criterion 3 |
| IDA-04 | 05-06, 05-10, 05-11 | SATISFIED | Criterion 4 (role gate) |
| IDA-05 | 05-04, 05-06, 05-07, 05-08, 05-11 | SATISFIED | Criterion 4 (tenant scoping, migrations) |
| IDA-06 | 05-07, 05-09, 05-10, 05-11 | SATISFIED | Criterion 4 (approver role, separate self-approval rule) |
| CRED-01 | 05-05, 05-10, 05-11 | SATISFIED | Criterion 5 |
| CRED-02 | 05-05, 05-10, 05-11 | SATISFIED | Criterion 5 |

### Commands I ran

| Command | Result |
|---------|--------|
| `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` | 524 passed, 0 failed, 11 skipped (all `Category=SqlServer`, need a container) |
| `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` | 1831 passed, 0 failed (first run; the known Phase 4 SQLite-pool flake did not occur) |
| `node --test src/AzureDesignStudio/JsTests/*.test.cjs` | 17/17 passed |
| `bash src/RunSqlServerTests.sh` (SQL Server 2022 container, port 14377) | 11 passed, 0 failed (TenantGuardrails and TenantApprovals migrations among them); the script's own container removed, no other containers touched |
| `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst` | 0 errors, 1 pre-existing PublishFolderType warning |
| Focused re-run: AuthorizationSurface, IdentityRpc, LegacyClaim, DevelopmentCrypto, ApprovalRules, GuardrailAuthorization, TenantIsolation, WriteFailure | 170 passed, 0 failed, 0 skipped |
| `git status --short` after all of the above | clean (no `packages.lock.json` drift, nothing to revert) |

No Entra token was fetched or printed; the Desktop app was not launched.

### Anti-Patterns

Debt-marker scan (TBD/FIXME/XXX, plus TODO/HACK/NotImplemented outside tests) over the 206 files changed by Phase 5 under `src/`, `docs/` and `AGENTS.md`: none found. No stubs: the identity, guardrail, approval and crypto paths are fully implemented and tested.

### Review fixes (05-REVIEW-FIX.md) judged

- CR-01 (inherited default's author/time): code confirmed. `GetGuardrailPolicy` blanks `UpdatedBy`/`UpdatedAt` and sets `Inherited` for a NULL-tenant row; own rows keep theirs. Two tests pin it. The inherited body itself remains readable by every guardrail administrator, including every personal account on a hosted server. This is the operator-owned shared default, not another tenant's policy, so criterion 4 ("two tenants cannot read ... each other's policies") holds. Residual risk, see below.
- WR-01..04, WR-08: confirmed in code and tests (fail-closed gate scope, development-provider allow-list, display-name sanitizer, stack approval bound to the stack body hash, in-band 503 for a failed write with a single insert retry).
- WR-06: confirmed. A different approver is refused with 409 `PLAN_ALREADY_APPROVED`; same-approver replacement writes a `PlanApprovalReplaced` event. The same-scope requester capture remains (see below).
- WR-05: skipped by user decision, consistent with CONTEXT (replace is the default mode); consequence documented.
- WR-07: documentation and runbook guard only.

## Residual Risks and Honest Gaps (for the user, not failures)

1. **CR-01 residual.** The inherited default guardrail body is readable by every guardrail administrator (all personal accounts on a hosted server). Operators must keep nothing private in it. Withholding the body would be stricter but empties the editor for inheriting tenants; a product decision.
2. **WR-06 residual.** The requester recorded on an approval is the newest live preview's requester in the same scope; the approval request carries only the hash. A principal in the same scope with the full payload and a working credential could capture the requester slot. Mitigated: the approved bytes are what the approver approved, two distinct principals are still involved, an approval cannot be taken over afterwards, replacement is audited. Documented in the operator guide; accepted.
3. **WR-05.** Under the default `replace` mode, a tenant administrator's first own policy stops inheriting every default rule; the default is a fallback, not a floor. By design; documented.
4. **Implied roles.** Personal-account scopes are their own Guardrail.Admin and Deploy.Approver, and the in-process Desktop host is both plus self-approval (a CONTEXT decision; the marker can only be set by `InProcessCallInvoker`, never from a claim).
5. **macOS deploy.db upgrade not independently evidenced.** 05-11 records the user's combined approval, but the executor found the scratch `deploy.db` untouched and no `backups/deploy` folder (no trace of an app launch). The migration is proven offline by the automated tests and the SQL Server container run, not by a macOS app launch. I did not launch the Desktop app.
6. **Real token claims (assumption A1) approved but not itemised.** The claim shapes and account types tried were not reported; whether a real `Server.Access` registration was available is unknown. The tests use synthetic claims of both spellings.
7. **GitHub `sqlserver` CI job has not run** (branch unpushed). I ran the same suite locally against a SQL Server 2022 container: 11/11.
8. **Windows Desktop target not compiled on this Mac.** Only the Mac Catalyst target was built; Windows-specific code under `Platforms/Windows` is unverified here.
9. **Pre-existing, out of scope.** Core.Tests Phase 4 workspace tests share the process-wide SQLite pool and can flake (passed in my run). The Desktop scan drawer reads the server policy only when `StatusCode == 0` while the service answers 200, so the server copy is never used there (noted in 05-REVIEW-FIX.md).
10. **Upgrade consequences** (documented): approvals pending across the upgrade must be re-approved (legacy rows sit in the empty scope and fail closed); a stack's plan hash is now a different value, so pending stack approvals must be re-approved.

## Gaps Summary

No gaps. All five success criteria are met by code I read and tests I ran; all eight requirement IDs are accounted for. The items above are follow-ups for the user and the orchestrator, not blockers.

---

_Verified: 2026-11-14_
_Verifier: Claude (gsd-verifier)_
