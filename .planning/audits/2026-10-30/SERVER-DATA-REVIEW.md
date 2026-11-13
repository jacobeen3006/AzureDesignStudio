# Server and Data Trust Review

Status: COMPLETE — targeted source audit finished; limitations are documented below.
Baseline: fa62bb9001fccb35bae7bea9df27499c4b31fa92
Scope: Server auth/RPC/deployment/approval/crypto; Workspace persistence/offline sync; Discovery/MCP isolation.

## Summary
Targeted source audit found actionable authorization, offline-sync, quota, and MCP reporting defects. Full review of every one of the 2,210-line DeployService RPC branches was not performed; no tests/builds/app or Azure/database operations were run. The stale `.planning/CODEBASE-REVIEW.md` was checked against current source: its claim that `Encoding.Default` is OS-dependent and its claim that `LoadSubscriptionInfo` returns secrets are not supported by current .NET 10 behavior/source (it returns only subscription id/name).

## Critical findings

### CR-01 — Guardrail and approval RPCs have no role separation
**Severity:** BLOCKER (security); **Confidence:** High
**Evidence:** `src/AzureDesignStudio.Server/Services/DeployService.cs:27-29,604-659,1295-1373,1745-1823`; only class-level `[Authorize]` and `[RequiredScope]` are present; `ApprovePlan` records any authenticated caller (lines 608-649), `GetGuardrailPolicy`/`SetGuardrailPolicy` similarly only check identity (1298-1366), and decom approval accepts any authenticated caller (1749-1814). Deploy later requires the approval principal match the deploying principal (`:795-800`), so the same identity can self-approve and execute. No `Deploy.Approver` / `Guardrail.Admin` role gate exists.
**Impact/repro:** An account with the app's ordinary API scope can rewrite the server's do-not-touch list and self-approve a plan before applying it; approval is not an independent control.
**Minimal repair/test:** Require explicit authorization policies for guardrail admin, deployment approver and decommission approver; enforce a separate approver identity from the plan author where that policy is intended. Integration-test ordinary scoped user gets 403, allowed roles work, and self-approval is rejected independently.
**Roadmap:** Phase 5 (IDA-04, IDA-06, IDA-05); regression coverage Phase 8 (TEST-02).

### CR-02 — Malformed object-identifier claims can crash RPCs
**Severity:** BLOCKER (availability/auth correctness); **Confidence:** High
**Evidence:** `src/AzureDesignStudio.Server/Services/ServiceTools.cs:8-11` returns the raw legacy URI claim without GUID validation. `DesignService.cs:29-36,99-106,119-136,153-168` and `DeployService.cs:51-58,109-116,244-251,404-411,701-710` call `new Guid(userIdClaim)` after checking only empty/null; similar paths continue later in DeployService.
**Impact/repro:** Send an otherwise authenticated, in-scope token with a non-GUID/missing mapped claim and invoke an affected RPC; `Guid` parsing throws and produces an unhandled gRPC failure rather than the required typed 401.
**Minimal repair/test:** Centralize extraction as `TryFrom`/`Guid.TryParse` accepting the current and legacy claim names; return typed 401 before any DB/ARM work. Parameterized integration tests for missing, malformed, and valid legacy claims across all RPCs.
**Roadmap:** Phase 5 (IDA-01, IDA-02); Phase 8 (TEST-01, TEST-02).

## Warning findings

### WR-01 — Offline sync discards pending edits on non-2xx application responses
**Severity:** BLOCKER (data loss risk)
**Evidence:** `src/AzureDesignStudio/Services/DesignGrpcService.cs:40-52,63-70` treats any completed `SaveAsync` RPC as success, then deletes pending data with `ClearPendingAsync`; it never checks `SaveDesignResponse.StatusCode`. The same applies in `FlushPendingAsync` (`:45-46`). Server errors are encoded in response fields (`src/AzureDesignStudio.Server/Services/DesignService.cs:76-90`), not necessarily thrown as `RpcException`.
**Impact/repro:** Queue a pending design, then make server return 400/507/500 without transport failure during flush; local row is deleted although server did not save it. A normal online SaveDesign response with non-200 also returns the code after clearing pending.
**Minimal repair/test:** Clear only on explicit 200; retain and report other status responses, with retry policy distinguishing terminal validation/quota errors from transient failures. Test 400 and 507 responses preserve the local payload; 200 removes it.
**Roadmap:** Phase 5 offline development (CRED-01 context); Phase 7 (SHELL-03 truthful offline-pending state); Phase 8 (TEST-02/TEST-04).

### WR-02 — Offline delete is local-only and can resurrect server designs
**Severity:** WARNING (sync correctness)
**Evidence:** `src/AzureDesignStudio/Services/DesignGrpcService.cs:133-149` deletes the local record on transport failure but persists no delete tombstone or queued operation. On reconnection, `GetAllSavedDesign` combines the server list with local names and `FlushPendingAsync` only uploads saves (`:34-52,80-94`).
**Impact/repro:** Delete a server-backed design while offline; reconnect and list designs—the unchanged server record returns, because no remote delete is ever sent.
**Minimal repair/test:** Represent deletions as durable pending operations/tombstones and replay through authenticated Delete on reconnect; resolve conflicts explicitly. Test offline delete survives app restart and disappears remotely after successful sync.
**Roadmap:** Phase 5 (offline behavior); Phase 8 (TEST-04).

### WR-03 — The 10-design quota is a non-atomic check-then-insert
**Severity:** WARNING (quota bypass/data invariant)
**Evidence:** `src/AzureDesignStudio.Server/Services/DesignService.cs:46-67` counts a user's designs, rejects at ten, then separately queries/inserts a design. There is no transaction/unique quota enforcement around count and insert.
**Impact/repro:** With nine designs, two concurrent Save RPCs for different names can both observe nine and insert, exceeding quota. Existing-name updates are also rejected once count is ten because quota runs before the update lookup.
**Minimal repair/test:** Query existing design before applying creation quota, and serialize/transactionally enforce per-user creation capacity (or use a concurrency-safe quota mechanism). Concurrent test from nine records must allow exactly one new design; existing-name update at quota remains allowed.
**Roadmap:** Phase 8 (TEST-02 quota test); Phase 6 (BOUND-04 if per-user concurrency control is introduced).

### WR-04 — RSA-OAEP encryption has an unvalidated plaintext size ceiling
**Severity:** WARNING (credential-save reliability)
**Evidence:** `src/AzureDesignStudio.Server/Services/CryptoService.cs:31-35` sends the entire UTF-8 client secret directly to Key Vault RSA-OAEP; there is no size check or envelope encryption. RSA-OAEP cannot encrypt arbitrarily long values, so sufficiently long secrets fail at the crypto call and are caught by `SaveSubscriptionInfo` as a generic 400 (`DeployService.cs:67-84`).
**Impact/repro:** Submit a client secret longer than the configured RSA key's OAEP limit; the save fails without a useful validation response. Limit depends on actual key size/algorithm; no live Key Vault call was made.
**Minimal repair/test:** Prefer envelope encryption (data-encryption key + Key Vault wrap/unwrap), or reject over a documented bound before the service call with a clear status. Unit-test boundary behavior using a crypto abstraction/fake.
**Roadmap:** Phase 5 (CRED-01/02 provider abstraction); Phase 9 (CRED-03 credential model migration).

### WR-05 — Approval rows are global per hash, not principal/tenant scoped
**Severity:** WARNING (cross-user interference)
**Evidence:** `PlanApprovalModel.PlanHash` is the sole primary key (`src/AzureDesignStudio.Server/Models/DesignDbContext.cs:52-57`); `ApprovePlan` replaces an existing row by hash (`DeployService.cs:635-650`), while create/apply looks up solely by hash (`:786-800`). Decommission approvals use the same global-hash key and replacement behavior (`:1799-1814`), and execute fetches by hash (`:1896`).
**Impact/repro:** Where identical canonical plan bytes/hash are previewed by multiple users/tenants, a later approval overwrites the earlier principal's row; the first principal's apply is denied and the latter inherits the one globally stored approval. Deploy authorization checks principal, which prevents direct cross-principal application, but the storage model creates race/interference and does not meet tenant-scoped approval expectations.
**Minimal repair/test:** Scope approval identity by tenant plus user (or use a unique approval id with explicit tenant/user/hash keys); verify stored subscription/window/hash metadata at consume time. Test two principals and tenants with the same plan hash cannot overwrite/read/consume each other's approval.
**Roadmap:** Phase 5 (IDA-05/IDA-06); Phase 8 (TEST-02 approval ownership).

### WR-06 — MCP cost summary reports a sum of unit prices as a total
**Severity:** WARNING (misleading decision support)
**Evidence:** `src/AzureDesignStudio.Mcp/AdsMcpTools.cs:102-149` explicitly describes cached rows as Retail Prices unit prices (103,111), but sums each row's `retailPrice` and returns it as `TotalRetailPrice` (119-123,146-149), with buckets also summing those prices (130-141). No usage quantities are applied, and the code explicitly says there are no actual-spend rows.
**Impact/repro:** Query multiple cached meter rows and receive a “total” that is only a sum of unrelated per-unit rates, not estimated or actual spend.
**Minimal repair/test:** Rename/expose the result as an unweighted unit-price aggregate or remove the total; only report spend when units/quantity and compatible currency/period are available. Test mixed unit/currency inputs do not present a false monetary total.
**Roadmap:** Phase 10 (POST-02 provenance and truthful local cost); Phase 8 (TEST-02 or MCP manifest/contract tests).

## Review coverage and limits
- Read current `Program.cs`, `ServiceTools.cs`, `DesignService.cs`, the full `DeployService.cs` in two segments, `CryptoService.cs`, `DesignGrpcService.cs`, `WorkspaceDbContext.cs`, `WorkspaceEntities.cs`, MCP host/workspace/tools and `AdsToolEngine.cs`/views.
- Reviewed current source against stale claims in `.planning/CODEBASE-REVIEW.md`; specifically, `LoadSubscriptionInfo` returns only subscription id/name (`DeployService.cs:105-129`; proto defines secret on the Save request, `deploy_model.proto:8-9,38`), and the `Encoding.Default` claim is not a valid portability bug for current .NET (it is UTF-8 on .NET Core/.NET 10); retain UTF-8 explicitly for clarity, not as a confirmed defect.
- Did not run tests/builds, launch the app, contact Azure/Key Vault, open workspace/user databases, inspect credentials, or review every Discovery engine and all RPC branches. Those would exceed the requested disk-only scope and/or require the integrity worker.
- No source/config/lock/STATE/ROADMAP files were changed. No user-owned untracked files were modified.

## Roadmap traceability
- Phase 5: CR-01, CR-02, WR-04, WR-05.
- Phase 6: WR-03 only if concurrency limits are implemented as part of its bounds work.
- Phase 7: WR-01; Phase 5 offline requirements also relevant to WR-02.
- Phase 8: tests/regression coverage for CR-01 through WR-05.
- Phase 9: WR-04 credential migration.
- Phase 10: WR-06 cost semantics.

## Completeness
This targeted area audit is complete. It is not a whole-repository audit; remaining areas and unreviewed code paths are listed above.
