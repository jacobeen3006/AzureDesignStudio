# Wave B2-B4: Policy and Quota Preflight, Governance Findings on Scans — Implementation Plan

> **For agentic workers:** Execute task by task in order; each task is test-first and ends in its own commit(s). Steps use checkbox (`- [ ]`) syntax.

**Goal:** Before a deployment, the review shows which resources Azure Policy would deny or audit and whether the subscription has the quota for what is being created. After a tenant scan, Advisor recommendations, non-compliant policy states and unhealthy Defender assessments are stored with the scan and shown per resource in the drawer, on the topology, in both reports and through MCP.

**Architecture:**
- **Preflight (B2, B4):** two networked engines in `Discovery/Preflight` (`PolicyPreflight`, `QuotaPreflight`) run inside the existing `WhatIf` RPC *after* ARM what-if succeeds. They read the what-if `after` payloads, which ARM has already resolved (parameters, `resourceGroup().location`, copy loops). Results ride on new `WhatIfResponse` fields. They are advisory: they never withhold the what-if token, never throw into the RPC, keep partial results when time runs out, and turn every failure into a note.
- **Governance (B3):** new `Discovery/Governance` (queries, normalizer, model). The scan engine runs optional extra Resource Graph queries per subscription group ("sources"); a source that fails is recorded as unavailable and never makes the scan Partial. Rows go to a new workspace table keyed by scan id (the `ChangeFeed` precedent: guarded `CREATE TABLE IF NOT EXISTS`, no foreign key). The scan manifest moves from a private Desktop record to a public Discovery type so the drawer, the report and MCP read one shape.

**Tech Stack:** .NET 10, xUnit, System.Text.Json, gRPC/protobuf (in-process on Desktop), EF Core SQLite workspace, Blazor + AntDesign, ModelContextProtocol server.

**Spec:** `docs/feature-plan-2026-11.md`, Wave B items B2, B3, B4 (N33, N34).

## Facts verified on Microsoft Learn (2026-11-13)

Each fact below was read on the primary page and re-checked by an independent verifier. Build fixtures from these shapes.

**checkPolicyRestrictions** (`rest/api/policyinsights/policy-restrictions/*`):
- `POST {mgmt}/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.PolicyInsights/checkPolicyRestrictions?api-version=2024-10-01` (also the same without `/resourceGroups/{rg}`). The management-group variant has no `includeAuditEffect`; not used here.
- Body: `{ "resourceDetails": { "resourceContent": {…}, "apiVersion": "…", "scope": "…" }, "pendingFields": [ … ], "includeAuditEffect": true }`. `resourceDetails` is required. `resourceContent` "can be a partial set of all resource properties". `scope` is for child resources (the parent id). This plan sends no `pendingFields`.
- Response: `contentEvaluationResult.policyEvaluations[] { policyInfo { policyDefinitionId, policySetDefinitionId, policyDefinitionReferenceId, policyAssignmentId }, evaluationResult (string, "typically NonCompliant"), evaluationDetails { evaluatedExpressions[] { expression, path, operator, … }, reason }, effectDetails { policyEffect } }` — this "will indicate if the partial content that was provided will be denied as-is". `fieldRestrictions[]` describes constraints on **pending fields** (`result` Required|Removed|Deny|Audit means "required by policy", not "violated").
- **Verdicts come only from `policyEvaluations`, classified by `effectDetails.policyEffect`.** `fieldRestrictions` is not turned into findings.
- Permission: `Microsoft.PolicyInsights/checkPolicyRestrictions/read`. Subscription writes are throttled per principal by a token bucket; a few dozen POSTs per review are within it.
- Not documented: management-group-inherited assignments, whether template expressions are allowed in `resourceContent`, whether ARM what-if masks secure values in `after` payloads. No Microsoft "policy what-if" for a whole template exists; ARM enforces Deny at deployment (`RequestDisallowedByPolicy`).

**Quota** (`rest/api/compute/usage/list`, `resource-skus/list`, `virtualnetwork/usages/list`):
- `GET {mgmt}/subscriptions/{sub}/providers/Microsoft.Compute/locations/{loc}/usages?api-version=2026-04-01` → `value[] { name { value, localizedValue }, currentValue, limit, unit }` + `nextLink`. Regional total is `name.value == "cores"` ("Total Regional vCPUs"); families look like `standardDSv3Family` ("Standard DSv3 Family vCPUs").
- `GET {mgmt}/subscriptions/{sub}/providers/Microsoft.Compute/skus?api-version=2021-07-01&$filter=location eq '{loc}'` → `value[] { resourceType ("virtualMachines"), name ("Standard_D2s_v5"), family, locations[], capabilities[] { name, value } (`vCPUs`, string), restrictions[] { type (Location|Zone), values[], restrictionInfo { locations[], zones[] }, reasonCode (QuotaId|NotAvailableForSubscription) } }`.
- `GET {mgmt}/subscriptions/{sub}/providers/Microsoft.Network/locations/{loc}/usages?api-version=2026-01-01` → same `value[]` shape; names include `PublicIPAddresses`. `StandardSkuPublicIpAddresses` is documented only by the Microsoft.Quota API.
- Not documented (best effort, surfaced as caveats): that a SKU's `family` equals the usage `name.value`; whether `vCPUs` or `vCPUsAvailable` counts. Quota ≠ capacity. Spot quota is a separate pool. AKS upgrades temporarily use extra vCPUs.
- Permissions: `Microsoft.Compute/locations/usages/read`, `Microsoft.Compute/skus/read`, `Microsoft.Network/locations/usages/read`.

**Resource Graph governance tables** (ARG samples, Advisor/Policy/Defender REST):
- Same endpoint and `$skipToken` paging as `resources`; no `$skipToken` when every output column is dynamic or null, so project string columns with `tostring(...)`.
- `advisorresources`, `type =~ 'microsoft.advisor/recommendations'`: `properties.category` (HighAvailability|Security|Performance|Cost|OperationalExcellence), `properties.impact` (High|Medium|Low), `properties.shortDescription.problem|solution`, `properties.resourceMetadata.resourceId|source`, `properties.recommendationStatus` (New|Postponed|Dismissed|Completed; "applicable from v2026-02-01-preview", often absent), `properties.suppressionIds`.
- `policyresources`, `type =~ 'microsoft.policyinsights/policystates'`: `properties.complianceState`, `properties.resourceId`, `properties.policyAssignmentId|Name`, `properties.policyDefinitionId`, `properties.policyDefinitionReferenceId`, `properties.policySetDefinitionId`. Several rows per resource/assignment are possible.
- `securityresources`, `type =~ 'microsoft.security/assessments'`: `properties.status.code` (Healthy|Unhealthy|NotApplicable), `properties.displayName`, `properties.metadata.severity` (Low|Medium|High). The row `id` is the **assessment** id (`{resourceId}/providers/Microsoft.Security/assessments/{name}`). The target-resource field casing in ARG is unverified.
- `securityresources`, `type =~ 'microsoft.security/pricings'`: `name` is the plan (VirtualMachines, CloudPosture, …), `properties.pricingTier` (Free|Standard).
- ARG filters by RBAC silently: an inaccessible subscription returns no rows, not an error. Zero rows never proves "no findings".

## Scope decisions

- Preflights are advisory; the token and approval logic do not change. ARM still enforces policy and quota at deployment, and the review says so.
- Preflights cover only what the plain what-if covered: the batch at the target scope for staged plans. Management-group and tenant targets get a note, not a check. The stack what-if RPC is not changed (the drawer runs the plain what-if first in stack mode, so the results still appear).
- Quota: vCPUs for VMs, scale sets, AKS clusters and agent pools, plus public IPs. Spot, App Service workers, Container Apps and Container Instances are not checked (note).
- Governance: Advisor, Policy (non-compliant only), Defender assessments (unhealthy only) and Defender plan tiers. **Attack paths are out of scope**: they need Defender CSPM plus agentless scanning and their entities are not ARM ids.
- No permission probes are added to `PermissionPreflight`.

## Global Constraints

- Package versions only in `Directory.Packages.props`; no new packages; **no new project references** (lock files are restored in locked mode in CI). Warnings are errors in app projects.
- `AzureDesignStudio.Workspace` stays storage-neutral: it takes plain row records and checkpoint JSON strings, never Discovery types.
- Services read the caller with `ServiceTools.GetUser(context)` / `GetUserId`, never `GetHttpContext()`. gRPC clients only through `GrpcClientFactory`.
- AntDesign 1.x enum values, not raw strings; no `async void`.
- The MCP server stays read-only (`ReadOnly = true`, read-only SQLite).
- Workspace schema changes are additive, guarded DDL in `WorkspaceDbContext.EnsureAdditiveTables` / `EnsureAddedColumns`. Never re-create or drop user tables.
- `ScanEngine.RequestKeyOf` must not change (old checkpoints must still resume). The existing manifest members (`groupsCompleted`, `groupsFailed`, `rows`, `truncated`) keep their names and meaning.
- Preflight engines and governance parsing never throw except `OperationCanceledException` from the **caller's** token; every other failure, including the engine's own deadline, is a note with the findings gathered so far.
- **Secrets never go to Azure Policy** — `PolicyPreflight.Scrub` applies all of these to `resourceContent` before sending:
  1. Value-based: every string leaf that contains the value of a `secureString` parameter, or any string leaf of a `secureObject` parameter (values from the request parameters JSON, else the template `defaultValue`), is removed.
  2. Container-based: drop `protectedSettings`, `customData`, `sharedKey`, `secureValue`, `connectionString`, `connectionStrings`, `adminPassword`, and in any array of `{ name, value }` objects (`appSettings`, `environmentVariables`, `env`) drop `value` and keep `name`.
  3. Name-based, **string leaves only** (booleans and numbers are never removed): keys containing `password`, `secret`, `connectionstring`, `accountkey`, `accesskey`, `token` or `sas` (case-insensitive).
  4. Pattern-based: string leaves containing `AccountKey=`, `SharedAccessKey=`, `SharedAccessSignature=`, `sig=` or `-----BEGIN`.
  5. Types whose content is a secret are not checked at all, with a note: `Microsoft.KeyVault/vaults/secrets`, `Microsoft.KeyVault/vaults/keys`, `Microsoft.Web/sites/config`, `Microsoft.Web/sites/slots/config`, `Microsoft.ApiManagement/service/namedValues`.
  Deployment parameters themselves are never sent.
- ARM ids are matched case-insensitively everywhere (`/resourceGroups/` and `/resourcegroups/` both occur).
- Every ARM fixture uses the shapes in "Facts verified" above.
- Do not commit lock-file drift, `.DS_Store`, or anything under `.superpowers/`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Commands: `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`, `dotnet test src/AzureDesignStudio.Server.Tests`, `dotnet build src/AzureDesignStudio.Mcp`, `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst` (revert any `packages.maccatalyst.lock.json` change), `node --test src/AzureDesignStudio/JsTests/*.test.cjs`.

## Review Focus

1. **A preflight that cannot run** (no permission, throttled, network failure, deadline, malformed JSON) leaves the what-if result and its token exactly as before, keeps the findings already gathered, and adds one plain note; it never turns into "no findings" silently.
2. **Secrets**: no secret value — `adminPassword`, an App Service `AccountKey=` connection string, an extension's `protectedSettings`, a secure parameter's value — ever appears in a `checkPolicyRestrictions` request body; non-secret booleans such as `disablePasswordAuthentication` survive.
3. **Quota arithmetic**: Modify rows count only the increase; spot, unknown sizes and SKUs without a vCPU count never produce a false "Ok".
4. **A governance source that fails** marks only that source unavailable; the scan stays `Completed`, its content hash and resource count are unchanged, and a resumed scan neither re-runs completed source groups nor keeps a stale "unavailable" after a retry succeeds.
5. **"Not collected" vs "no findings"**: a scan taken before this change, a source that was not requested, one that is incomplete, and one that failed each read differently from "collected, zero rows" in the drawer, both reports and MCP.

---

### Task 1: WhatIf RPC test harness

`DeployService.WhatIf` looks up the resource group through an `ArmClient` with no scriptable transport, so Server.Tests has no WhatIf RPC test, and the existing fakes cannot script 403s, hangs or body assertions.

**Files:** `src/AzureDesignStudio.Services/DeployService.cs`; `src/AzureDesignStudio.Server.Tests/TestSupport.cs`; `src/AzureDesignStudio.Server.Tests/WhatIfRpcTests.cs` (create).

- [ ] Route the resource-group lookup in `WhatIf` (and `CreateDeployment`, which repeats it) through the factory `"arm"` HttpClient the same way `PrepareStackAsync` does, with no behaviour change.
- [ ] In `TestSupport.cs` add a route-table handler (also usable as `IHttpClientFactory`): routes match method + path (case-insensitive) and answer a status and body, or wait on an awaitable that honours the request's cancellation token; it records method, path, query and body of every request. `StackRpcTests` may keep its own fake; do not break it.
- [ ] Script the what-if POST to answer 200 with the result body directly (no 202 polling in tests).
- [ ] Tests: `WhatIf_ResourceGroupTarget_ReturnsTokenAndChanges`, `WhatIf_UnknownResourceGroup_Answers404`, `WhatIf_NoCredential_Answers404`.
- [ ] Commit `test(deploy): scriptable what-if RPC tests`.

### Task 2: Policy preflight engine

**Files:** `src/AzureDesignStudio.Discovery/Preflight/PolicyPreflight.cs` (create); `src/AzureDesignStudio.Core.Tests/PolicyPreflightTests.cs` (create; fake-handler style of `WhatIfTests.cs`).

**Interfaces:**

```csharp
public enum PolicyVerdict { Deny, Audit, Other }
public sealed record PolicyFinding(string ResourceId, PolicyVerdict Verdict, string Effect,
    string? PolicyAssignmentId, string? PolicyDefinitionId, string? PolicyDefinitionReferenceId, string Reason);
public sealed record PolicyPreflightResult(IReadOnlyList<PolicyFinding> Findings, int Checked, int NotChecked, IReadOnlyList<string> Notes);

public sealed class PolicyPreflight(ArmTransport transport)
{
    public const string ApiVersion = "2024-10-01";
    public const int MaxResources = 50;
    public const int MaxConcurrency = 3;
    public static IReadOnlyList<string> Caveats { get; }
    public Task<PolicyPreflightResult> CheckAsync(IReadOnlyList<WhatIfChange> changes, string templateJson, string? parametersJson,
        TimeSpan budget, CancellationToken ct);
    public static IReadOnlySet<string> SecureValues(string templateJson, string? parametersJson);          // pure
    public static string? BuildRequest(string resourceId, string afterJson, string? templateJson,
        IReadOnlySet<string> secureValues, out string? skipReason);                                         // pure
    public static IReadOnlyList<PolicyFinding> ParseResult(string resourceId, string responseJson);       // pure
}
```

**Behaviour:**
- Candidates: `Create` and `Modify` changes with a non-empty `AfterJson` and a `ResourceId` under `/subscriptions/{sub}/…`, ordered by `ResourceId` (ordinal); the first `MaxResources` are checked, the rest counted as not checked with one note.
- Endpoint per change, parsed segment by segment case-insensitively: the resource-group form when the id has a `resourceGroups` segment, else the subscription form.
- `resourceContent`: from the after payload keep only `type`, `name`, `location`, `tags`, `sku`, `kind`, `identity`, `zones`, `plan`, `extendedLocation`, `properties`; then apply the secret rules in Global Constraints. `apiVersion`: the after payload's `apiVersion` if present, else the first template resource whose `type` matches (case-insensitive), else skip with a note. For a child resource, `scope` = the parent id derived from `resourceId` (drop the last two segments). `includeAuditEffect: true`. No `pendingFields`.
- Parse: one finding per `contentEvaluationResult.policyEvaluations[]` entry — ids from `policyInfo`; `Effect` = `effectDetails.policyEffect`; `Verdict` Deny or Audit when the effect equals that word (case-insensitive), else Other; `Reason` = `evaluationDetails.reason`, else the first evaluated expression as "{path} {operator} {targetValue}", else "Policy evaluation reported a violation". An `evaluationResult` other than `NonCompliant` gives `Verdict.Other` with reason "evaluation result '<value>'". `fieldRestrictions` are ignored. Duplicates removed; ordered by ResourceId, Verdict, PolicyAssignmentId, PolicyDefinitionReferenceId (ordinal).
- Concurrency: at most `MaxConcurrency` requests in flight. Budget: an internal deadline of `budget` from the start; when it passes, no new requests start, in-flight ones are cancelled by the deadline (not by `ct`), and the result carries the findings gathered so far plus the note "Policy check stopped at the time limit: N of M resources not checked."
- Failures: a 403 stops further calls with the note "No permission to check Azure Policy (needs Microsoft.PolicyInsights/checkPolicyRestrictions/read)."; a 429 that the transport gives up on, or any other HTTP/JSON failure, counts that resource as not checked with a note naming it and the ARM error code; only `OperationCanceledException` from `ct` propagates.
- `Caveats`: Azure Policy is enforced by ARM at deployment; this check evaluates each resource on its own with the values what-if predicts; assignments inherited from management groups and effects such as Modify, Append and DeployIfNotExists may not be reported; secret values are removed before checking, so a policy on them is not evaluated; only the previewed batch is checked.

- [ ] **Tests first:** `Scrub_VmAdminPassword_Removed_DisablePasswordAuthenticationKept`; `Scrub_FunctionAppAppSettingsValues_Removed_NamesKept` (an `AccountKey=` connection string); `Scrub_ProtectedSettingsAndCustomData_Removed`; `Scrub_SecureParameterValue_RemovedWherever_ItAppears` (value embedded in a longer string too); `Scrub_SecureObjectLeaves_Removed`; `Scrub_NonSecretBooleansAndNumbers_Kept` (AKS `addonProfiles.azureKeyvaultSecretsProvider.enabled`); `BuildRequest_SecretTypes_AreSkipped` (Key Vault secret, APIM named value); `BuildRequest_KeepsOnlyResourceMembers`; `BuildRequest_ApiVersionFromAfter_ThenTemplate_ThenSkip`; `BuildRequest_ChildResource_SetsParentScope` (subnet, lower-case `resourcegroups`); `ParseResult_DenyFromEvaluation`; `ParseResult_AuditFromEvaluation`; `ParseResult_FieldRestrictionsAlone_GiveNoFindings`; `ParseResult_EvaluationResultError_IsOther`; `Check_RgAndSubscriptionEndpoints_CaseInsensitive`; `Check_Forbidden_StopsWithOneNote`; `Check_OneFailure_SkipsOnlyThatResource`; `Check_CapsAtMaxResources`; `Check_NeverMoreThanThreeInFlight`; `Check_BudgetExpires_KeepsFindingsGathered` (five answer, one hangs); `Check_DeleteAndNoChangeRows_AreIgnored`; `Check_CallerCancelled_Propagates`.
- [ ] Implement; full offline suite; commit `feat(discovery): Azure Policy preflight from what-if payloads`.

### Task 3: Policy preflight in the review

**Files:** `src/AzureDesignStudio.SharedModels/deploy_model.proto`; `src/AzureDesignStudio.Services/DeployService.cs`; `src/AzureDesignStudio/Services/DeployGrpcService.cs` (only if it maps fields); `src/AzureDesignStudio/Components/MenuDrawer/CodeDrawerTemplate.razor(.cs)`; tests in `src/AzureDesignStudio.Server.Tests/WhatIfRpcTests.cs`.

**Proto (append to `WhatIfResponse`):** `repeated PolicyFlag policy_flags = 13; repeated string policy_notes = 14;` and `message PolicyFlag { string resource_id = 1; string verdict = 2; string effect = 3; string policy_assignment_id = 4; string policy_definition_id = 5; string policy_definition_reference_id = 6; string reason = 7; }`. `policy_notes` carries the engine's notes followed by its caveats.

**Service:** after `WhatIfClient.WhatIfAsync` succeeds (and only then), run `PolicyPreflight.CheckAsync` with the request's template and parameters and a budget of `DeployService.PreflightBudget` (an `internal` settable property defaulting to 30 seconds, so tests can shorten it), on its **own** `ArmTransport` instance built from the same `"arm"` HttpClient and credential (so quota calls do not queue behind policy calls). Management-group and tenant targets: skip with the note "Policy is not checked for management-group or tenant deployments." The token, guardrails, plan hash and status codes are unchanged; the preflight never changes `status_code`.

**Drawer:** a "Policy" section under the guardrail banner: Deny findings first (red tag "Would be denied"), then Audit (orange "Audit"), then Other (default "Check"), each with the resource name (last id segment), the assignment name (last segment of the assignment id) and the reason; the notes as a muted list. A per-row tag "Policy: denied" next to what-if rows whose id has a Deny finding (case-insensitive). One line of copy: "Azure enforces policy when you deploy; this is an early warning."

- [ ] **Tests first (Server.Tests):** `WhatIf_PolicyDeny_IsReturned_AndTokenStillIssued`; `WhatIf_PolicyForbidden_AddsNote_TokenUnchanged`; `WhatIf_WhatIfFails_NoPolicyCall` (zero checkPolicyRestrictions requests); `WhatIf_ManagementGroupTarget_SkipsPolicyWithNote`; `WhatIf_PolicyRequestBody_HasNoSecrets` (VM with a secure `adminPassword` parameter: the recorded body contains neither the value nor `adminPassword`).
- [ ] Implement; Server + Core suites; Desktop build; commit `feat(deploy): show Azure Policy preflight in the deployment review`.

### Task 4: Quota preflight engine

**Files:** `src/AzureDesignStudio.Discovery/Preflight/QuotaPreflight.cs` (create); `src/AzureDesignStudio.Core.Tests/QuotaPreflightTests.cs` (create).

**Interfaces:**

```csharp
public sealed record ComputeDemand(string Location, string VmSize, int Instances);   // location normalized: lower case, no spaces; Instances < 0 = released by a Modify
public sealed record IpDemand(string Location, string Sku, int Count);
public sealed record QuotaDemandSet(IReadOnlyList<ComputeDemand> Compute, IReadOnlyList<IpDemand> PublicIps, IReadOnlyList<string> Notes);
public enum QuotaStatus { Ok, NearLimit, Exceeds, Unknown, SkuUnavailable }
public sealed record QuotaFinding(string Location, string QuotaName, string DisplayName, long Current, long Limit, long Requested,
    QuotaStatus Status, string? VmSize, string? Detail);
public sealed record QuotaPreflightResult(IReadOnlyList<QuotaFinding> Findings, IReadOnlyList<string> Notes);

public sealed class QuotaPreflight(ArmTransport transport)
{
    public const string ComputeUsagesApiVersion = "2026-04-01";
    public const string SkusApiVersion = "2021-07-01";
    public const string NetworkUsagesApiVersion = "2026-01-01";
    public const double NearLimitRatio = 0.9;
    public static IReadOnlyList<string> Caveats { get; }
    public static QuotaDemandSet DemandFrom(IReadOnlyList<WhatIfChange> changes);                     // pure
    public Task<QuotaPreflightResult> CheckAsync(string subscriptionId, IReadOnlyList<WhatIfChange> changes, TimeSpan budget, CancellationToken ct);
}
```

**Demand (pure, from `AfterJson`, minus `BeforeJson` on Modify):**
- `Microsoft.Compute/virtualMachines`: `properties.hardwareProfile.vmSize`; Create → +1. Modify with a size change → +1 new size and −1 old size (so regional and same-family checks see only the vCPU difference).
- `Microsoft.Compute/virtualMachineScaleSets`: `sku.name` × `sku.capacity`; Modify → +after capacity of the new size and −before capacity of the old size.
- `Microsoft.ContainerService/managedClusters`: each `properties.agentPoolProfiles[]` → `vmSize` × (`maxCount` when `enableAutoScaling` is true, else `count`); Modify subtracts the before pool with the same `name`.
- `Microsoft.ContainerService/managedClusters/agentPools`: `properties.vmSize` × the same count rule; Modify subtracts the before values.
- Spot — VM/VMSS `properties.priority` or `properties.virtualMachineProfile.priority`, AKS `scaleSetPriority` — equal to `Spot`: no demand; note "Spot quota is a separate pool and is not checked."
- `Microsoft.Network/publicIPAddresses`: Create → 1 of `sku.name` (default `Standard`).
- Missing `location` or size: no demand; note naming the resource. Demands are summed per (location, size) and (location, sku); entries summing to zero are dropped; ordered by location then size (ordinal).

**Check:** per location with demand: the SKU list → `resourceType == "virtualMachines"` with `name` equal to the size (case-insensitive). Absent → `SkuUnavailable` "not offered in {location}". A `Location` restriction whose `values` or `restrictionInfo.locations` contain the location → `SkuUnavailable` "not available to this subscription in {location}". Zone restrictions → one note (zones are not checked). vCPUs = `capabilities[vCPUs]` parsed as an integer; family = `family`. A missing or unparseable vCPU count makes that family's check and the regional `cores` check `Unknown` with detail "SKU reports no vCPU count". Compute usages → per family: `Current = currentValue`, `Limit = limit`, `Requested = max(0, Σ instances × vCPUs)`; the regional `cores` check likewise over all families. A family with no usage row → `Unknown` with detail "no usage entry named '{family}'". Network usages: Standard IPs against `StandardSkuPublicIpAddresses` when present, else `PublicIPAddresses`; Basic against `PublicIPAddresses`. Status: `Exceeds` when `Current + Requested > Limit`; `NearLimit` when `Current + Requested >= NearLimitRatio × Limit`; else `Ok`. Order: SkuUnavailable, Exceeds, NearLimit, Unknown, Ok; then location, quota name.
- Budget and failures as in `PolicyPreflight`: deadline keeps what was gathered and marks the rest `Unknown` with a note; 403 → note "No permission to read quotas (needs Microsoft.Compute/locations/usages/read, Microsoft.Compute/skus/read and Microsoft.Network/locations/usages/read)." and that location's checks become `Unknown`; other failures likewise with the ARM error code. No demand → no calls. Never a false `Ok`.
- `Caveats`: quota is not capacity (Azure can still be out of capacity); limits are per subscription per region; AKS upgrades temporarily use extra vCPUs; the family-to-quota mapping follows the Resource SKUs `family` name; only the previewed batch is counted.

- [ ] **Tests first:** demand tests for each rule (VM create; VM resize gives +new/−old; VMSS capacity increase and decrease; AKS autoscale uses maxCount; agentPools child; VM, VMSS and AKS spot skipped with note; missing location noted; public IP); check tests: `Check_FamilyExceeds`, `Check_RegionalCoresNearLimit`, `Check_ResizeWithinFamily_RequestsOnlyTheDifference`, `Check_SizeNotOffered_IsSkuUnavailable`, `Check_LocationRestriction_IsSkuUnavailable`, `Check_MissingVcpus_IsUnknown`, `Check_NoUsageRow_IsUnknown`, `Check_StandardIpFallsBackToPublicIPAddresses`, `Check_Forbidden_AllUnknownWithNote`, `Check_FollowsNextLink`, `Check_NoDemand_MakesNoCalls`, `Check_BudgetExpires_RestUnknown`.
- [ ] Implement; full offline suite; commit `feat(discovery): quota and SKU availability preflight`.

### Task 5: Quota preflight in the review

**Files:** as Task 3.

**Proto:** `repeated QuotaFlag quota_flags = 15; repeated string quota_notes = 16;` with `message QuotaFlag { string location = 1; string quota_name = 2; string display_name = 3; int64 current = 4; int64 limit = 5; int64 requested = 6; string status = 7; string vm_size = 8; string detail = 9; }` (`status` is the enum name). The Discovery record is `QuotaFinding`, so no simple-name clash with the proto type.

**Service:** run concurrently with the policy preflight on its own `ArmTransport`, same `PreflightBudget`; subscription id from the target scope (resource-group and subscription targets only; otherwise note "Quota is not checked for management-group or tenant deployments."). `quota_notes` = engine notes then caveats.

**Drawer:** a "Quota" section listing non-`Ok` findings first (`Exceeds` and `SkuUnavailable` red, `NearLimit` orange, `Unknown` default) as "{DisplayName} in {location}: {current} used + {requested} requested of {limit}" (or the detail for SKU findings), the `Ok` ones collapsed under "N quotas have room"; notes muted. Copy: "Quota is checked again by Azure when you deploy."

- [ ] **Tests first (Server.Tests):** `WhatIf_QuotaExceeds_IsReturned_TokenStillIssued`; `WhatIf_PolicyAndQuotaBothReturned`; `WhatIf_PreflightBudget_KeepsPartialResults` (policy: five answer, one hangs; quota usages hang; short budget; findings for the five plus both notes; token issued).
- [ ] Implement; suites; Desktop build; commit `feat(deploy): show the quota preflight in the deployment review`.

### Task 6: Governance model, queries and normalizer

**Files:** `src/AzureDesignStudio.Discovery/Governance/GovernanceFinding.cs`, `GovernanceQueries.cs`, `GovernanceNormalizer.cs` (create); `src/AzureDesignStudio.Core.Tests/GovernanceTests.cs` (create; `ChangeFeedTests.cs` is the style).

**Interfaces:**

```csharp
public enum GovernanceSource { Advisor, Policy, Defender }
public enum GovernanceSeverity { Low, Medium, High }
public sealed record GovernanceFinding(GovernanceSource Source, string RecordId, string ResourceId, string SubscriptionId,
    GovernanceSeverity Severity, string Category, string Title, string? Detail, string? PolicyAssignmentId, string? PolicyDefinitionId);
public sealed record DefenderPlan(string SubscriptionId, string Plan, string Tier);
public static class GovernanceSources { public const string Advisor = "advisor", Policy = "policy", Defender = "defender";
    public static IReadOnlyList<string> All { get; } }   // ["advisor","policy","defender"]
public static class GovernanceQueries
{
    public static string Advisor(IReadOnlyList<string> subscriptionIds);
    public static string Policy(IReadOnlyList<string> subscriptionIds);
    public static string DefenderAssessments(IReadOnlyList<string> subscriptionIds);
    public static string DefenderPlans(IReadOnlyList<string> subscriptionIds);
}
public static class GovernanceNormalizer
{
    public static GovernanceFinding? FromAdvisorRow(JsonElement row);
    public static GovernanceFinding? FromPolicyRow(JsonElement row);
    public static GovernanceFinding? FromDefenderRow(JsonElement row);
    public static DefenderPlan? FromPlanRow(JsonElement row);
    public static IReadOnlyList<GovernanceFinding> Dedupe(IEnumerable<GovernanceFinding> findings);
}
public static class GovernanceCaveats { public static IReadOnlyList<string> All { get; } }
```

**Queries** (subscription filter written exactly as `ScanEngine.BuildQuery` writes it; every projected column is `tostring(...)` or a string column; each ends `| order by id asc`; no `take`/`limit`):
- Advisor: `advisorresources | where type =~ 'microsoft.advisor/recommendations' | where subscriptionId in (…) | project id, subscriptionId, resourceId = tostring(properties.resourceMetadata.resourceId), source = tostring(properties.resourceMetadata.source), category = tostring(properties.category), impact = tostring(properties.impact), problem = tostring(properties.shortDescription.problem), solution = tostring(properties.shortDescription.solution), status = tostring(properties.recommendationStatus), suppressions = tostring(properties.suppressionIds)`.
- Policy: `policyresources | where type =~ 'microsoft.policyinsights/policystates' | where subscriptionId in (…) | where tostring(properties.complianceState) =~ 'NonCompliant' | project id, subscriptionId, resourceId = tostring(properties.resourceId), assignmentId = tostring(properties.policyAssignmentId), assignmentName = tostring(properties.policyAssignmentName), definitionId = tostring(properties.policyDefinitionId), referenceId = tostring(properties.policyDefinitionReferenceId)`.
- Defender assessments: `securityresources | where type =~ 'microsoft.security/assessments' | where subscriptionId in (…) | where tostring(properties.status.code) =~ 'Unhealthy' | project id, subscriptionId, targetId = tostring(coalesce(properties.resourceDetails.Id, properties.resourceDetails.id)), name = tostring(properties.displayName), severity = tostring(properties.metadata.severity)`.
- Defender plans: `securityresources | where type =~ 'microsoft.security/pricings' | where subscriptionId in (…) | project id, subscriptionId, plan = tostring(name), tier = tostring(properties.pricingTier)`.

**Normalizing:**
- Advisor: skip rows whose `status` is Dismissed, Postponed or Completed (case-insensitive), rows whose `suppressions` is non-empty and not `[]`, and rows whose `source` contains `/providers/Microsoft.Security/assessments/` (Defender reports those). Severity from `impact` (High/Medium/Low; else Medium). Category = `category`. Title = `problem` (else "Advisor recommendation"); Detail = `solution`.
- Policy: Category "Policy", Severity Medium, Title = `assignmentName` (else the last segment of `assignmentId`), Detail = `referenceId` when present. RecordId = row `id` when non-empty, else `"policy:" + lower(resourceId|assignmentId|definitionId|referenceId)`.
- Defender: ResourceId = `targetId` when it starts with `/subscriptions/` (case-insensitive), else the part of the row `id` before `/providers/Microsoft.Security/assessments/` (case-insensitive). Category "Security", Severity from `severity`, Title = `name`.
- Any row without a resource id → null. Dedupe on (Source, ResourceId case-insensitive, Title, PolicyAssignmentId, PolicyDefinitionId, Detail); ordered by ResourceId, Source, Severity descending, Title (ordinal).
- `GovernanceCaveats.All`: Resource Graph hides subscriptions and resources the scanning identity cannot read, so zero findings is not proof of none; policy compliance lags changes by about 15 minutes and full evaluation runs every 24 hours; Defender recommendations depend on which plans are on; Advisor recommendations refresh on Advisor's schedule, and dismissed or postponed recommendations may still be listed when Resource Graph does not carry their status.

- [ ] **Tests first:** one query test per builder (table, type filter, subscription filter, `order by id asc`, no `take`/`limit`, every projected column wrapped in `tostring` or a known string column); normalizer tests for each rule above, including `Defender_TargetFromAssessmentId_WhenResourceDetailsMissing`, `Advisor_SkipsDefenderSourcedDismissedAndSuppressed`, `Policy_RecordIdFallback`, `Dedupe_IsCaseInsensitiveOnResourceId`, `Plans_ParseTier`, and a malformed row → null (never throws).
- [ ] Implement; offline suite; commit `feat(discovery): governance findings from Advisor, Policy and Defender rows`.

### Task 7: Scan engine runs governance sources; public scan manifest

**Files:** `src/AzureDesignStudio.Discovery/Scan/ScanEngine.cs`; `src/AzureDesignStudio.Discovery/Scan/ScanManifest.cs` (create); `src/AzureDesignStudio.Desktop/WorkspaceScanSink.cs` (switch to `ScanManifest.Build` with no behaviour change yet); tests in `src/AzureDesignStudio.Core.Tests/ScanEngineTests.cs` and `ScanManifestTests.cs` (create).

**Interfaces:**
- `ScanRequest` gains `IReadOnlyList<string>? Sources = null` (values from `GovernanceSources`). `RequestKeyOf` unchanged.
- `ScanCheckpoint` gains `List<string> RequestedSources`, `Dictionary<string, int> SourceRows`, `Dictionary<string, string> FailedSourceGroups` (key `"{source}:{groupKey}"` → reason; an entry is removed when that group later succeeds) and `List<DefenderPlan> DefenderPlans` (merged per subscription + plan, newest wins). All default to empty, so old checkpoint JSON still loads. Completed source groups are recorded in `CompletedQueries` as `"{source}:{groupKey}"`.
- `IScanSink` gains a default-implemented `Task SourcePagePersistedAsync(string source, string queryKey, IReadOnlyList<GovernanceFinding> findings, ScanCheckpoint checkpoint, CancellationToken ct) => Task.CompletedTask;`.
- `ScanManifest` (public, Discovery): `int GroupsCompleted` (inventory keys only — keys without `:`), `int GroupsFailed`, `int Rows`, `bool Truncated`, `IReadOnlyDictionary<string, SourceState>? Sources`, `IReadOnlyList<DefenderPlan>? DefenderPlans`; `public sealed record SourceState(string State, int Rows, string? Detail)` with `State` one of `"collected"`, `"incomplete"`, `"unavailable"`, `"not-requested"`; `static ScanManifest Build(string status, ScanCheckpoint checkpoint, int totalGroups)`; `static ScanManifest? Read(string? json)`; `string ToJson()` (camelCase, nulls omitted — the four existing members keep their JSON names). `Read` of a manifest without `sources` returns `Sources = null`, which every reader shows as "not collected (scan predates governance collection)". State derivation per requested source: any `FailedSourceGroups` entry → unavailable (Detail = first reason by key order); else all its groups completed → collected; else incomplete. Not requested → not-requested.

**Behaviour:** for each subscription group and each requested source, one extra worker (Defender runs both its queries in that worker; plans go into the checkpoint, assessments to the sink). Rows are normalized and deduped, then persisted under the same `persistGate` with the engine's existing order: add the source key to `CompletedQueries` and update `SourceRows`/`DefenderPlans`, call `SourcePagePersistedAsync`, and roll those changes back if the sink throws. A source error or truncation records `FailedSourceGroups` and never touches `FailedQueries`, `AnyTruncated`, the scan status, `Rows` or the content hash. `RequestedSources` is recorded at start (and kept on resume). Progress totals include source workers. Cancellation behaves as for inventory groups.

- [ ] **Tests first:** `Sources_NotRequested_NoExtraQueries`; `Sources_Requested_RowsReachTheSink`; `SourcePage_CheckpointNamesTheSourceGroup` (the checkpoint handed to the sink already contains the source key); `SourceSinkThrows_RollsBack`; `SourceFailure_MarksOnlyThatSourceUnavailable_ScanCompleted`; `SourceTruncation_MarksUnavailable`; `Resume_SkipsCompletedSourceGroups`; `Resume_RetrySucceeds_ClearsFailure`; `ContentHashAndResourceCount_UnchangedBySources`; `OldCheckpointWithoutSourceFields_StillResumes`; manifest tests: four existing members unchanged for a scan without sources, each state, `GroupsCompleted` ignores source keys, legacy manifest reads with `Sources == null`, plans round-trip.
- [ ] Implement; offline suite; Desktop build; commit `feat(discovery): tenant scans collect governance sources`.

### Task 8: Workspace stores governance findings

**Files:** `src/AzureDesignStudio.Workspace/WorkspaceEntities.cs`, `WorkspaceDbContext.cs`, `WorkspaceScans.cs`; `src/AzureDesignStudio.Desktop/WorkspaceScanSink.cs`; tests in `src/AzureDesignStudio.Core.Tests/WorkspaceTests.cs`.

**Interfaces:**
- `public sealed record GovernanceFindingRow(string Source, string RecordId, string ResourceId, string SubscriptionId, string Severity, string Category, string Title, string? Detail, string? PolicyAssignmentId, string? PolicyDefinitionId)` in Workspace (plain strings).
- Entity `GovernanceFindingRecord`, table `GovernanceFindings`, primary key `(ScanId, Source, RecordId)`, index `(ScanId, ResourceId)`, created by guarded DDL in `EnsureAdditiveTables`.
- `WorkspaceScans.PersistSourcePageAsync(string scanId, IReadOnlyList<GovernanceFindingRow> rows, string checkpointJson, CancellationToken ct = default)` — upsert rows and the checkpoint in one transaction; `ResourceCount` unchanged.
- `WorkspaceScans.GetGovernanceAsync(string scanId, CancellationToken ct = default) → IReadOnlyList<GovernanceFindingRow>`.
- `WorkspaceScanSink` implements `SourcePagePersistedAsync` (maps `GovernanceFinding` → row; enum names as strings) and builds the manifest with `ScanManifest.Build` (total groups known from the request); a source failure never goes into the scan `Error`.

- [ ] **Tests first:** `GovernanceTable_CreatedOnExistingWorkspace` (open a workspace created without the table; it appears; existing rows intact); `PersistSourcePage_UpsertsAndKeepsResourceCount`; `PersistSourcePage_WritesTheCheckpoint`; `GetGovernance_ReturnsOnlyThatScan`.
- [ ] Implement; offline suite; Desktop build; commit `feat(workspace): store governance findings with each scan`.

### Task 9: MCP tool `ads_governance`

**Files:** `src/AzureDesignStudio.Mcp/AdsWorkspace.cs`, `AdsMcpTools.cs`; `src/AzureDesignStudio.Discovery/Mcp/AdsToolEngine.cs` (+ views file); tests in `src/AzureDesignStudio.Core.Tests/AdsToolEngineTests.cs`.

- `AdsWorkspace.ListGovernanceAsync(scanId)` guarded by `HasTableAsync("GovernanceFindings")` (missing table → empty list plus a flag); maps rows to `GovernanceFinding` (unknown enum strings → the row is skipped and counted as malformed).
- `AdsToolEngine.Governance(ScanManifest? manifest, IReadOnlyList<GovernanceFinding> rows, int malformed, string? source, string? resourceId, int limit) → GovernanceReport { Sources (state per source; "not collected" for a legacy manifest), CountsBySource, CountsBySeverity, Rows, Total, Truncated, Caveats }`; `limit` clamped 1..1000, default 200; `resourceId` matched case-insensitively.
- Tool `ads_governance(scanId, source?, resourceId?, limit?)`, `ReadOnly = true`, description: "Governance findings stored with one scan: Advisor recommendations, non-compliant Azure Policy states and unhealthy Defender for Cloud assessments, per resource, with the collection state of each source. Reads only the local workspace; never contacts Azure." Unknown scan id → `McpException` like the other scan tools.
- Update the tool count in the `AdsMcpTools` doc comment and AGENTS.md (14 → 15).

- [ ] **Tests first:** engine tests for filtering, clamping, truncation, each source state, legacy manifest, counts, malformed rows reported in caveats.
- [ ] Implement; offline suite; `dotnet build src/AzureDesignStudio.Mcp`; commit `feat(mcp): ads_governance returns governance findings for a scan`.

### Task 10: Desktop shows governance findings

**Files:** `src/AzureDesignStudio.Desktop/Components/GovernanceFindingsView.razor` (create — the first component split out of the scan drawer); `TenantScanDrawerTemplate.razor`; `TopologyView.razor`; `src/AzureDesignStudio.Desktop/TenantScanSession.cs`; `src/AzureDesignStudio.Discovery/Report/ReportModel.cs`, `ReportHtml.cs`, `ReportBundle.cs` / `ControlsMap.cs`; tests in Core.Tests for the report parts.

- **Scan option:** a checkbox "Collect Advisor, Policy and Defender findings" (default on) next to the scan button; `TenantScanSession` passes `GovernanceSources.All` when ticked.
- **Governance tab** (`("governance", "Governance")`): `GovernanceFindingsView` takes `[Parameter]`s for the findings, the manifest source states (`null` = legacy), the Defender plans and the caveats (the drawer owns scan selection and loading, as with `TopologyView`). It shows a status line per source ("Collected: 42 findings" / "Incomplete: the scan stopped before this source finished" / "Unavailable: <reason>" / "Not requested for this scan" / "Not collected: this scan predates governance collection"), filters by source and severity, findings grouped by resource (name = last id segment, full id in a tooltip), Defender plan tiers, caveats.
- **Topology:** new `[Parameter] IReadOnlyDictionary<string, IReadOnlyList<GovernanceFinding>>? Findings` (case-insensitive ARM id keys); the component computes the count and top severity per node, draws a small badge in the left gutter (colour by top severity), and lists the node's findings in the inspector after Connections.
- **Reports:** `ReportModel` gains init-only `IReadOnlyList<GovernanceFinding>? Governance { get; init; }` and `IReadOnlyDictionary<string, SourceState>? GovernanceSources { get; init; }` (both null = not collected); `ReportHtml` section and Summary row show per-source states; report.json carries both; controls CSV rows with kinds `advisor-<category>`, `policy-noncompliant`, `defender-assessment` (UNMAPPED unless `ControlsMap` already maps them); the Markdown report (`SaveReportAsync`) gets a governance section with the same per-source states; both filled in `CollectReportSectionsAsync`.

- [ ] **Tests first (Core.Tests):** report JSON carries `governance` and `governanceSources` only when collected; HTML section and summary for collected-zero, unavailable, not-requested and legacy; CSV kinds.
- [ ] Implement; offline suite; Desktop build; commit `feat(desktop): governance findings in the scan drawer, topology and reports`.

### Task 11: Documentation

- `AGENTS.md`: preflight notes (where it runs, advisory, the budget, secret scrubbing rules, verdicts only from `policyEvaluations`), governance sources (tables, unavailable ≠ partial, `GovernanceFindings` table, `ScanManifest` is the one manifest shape, attack paths out of scope), MCP tool count 15, WHERE TO LOOK rows.
- `docs/feature-plan-2026-11.md`: mark B2, B3, B4 shipped with what shipped and the scope decisions above.
- `CHANGELOG.md` entry; `.planning/codebase/INTEGRATIONS.md` / `ARCHITECTURE.md` tool count and the new ARM endpoints.
- [ ] Commit `docs: Wave B2-B4 policy, quota and governance`.

## Not in this plan

- Attack paths; permission probes in `PermissionPreflight`; blocking deployments on a preflight result; policy or quota checks in the stack what-if RPC; App Service, Container Apps, Container Instances and spot quotas; decomposing the rest of the scan drawer.
