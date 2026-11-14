---
phase: 05-identity-guardrails-and-offline-development
fixed_at: 2026-11-14T00:00:00Z
review_path: .planning/phases/05-identity-guardrails-and-offline-development/05-REVIEW.md
iteration: 1
findings_in_scope: 9
fixed: 8
skipped: 1
status: partial
---

# Phase 5: Code Review Fix Report

**Fixed at:** 2026-11-14
**Source review:** `.planning/phases/05-identity-guardrails-and-offline-development/05-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 9 (CR-01, WR-01..WR-08; Info findings out of scope)
- Fixed: 8 (CR-01, WR-01, WR-02, WR-03, WR-04, WR-06, WR-07, WR-08)
- Skipped: 1 (WR-05 code change: by design, user decision; documented in the operator guide)

Verification (run in the main checkout on branch `feat/wave-b2-b4-governance-preflight`, no worktree):
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 524 passed, 11 skipped (SQL Server tests that need a database), 0 failed.
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1831 passed, 0 failed.
- `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst`: 0 errors.
- No migration or SQL changed (so `RunSqlServerTests.sh` was not needed); no JS touched; no `packages.lock.json` drift.

## Fixed Issues

### CR-01: Inherited default guardrail row author and time disclosed to every caller

**Files modified:** `src/AzureDesignStudio.Services/DeployService.cs`, `src/AzureDesignStudio.SharedModels/deploy_model.proto`, `src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs`, `docs/identity-and-guardrails.md`
**Commit:** 7dadf6d
**Needs human verification:** yes (decision on the body, below)
**Applied fix:** `GetGuardrailPolicy` returns empty `updated_by` and `updated_at` for an inherited (NULL-tenant) row and sets the new additive field `inherited = 6` (`GuardrailPolicyResponse`). The inherited body is still returned, because the editor and the Desktop scan drawer need the effective list and the same list already binds the caller at deploy time; the author identity and time are the cross-tenant leak and are gone. Tests: `TheInheritedDefaultNeverDisclosesItsAuthorOrTimestamp` (fails without the fix: author, oid and year asserted absent for an org member and personal accounts), `AScopeWithItsOwnRowStillSeesItsOwnAuthorAndTimeAndNoInheritedMarker`, and the existing inherited-read test now pins `Inherited`.
**Residual (documented in the operator guide, "Upgrade consequences"):** the default's body is still readable by every guardrail administrator, including every personal account on a hosted server. Operators must keep nothing private in it. Withholding the body entirely would be the stricter alternative; it would leave a tenant on the inherited default with an empty editor and an empty scan-drawer policy, so it was not done without your call.

### WR-01: Tenant isolation of the what-if gate was opt-in

**Files modified:** `src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs`, `src/AzureDesignStudio.Core.Tests/WhatIfTokenGateScopeTests.cs`, `src/AzureDesignStudio.Server.Tests/WhatIfRpcTests.cs`
**Commit:** 2e7ea27
**Needs human verification:** yes (logic change in the deploy gate)
**Applied fix:** fail closed instead of removing the overloads (about 60 test call sites use them). `ValidateAndConsume` and `ValidateAndConsumeForStack` now require `entry.ScopeId == scopeId` exactly, null included: a scoped token consumed without a scope, or an unscoped token consumed with one, answers the same Stale result as an unknown token and is not burned. The scope-less `TryPeekPlan` overloads only serve previews issued without a scope. Production callers already pass the scope. Tests: scoped deployment and stack tokens are Stale without a scope; the scope-less peek never serves a scoped preview. The old test that pinned the skip (`AScopedTokenStillConsumesThroughTheScopeLessOverload`) was rewritten to pin the refusal; five `WhatIfRpcTests` consumes now pass the caller's scope.

### WR-02: Development crypto provider refused only for the literal "Production"

**Files modified:** `src/AzureDesignStudio.Server/Services/CryptoProvider.cs`, `src/AzureDesignStudio.Server.Tests/Hosting/CryptoProviderTests.cs`, `src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs`, `docs/identity-and-guardrails.md`, `AGENTS.md`
**Commit:** 2ccbdff
**Applied fix:** allow-list. The provider is accepted only in the Development environment, or with the explicit opt-in `Crypto:AllowDevelopmentProvider=true`; Production is refused even with the opt-in. Staging, UAT, "Prod" and an empty environment name now stop startup with a message naming the opt-in. `DevelopmentIsAllowedOutsideProduction` (which pinned Staging as allowed) was replaced by tests for the allow-list, the refusal list, the opt-in, and "the opt-in never admits Production"; a host-level test boots a Staging and a "Prod" host and checks it does not start. The Development launch profiles already set `ASPNETCORE_ENVIRONMENT=Development`; the host tests that run in the `Testing` environment now pass the opt-in.

### WR-03: Display name written unsanitised into audit rows, approvals and logs

**Files modified:** `src/AzureDesignStudio.Services/Auth/AdsCaller.cs`, `src/AzureDesignStudio.Server.Tests/AdsIdentityTests.cs`, `docs/identity-and-guardrails.md`
**Commit:** 4cb1a43
**Applied fix:** new `AdsCaller.SanitizeDisplayName` used by `Principal`: drops control, format (bidi override, zero-width), line/paragraph separator, private-use, unassigned and unpaired-surrogate characters (CR/LF/tab become a space), turns `<` and `>` into spaces so a name can never forge the `<guid>` suffix, collapses whitespace and cuts on a text-element boundary within the 320-character column. A single grapheme longer than the room falls back to the bare id. Tests cover CRLF, NUL/BEL, RLO and zero-width, U+2028, whitespace, a forged `Alice <victim-guid>` (exactly one `<...>` pair, the caller's own id), a 5000-emoji cut and a lone surrogate. This commit also corrects the guide's statement about the `roles` claim (IN-05, second bullet).

### WR-04: An approval did not bind the stack policy

**Files modified:** `src/AzureDesignStudio.Core/Common/DeployPlanArtifact.cs`, `src/AzureDesignStudio.Services/DeployService.cs`, `src/AzureDesignStudio.Core.Tests/DeployPlanArtifactTests.cs`, `src/AzureDesignStudio.Server.Tests/StackRpcTests.cs`, `AGENTS.md`
**Commit:** edcc6f9
**Needs human verification:** yes (changes what a stack's plan hash is)
**Verified exploitable:** yes. The stack token binds the stack body, but the approval was keyed by the plain scope+template+parameters hash, so an approval granted from a plain what-if could be redeemed for a stack with a deny mode, unmanage policy or bypass flag the approver never saw.
**Applied fix:** no schema change. A stack's approvable plan is now `DeployPlanArtifact.CreateForStack` (scope + template + parameters + `DeploymentStackBody.PlanHash`), a different hash from the plain plan. `StackWhatIf` returns and registers that hash, `ApprovePlan` approves it, and `CreateStack` recomputes it, so a plain approval never covers a stack and a changed deny mode, unmanage action, bypass flag or stack name needs a new preview and approval. The client already echoes `StackWhatIf.plan_hash` for both calls, so it needed no change. Plain plans keep their old canonical bytes and hashes. Tests: hash differs per setting and from the plain hash; plain approval refused (`PLAN_NOT_APPROVED` / `PLAN_APPROVAL_REQUIRED`); changed bypass flag refused; stored approval verifies. `CreateStack_PolicyChangedAfterPreview_IsBlocked` now expects `PLAN_APPROVAL_REQUIRED` (still blocked; the stack token's `WHAT_IF_PAYLOAD_CHANGED` remains the second line). Because the hash is a different value, a stack approval pending across the upgrade must be re-approved (stack approvals were never long-lived).

### WR-06: Requester is "the newest preview" and approvals were overwritten silently

**Files modified:** `src/AzureDesignStudio.Services/DeployService.cs`, `src/AzureDesignStudio.Services/Models/TenantModels.cs`, `src/AzureDesignStudio.Server.Tests/Hosting/ApprovalRulesTests.cs`, `docs/identity-and-guardrails.md`
**Commit:** 155018f
**Needs human verification:** yes (behavior change in `ApprovePlan`; residual risk accepted)
**Applied fix (minimal mitigation):** an approver other than the one who recorded an existing approval for (scope, hash) is refused with 409 `PLAN_ALREADY_APPROVED` and the row is untouched. The same approver approving again replaces the row and writes a `PlanApprovalReplaced` event to `AdsSecurityEvents` (same commit; detail names the old and new requester id; no schema change, `EventType` fits its 32-character column). The old test `ASecondApproverReplacingTheRowKeepsTheRequesterOfTheLivePreview` pinned the silent takeover and was rewritten to pin the refusal.
**Not fixed (needs a product decision):** the same-scope requester capture. The approval request carries only the hash, so the requester is still the newest live preview's. Binding it to the exact token would need the approver to hold the requester's token or be shown the requester. The residual risk and what limits it (capturer needs the full payload and their own working credential; the approved bytes are what the approver approved; two different principals are still involved; the approval cannot be taken over afterwards; replacement is audited) is written in the operator guide.

### WR-07: Operator recipe put the bearer token on the grpcurl command line

**Files modified:** `docs/identity-and-guardrails.md`, `src/AzureDesignStudio.Core.Tests/IdentityRunbookTests.cs`
**Commit:** b308912
**Applied fix:** both grpcurl examples now use `-expand-headers -H 'Authorization: Bearer ${ADS_TOKEN}'` (single quotes, so the shell does not expand it); the "visible in the process list" caveat is replaced by an explanation of why single quotes matter. The runbook guard `Guide_PassesTheTokenThroughAnEnvironmentVariable` previously required the literal `Authorization: Bearer $ADS_TOKEN` (the leaking form). It now requires `-expand-headers` and `'Authorization: Bearer ${ADS_TOKEN}'` and refuses `Bearer $ADS_TOKEN`; the rest of the guard (env var in C#, no literal token) is unchanged and green.

### WR-08: Blanket DbUpdateException retry hid failures and leaked an unhandled RPC fault

**Files modified:** `src/AzureDesignStudio.Services/DeployService.cs`, `src/AzureDesignStudio.Server.Tests/Hosting/WriteFailureTests.cs` (new), `docs/identity-and-guardrails.md`
**Commit:** 5e847c8
**Needs human verification:** yes (error-path logic)
**Applied fix:** one shared `WriteWithRaceRetryAsync`. Only a failed INSERT of the scope's first row is retried, once, after clearing the tracker; this is provider-agnostic (it does not inspect SQL error numbers). A failed update, a second failed insert, or a lost connection is logged (`LogCritical` when the failed commit carried its security event, else `LogError`) and answered in-band with 503 `GUARDRAIL_POLICY_NOT_SAVED` / `TENANT_SETTINGS_NOT_SAVED`, with nothing stored. Non-`DbUpdateException` failures (the existing audit-insert-fails tests) still propagate as before. New tests: retry succeeds after one failure, two failures answer 503 and log critical, a failed update is attempted exactly once, for both RPCs.

## Skipped Issues

### WR-05: Replace mode lets any tenant administrator discard the operator's inherited rules

**File:** `src/AzureDesignStudio.Services/DeployService.cs:1731-1757, 1911-1920, 2014-2031`
**Reason:** by design, user decision. 05-CONTEXT.md fixes `replace` as the default, makes both modes selectable per tenant by that tenant's `Guardrail.Admin` (or personal-account owner), and makes the NULL-tenant row the inherited default. The finding's consequence is that behavior, so no code was changed.
**What was done instead (commit 31ee5fa, docs only):** `docs/identity-and-guardrails.md` states the consequence plainly in the interpretation section: the inherited default is a fallback, not an enforced floor; the first own policy under `replace` stops inheriting every default rule without any warning in the response or the audit event; each scope picks its own mode, so the default binds only scopes with no policy of their own and scopes that chose `merge`; the default row is immutable through the application (edit it in the database); and an operator who needs a default that binds every tenant cannot get it from this version (it would need an operator-only enforce flag and a migration). The optional suggestion to mention the stop-inheriting fact in the Set response or audit detail was not taken, because it changes response and audit shape without a decision; it is a small follow-up if you want it.

## Notes for the caller

- Pre-existing, not touched: the Desktop scan drawer (`TenantScanDrawerTemplate.razor`, two places) reads the server policy only when `response.StatusCode == 0`, but the service answers 200, so the "server copy" is never used there. Out of scope here; worth its own fix.
- Commits made (in order): 7dadf6d CR-01, 2e7ea27 WR-01, 2ccbdff WR-02, 4cb1a43 WR-03, edcc6f9 WR-04, 31ee5fa WR-05 (docs), 155018f WR-06, b308912 WR-07, 5e847c8 WR-08. Not pushed.

---

_Fixed: 2026-11-14_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
