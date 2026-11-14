# Wave A: Export Correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Exported designs state their outbound internet behaviour explicitly, cannot use retired SKUs unnoticed, and stack deployments are previewed with the stacks what-if that shows detach/delete and deny effects.

**Architecture:** Three independent slices. A1 lives in `Core` (subnet and NAT gateway export, one validator rule). A2 adds a SKU dimension to the scan-side `DeprecationCatalog` in `Discovery` and a matching rule in `Core`'s `DesignValidator`. A3 adds a stacks what-if client and parser in `Discovery`, one new RPC, and makes `CreateStack` accept only a token issued by that RPC.

**Tech Stack:** .NET 10, xUnit, System.Text.Json, protobuf/gRPC (in-process on Desktop), Blazor + AntDesign.

**Spec:** `docs/feature-plan-2026-11.md`, section "Wave A". Facts below were checked on Microsoft Learn on 2026-11-13 and override that document where they differ.

## Facts verified on Microsoft Learn (2026-11-13)

- **Default outbound access** (`virtual-network/ip-services/default-outbound-access`, updated 2026-09-10): only API versions released after 31 March 2026 default new subnets to private. "Deployments that use earlier API versions retain the earlier default behavior." ADS exports subnets at `2022-07-01`, so **today's exports are not broken**; they silently depend on a behaviour Azure is retiring. Private subnets "aren't applicable to delegated or managed subnets". Routes with next hop `Internet` break in a private subnet. Windows activation and updates need an explicit outbound method. Explicit methods: NAT gateway on the subnet, Standard public IP on the NIC, Standard load balancer outbound rules, UDR to a firewall/NVA.
- **Stacks** (`templates/microsoft.resources/deploymentstacks`): versions `2025-07-01`, `2024-03-01`. In `2025-07-01`, `actionOnUnmanage.resources` and `denySettings.mode` are still required; new optional members are `resourcesWithoutDeleteSupport` (`detach`/`fail`), `validationLevel` (`Provider`/`ProviderNoRbac`/`Template`), `deploymentScope`, `extensionConfigs`, `externalInputs`.
- **Stacks what-if** (REST `deployment-stacks-what-if-results-at-resource-group/create-or-update`, api `2025-07-01`): `PUT /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Resources/deploymentStacksWhatIfResults/{name}?api-version=2025-07-01`. Body `properties`: `deploymentStackResourceId`, `retentionInterval`, `template`, `parameters`, `actionOnUnmanage`, `denySettings`. Answers 200 or 201 with `Azure-AsyncOperation`; `provisioningState` starts `initializing`. Result: `properties.changes.resourceChanges[]` with `id`, `type`, `changeType` (`create`, `delete`, `detach`, `modify`, `noChange`, `unsupported`), `changeCertainty` (`definite`/`potential`), `managementStatusChange.{before,after}`, `denyStatusChange.{before,after}`, `unsupportedReason`; plus `properties.changes.denySettingsChange`, `properties.diagnostics[]` (`level`, `code`, `message`), `properties.error`. The how-to page limits `retentionInterval` to `PT1H`..`PT3H` (the REST sample's `P7D` is wrong for the CLI; use `PT1H`). The target stack need not exist. Limit: 800 results per scope. Needs `Microsoft.Resources/deploymentStacksWhatIfResults/write`.
- **Retirements:** Application Gateway v1 retired 2026-04-28; Basic public IP and Basic load balancer retired 2025-09-30 (no new Basic LB since 2025-03-31). Front Door (classic) and CDN Standard from Microsoft (classic) dates were **not** confirmed.

## Global Constraints

- Package versions only in `Directory.Packages.props`; no new packages are needed.
- Warnings are errors in app projects.
- ARM model files under `AzureDesignStudio.AzureResources` carry `[GeneratedCode("ArmTypeGenerator")]`: add members in a separate `partial` file, never edit the generated one, and keep them pure data.
- Every generated `dependsOn` is `[resourceId('type', 'name', ...)]`.
- Check the REST reference before writing any ARM fixture (AGENTS.md pitfall); the shapes above are the checked ones.
- Gallery designs and golden templates are generated: regenerate with the commands in AGENTS.md, never hand-edit.
- Do not commit lock-file drift caused by a newer local SDK patch.
- Test commands: `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` and `dotnet test src/AzureDesignStudio.Server.Tests`.
- Commit messages follow the repo style (`feat(core): ...`, `fix(discovery): ...`).

## Review Focus

1. **A design saved before this change** has no `privateSubnet` field: it must load as non-private and export `defaultOutboundAccess: true`, so its behaviour does not change (Task 2 test).
2. **A delegated or reserved subnet** (App Service, Container Apps, `AzureFirewallSubnet`, `GatewaySubnet`, `AzureBastionSubnet`) must not get the property at all (Task 2 test).
3. **A hub-spoke design that routes through a firewall** has no NAT gateway: the outbound rule must not shout a false warning there (Task 3 test).
4. **A stack what-if that Azure could not fully evaluate** (`unsupported`, `potential`, or an error diagnostic) must be shown as uncertain, never as "no changes" (Task 6 test).
5. **A plain what-if token replayed against `CreateStack`**, or a stack token replayed against another stack name or another unmanage policy, must be refused (Task 7 tests).

---

### Task 1: NAT gateway becomes a real outbound path

Today a NAT gateway exports with an empty `publicIpAddresses` list and no subnet ever references it, so the canvas link "Outbound NAT" changes nothing in the template.

**Files:**
- Modify: `src/AzureDesignStudio.Core/Models/AzureGroupBase.cs`
- Modify: `src/AzureDesignStudio.Core/Network/NatGatewayModel.cs`
- Modify: `src/AzureDesignStudio.Core/Network/SubnetModel.cs`
- Test: `src/AzureDesignStudio.Core.Tests/OutboundAccessTests.cs` (create)

**Interfaces:**
- Produces: `AzureGroupBase.LinkedNodes` (`protected IEnumerable<NodeModel>`, same semantics as `AzureNodeBase.LinkedNodes`); `SubnetModel.NatGateway` (`public NatGatewayModel?`, the first linked NAT gateway).

- [ ] **Step 1: Write the failing tests** in `OutboundAccessTests.cs`. Build nodes with `DataModelFactory.CreateNodeModelFromKey` and link them the way the existing load-balancer/public-IP export tests do (search `Core.Tests` for a test that links a `PublicIpModel`).

  - `NatGateway_LinkedToPublicIp_ExportsThatAddress`: NAT `nat1` linked to public IP `pip1` exports `properties.publicIpAddresses == [ { "id": "[resourceId('Microsoft.Network/publicIPAddresses', 'pip1')]" } ]` and `dependsOn` contains the same expression.
  - `Subnet_LinkedToNatGateway_ReferencesIt`: subnet `snet1` in `vnet1` linked to `nat1` exports `properties.natGateway.id == "[resourceId('Microsoft.Network/natGateways', 'nat1')]"`, with that expression in the subnet's `dependsOn`.
  - `Subnet_WithoutNatGateway_OmitsTheMember`: the serialized subnet has no `natGateway` member.
  - `NatGateway_WithoutPublicIp_IsInvalid`: `IsValid` is false (ARM refuses a NAT gateway with no public IP or prefix), so `DesignValidator` reports `node-config`.

- [ ] **Step 2: Run them and confirm they fail**

  Run: `dotnet test src/AzureDesignStudio.Core.Tests --filter OutboundAccessTests`
  Expected: 4 failures.

- [ ] **Step 3: Implement.** Add `LinkedNodes` to `AzureGroupBase`. In `NatGatewayModel` override `PopulateArmAttributes` to fill `PublicIpAddresses` and `DependsOn` from linked `PublicIpModel`s, and extend `IsValid` with "at least one linked public IP". In `SubnetModel.PopulateArmAttributes` set `Properties.NatGateway` (a `SubResource`) and the `dependsOn` entry from `NatGateway`, null otherwise.

- [ ] **Step 4: Run the whole offline suite**

  Run: `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`
  Expected: all pass. If `StencilExportGoldenTests` or `GalleryDesignTests.EveryPaletteEntryHasACompleteMinimalDesign` fails for the NAT gateway entry, the minimal design for `nat-gateway` needs a linked public IP: add it in that test's builder.

- [ ] **Step 5: Commit** `fix(core): export a NAT gateway's public IP and its subnet association`

---

### Task 2: Subnets state `defaultOutboundAccess` explicitly

**Files:**
- Create: `src/AzureDesignStudio.AzureResources/Network/SubnetPropertiesFormat.DefaultOutboundAccess.cs`
- Modify: `src/AzureDesignStudio.AzureResources/Network/Subnets.cs`, `VirtualNetworks.cs` (api-version only)
- Modify: `src/AzureDesignStudio.Core/Network/SubnetModel.cs`, `SubnetForm.razor`
- Modify: `src/AzureDesignStudio.Core/Network/DedicatedSubnets.cs` (only if it has no "is reserved name" query)
- Test: `src/AzureDesignStudio.Core.Tests/OutboundAccessTests.cs`

**Interfaces:**
- Produces: `SubnetPropertiesFormat.DefaultOutboundAccess` (`bool?`, `[JsonPropertyName("defaultOutboundAccess")]`, omitted when null); `SubnetModel.PrivateSubnet` (`bool`, `[MapToDto]`, `[DisplayName("Private subnet (no default outbound access)")]`, default `false`); `SubnetModel.SupportsPrivateSubnet` (`bool`: false when the subnet has any delegation, including the App Service one added from children, or a reserved name per `DedicatedSubnets`).

**Decisions:**
- The api-version of `Microsoft.Network/virtualNetworks` and `Microsoft.Network/virtualNetworks/subnets` moves from `2022-07-01` to `2024-05-01` (both the `[AzureResource]` attribute and the `ApiVersion` override). `2022-07-01` predates the property; `2024-05-01` is before the 31 March 2026 cut-off, so the platform default does not shift under us.
- The default stays non-private so existing designs keep their behaviour. The name is `PrivateSubnet` (not `DefaultOutboundAccess`) so that a missing field in an old `.ads.json` deserializes to the old behaviour.
- The exported value is always explicit: `defaultOutboundAccess = !PrivateSubnet`. When `SupportsPrivateSubnet` is false the member is omitted, and the form hides the switch.

- [ ] **Step 1: Write the failing tests**

  - `Subnet_Default_ExportsDefaultOutboundAccessTrue`: new subnet in a vnet → `properties.defaultOutboundAccess == true`, `apiVersion == "2024-05-01"`.
  - `Subnet_Private_ExportsDefaultOutboundAccessFalse`.
  - `Subnet_Delegated_OmitsDefaultOutboundAccess`: `Delegation = "Microsoft.App/environments"` → member absent.
  - `Subnet_ReservedName_OmitsDefaultOutboundAccess`: theory over `AzureFirewallSubnet`, `GatewaySubnet`, `AzureBastionSubnet`.
  - `Subnet_LoadedFromLegacyDto_IsNotPrivate`: a `SubnetModelDto` JSON without `privateSubnet` round-trips to `PrivateSubnet == false`.
  - `Subnet_PrivateSubnet_RoundTripsThroughDto`.

- [ ] **Step 2: Run them and confirm they fail** (`--filter OutboundAccessTests`).

- [ ] **Step 3: Implement** the partial property, the two api-version bumps, the model property and the form switch (AntDesign `Switch`, shown only when `SupportsPrivateSubnet`).

- [ ] **Step 4: Regenerate generated artefacts and run everything**

  ```bash
  ADS_WRITE_GALLERY=src/AzureDesignStudio/wwwroot/gallery dotnet test src/AzureDesignStudio.Core.Tests --filter GalleryDesignTests
  dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"
  ```
  Expected: all pass after golden files are refreshed by whatever mechanism `StencilExportGoldenTests` documents at the top of the file. Review the golden diff: only the two api-versions and the new subnet member may change.

- [ ] **Step 5: Validate against Azure (creates nothing).** Follow the AGENTS.md note: `ADS_WRITE_TEMPLATES=<dir> dotnet test src/AzureDesignStudio.Core.Tests --filter GalleryDesignTests.WriteExportedTemplatesWhenAsked`, then `az deployment group validate` on the hub-spoke template. This step needs a signed-in `az` and a throwaway resource group; if none is available, say so in the commit body rather than skipping silently.

- [ ] **Step 6: Commit** `feat(core): write defaultOutboundAccess explicitly on exported subnets`

---

### Task 3: Validator rule `outbound-access`

**Files:**
- Modify: `src/AzureDesignStudio.Core/Validation/DesignValidator.cs`
- Test: `src/AzureDesignStudio.Core.Tests/DesignValidatorTests.cs`

**Interfaces:**
- Consumes: `SubnetModel.PrivateSubnet`, `SubnetModel.SupportsPrivateSubnet`, `SubnetModel.NatGateway` (Tasks 1-2); `VirtualMachineModel.Subnet`.
- Produces: rule id `"outbound-access"`.

**Rule.** For each subnet where `SupportsPrivateSubnet` is true and that hosts at least one virtual machine or scale set (directly or through an availability set):

| Subnet state | Result |
|---|---|
| Has a NAT gateway | no issue |
| Its virtual network, or one peered to it, contains an Azure Firewall | `Info`: "Subnet '{name}' has no NAT gateway. If outbound traffic goes through the firewall, add the route table after export; this design does not model routes." |
| Not private, no explicit path | `Warning`: "Subnet '{name}' relies on default outbound access, which Azure is retiring for new virtual networks. Link a NAT gateway, or mark the subnet private once it has an outbound path." |
| Private, no explicit path | `Warning`: "Subnet '{name}' is private and has no outbound path. Its machines cannot reach the internet, including Windows activation and updates. Link a NAT gateway." |

A public IP on the machine and load balancer outbound rules are not counted: the VM export has no public-IP link and the load balancer export writes no outbound rules. If either turns out to exist when you read the models, count it and add a test.

- [ ] **Step 1: Write the failing tests**: one per table row, plus `Subnet_WithoutMachines_ProducesNoOutboundIssue` and `DelegatedSubnet_ProducesNoOutboundIssue`. Assert severity and that the message contains the subnet name.
- [ ] **Step 2: Run** `--filter DesignValidatorTests`; expect the new tests to fail.
- [ ] **Step 3: Implement** `AddOutboundAccessIssues(List<NodeModel> nodes, List<ValidationIssue> issues)` and call it from `Validate`. Peering: use `VirtualNetworkPeeringModel` if it links two networks; otherwise treat only the subnet's own network.
- [ ] **Step 4: Run the offline suite.** `GalleryDesignTests` filters on `Error` only, so warnings must not break it; confirm.
- [ ] **Step 5: Commit** `feat(core): warn when a machine subnet depends on default outbound access`

---

### Task 4: Retired SKUs in designs

The public IP and load balancer forms still offer `Basic`, which Azure no longer deploys.

**Files:**
- Create: `src/AzureDesignStudio.Core/Validation/RetiredSkus.cs`
- Modify: `src/AzureDesignStudio.Core/Validation/DesignValidator.cs`
- Modify: `src/AzureDesignStudio.Core/Network/PublicIpForm.razor`, `LoadBalancerForm.razor`
- Test: `src/AzureDesignStudio.Core.Tests/DesignValidatorTests.cs`

**Interfaces:**
- Produces: `public sealed record RetiredSku(string ResourceType, string Sku, DateOnly RetiredOn, string Replacement)`; `public static class RetiredSkus { public static IReadOnlyList<RetiredSku> All; public static RetiredSku? Match(string resourceType, string? sku); }` (case-insensitive). Rule id `"retired-sku"`, severity `Error`.

**Values:**

| ResourceType | Sku | RetiredOn | Replacement |
|---|---|---|---|
| `Microsoft.Network/publicIPAddresses` | `Basic` | 2025-09-30 | Standard |
| `Microsoft.Network/loadBalancers` | `Basic` | 2025-09-30 | Standard |
| `Microsoft.Network/applicationGateways` | `Standard` | 2026-04-28 | Standard_v2 |
| `Microsoft.Network/applicationGateways` | `WAF` | 2026-04-28 | WAF_v2 |

- [ ] **Step 1: Write the failing tests**: `BasicPublicIp_ProducesRetiredSkuError`, `BasicLoadBalancer_ProducesRetiredSkuError`, `AppGatewayV1Tier_ProducesRetiredSkuError` (set `Tier = "Standard"`), `StandardSkus_ProduceNoRetiredSkuIssue`. Message must contain the retirement date as `yyyy-MM-dd` and the replacement.
- [ ] **Step 2: Run and confirm failure.**
- [ ] **Step 3: Implement** the table and the rule (read the SKU from `PublicIpModel.Sku`, `LoadBalancerModel.Sku`, `AppGatewayModel.Tier`). In both forms keep the `Basic` radio only when the model already holds `Basic` (so an old design still shows its value), disabled, with the text "Basic (retired)".
- [ ] **Step 4: Run the offline suite.**
- [ ] **Step 5: Commit** `feat(core): refuse retired Basic and v1 network SKUs in designs`

---

### Task 5: Retired SKUs in scans

**Files:**
- Modify: `src/AzureDesignStudio.Discovery/Analysis/DeprecationCatalog.cs`
- Modify: `src/AzureDesignStudio.Desktop/Components/TenantScanDrawerTemplate.razor` (only where the watch list prints an entry)
- Test: `src/AzureDesignStudio.Core.Tests/OrphanAnalyzerTests.cs` (the existing catalog tests live there)

**Interfaces:**
- Produces: `DeprecationEntry` gains an optional last parameter `string? Sku = null`; `DeprecationCatalog.Match(InventoryItem item)` (SKU-aware; the existing `Match(string resourceType)` keeps matching only entries whose `Sku` is null); `Watch` uses the new overload. `Seed.DataAsOf` becomes 2026-11-13.

**SKU read from `InventoryItem.Raw`:** `sku.name` for public IPs and load balancers; `properties.sku.tier` for application gateways. Resource Graph returns `sku` as a top-level column: check how `InventoryItem.Raw` is filled before choosing the path, and use a real ARG row shape in the fixture.

**Seed additions:** the four rows of Task 4 with status `retired`, plus `Microsoft.Network/frontDoors` and `Microsoft.Cdn/profiles` with SKU `Standard_Microsoft` as `review_due`, date null, note "Classic tier; confirm the retirement date on Microsoft Learn." Do not write a date for those two until the Learn page has been opened.

- [ ] **Step 1: Write the failing tests**: `Watch_FlagsBasicPublicIp`, `Watch_IgnoresStandardPublicIp`, `Watch_FlagsApplicationGatewayV1ByTier`, `Watch_ItemWithoutRaw_MatchesTypeOnlyEntries`, `Load_OldSnapshotWithoutSku_StillParses`, and `SeedAgreesWithCoreRetiredSkus` (every `RetiredSkus.All` row has a seed entry with the same type, SKU and date; `Core.Tests` references both projects).
- [ ] **Step 2: Run and confirm failure.**
- [ ] **Step 3: Implement.** Show the SKU beside the type in the scan drawer's retirement list.
- [ ] **Step 4: Run the offline suite.**
- [ ] **Step 5: Commit** `feat(discovery): flag retired SKUs in tenant scans`

---

### Task 6: Stacks what-if client and parser, stacks api `2025-07-01`

**Files:**
- Modify: `src/AzureDesignStudio.Discovery/Iac/DeploymentStack.cs` (`ApiVersion`), `DeploymentStackClient.cs` (`ListApiVersion`)
- Create: `src/AzureDesignStudio.Discovery/Iac/StackWhatIf.cs` (models + parser + request body)
- Create: `src/AzureDesignStudio.Discovery/Iac/StackWhatIfClient.cs`
- Test: `src/AzureDesignStudio.Core.Tests/StackWhatIfTests.cs` (create); update `DeploymentStackTests.cs` for the version

**Interfaces:**
- Produces:

```csharp
public enum StackChangeKind { Create, Modify, NoChange, Detach, Delete, Unsupported }

public sealed record StackResourceChange(
    string Id, string Type, StackChangeKind Kind, bool Potential,
    string? ManagementBefore, string? ManagementAfter,
    string? DenyBefore, string? DenyAfter, string? UnsupportedReason);

public sealed record StackWhatIfSummary(
    string ProvisioningState,
    IReadOnlyList<StackResourceChange> Changes,
    string? DenyModeBefore, string? DenyModeAfter,
    IReadOnlyList<ArmDiagnostic> Diagnostics)
{
    public IReadOnlyList<string> DeleteResourceIds { get; }
    public IReadOnlyList<string> DetachResourceIds { get; }
    public bool HasDelete { get; }
    /// Non-null when the result cannot be trusted as complete: any Unsupported or
    /// Potential change, an error-level diagnostic, or a state other than "succeeded".
    public string? IndeterminacyReason { get; }
}

public static class StackWhatIf
{
    public const string ApiVersion = "2025-07-01";
    public const string RetentionInterval = "PT1H";
    public static string BuildRequest(string stackResourceId, string stackBodyJson);
    public static StackWhatIfSummary Parse(string resultJson);
    public static string ResultName(string stackName, string planHash); // "ads-" + first 16 hex of the hash; matches ^[-\w\._\(\)]{1,90}$
}

public sealed class StackWhatIfClient(ArmTransport transport)
{
    public Task<StackWhatIfSummary> RunAsync(string subscriptionId, string resourceGroupName,
        string stackName, string stackBodyJson, string planHash, CancellationToken ct = default);
}
```

**Decisions:**
- `DeploymentStack.ApiVersion` and `DeploymentStackClient.ListApiVersion` both become `2025-07-01`. The body is unchanged: `resourcesWithoutDeleteSupport` and `validationLevel` are not emitted (delete is refused anyway; default validation is what we want). The plan hash covers the body and the stack name, not the api-version, so existing hashes stay valid.
- `BuildRequest` copies `template`, `parameters`, `actionOnUnmanage`, `denySettings` out of the stack body that `DeploymentStack.Build` produced and adds `deploymentStackResourceId` and `retentionInterval`. It never rebuilds them, so what is previewed is exactly what is hashed.
- `RunAsync` does `PutAsyncPolled`, then a GET if the polled response carries no `changes`, then a best-effort DELETE of the result (a failed delete is ignored; the result expires in an hour).

- [ ] **Step 1: Write the failing tests** with fixtures built from the REST shape above:

  - `Parse_MapsEveryChangeType`: one change per `changeType` value → the six kinds.
  - `Parse_DetachAndDelete_AreListed`: `DetachResourceIds`/`DeleteResourceIds`/`HasDelete`.
  - `Parse_ReadsManagementAndDenyStatus`: `managementStatusChange` `unmanaged`→`managed`, `denyStatusChange` `none`→`denyDelete`.
  - `Parse_Unsupported_IsIndeterminate`, `Parse_PotentialChange_IsIndeterminate`, `Parse_ErrorDiagnostic_IsIndeterminate`, `Parse_StateNotSucceeded_IsIndeterminate`.
  - `Parse_NoChangesMember_IsIndeterminate` (never "no changes").
  - `Parse_PropertiesError_Throws` a `WhatIfException` carrying ARM's code.
  - `BuildRequest_CopiesPolicyAndTemplateFromTheStackBody`: byte-equal `template` and `actionOnUnmanage` after canonicalisation; `retentionInterval == "PT1H"`; no `location`.
  - `ResultName_IsStableAndValid`.
  - In `DeploymentStackTests`: the stack URL ends with `api-version=2025-07-01`, and `Build` output for a fixed input is unchanged (hash equality with the value the test already pins).

- [ ] **Step 2: Run** `--filter "StackWhatIfTests|DeploymentStackTests"`; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the offline suite.**
- [ ] **Step 5: Commit** `feat(discovery): deployment stacks 2025-07-01 and a stacks what-if client`

---

### Task 7: Stack deployments are gated on the stacks what-if

**Files:**
- Modify: `src/AzureDesignStudio.SharedModels/deploy_model.proto`
- Modify: `src/AzureDesignStudio.Discovery/Iac/WhatIf/WhatIfTokenGate.cs`
- Modify: `src/AzureDesignStudio.Services/DeployService.cs`
- Modify: `src/AzureDesignStudio.Server/Services/HostedServices.cs` only if RPCs are listed there individually
- Modify: `src/AzureDesignStudio/Services/DeployGrpcService.cs`, `Components/MenuDrawer/CodeDrawerTemplate.razor(.cs)`
- Test: `src/AzureDesignStudio.Server.Tests` (stack RPC tests), `src/AzureDesignStudio.Core.Tests` (gate tests)

**Interfaces:**
- Consumes: Task 6 types.
- Produces: `rpc StackWhatIf(StackWhatIfRequest) returns (StackWhatIfResponse)`. `StackWhatIfRequest` has the same fields as `CreateStackRequest` minus `what_if_token` and `plan_hash`. `StackWhatIfResponse`: `int32 status_code`, `string error_message`, `string what_if_token`, `string plan_hash`, `string changes_json`, `int32 creates`, `int32 modifies`, `int32 detaches`, `int32 deletes`, `repeated string caveats`, `string indeterminacy_reason`.
- `WhatIfTokenGate.IssueForStack(string subscriptionId, string resourceGroupName, string stackPlanHash, bool hasDelete, IReadOnlyList<string> deleteIds)` and `ValidateAndConsumeForStack(string? token, string subscriptionId, string resourceGroupName, string stackPlanHash)`. A stack token and a deployment token are different kinds: each `ValidateAndConsume*` refuses the other kind with the existing mismatch status.

**Decisions:**
- `stackPlanHash` is `DeploymentStackBody.PlanHash`, which already binds template, parameters, policy, bypass flag and stack name.
- `CreateStack` calls `ValidateAndConsumeForStack` instead of the deployment overload. Its existing plan-approval check (scope + template + parameters hash) is unchanged.
- `StackWhatIf` runs the same request validation and guardrail checks `CreateStack` runs before its gate. Extract that shared prefix into one private method rather than copying it.
- An indeterminate summary still issues a token (same as the deployment what-if today, which returns caveats); the reason is returned and shown.
- The drawer's stack path calls `StackWhatIf` where it called `WhatIf`, and its preview lists detaches and deny-status changes. Follow the existing what-if preview markup; no new component.

- [ ] **Step 1: Write the failing gate tests** (`Core.Tests`): `StackToken_IsSingleUse`, `StackToken_RefusedForAnotherStackHash`, `StackToken_RefusedByDeploymentValidate`, `DeploymentToken_RefusedByStackValidate`, `StackToken_Expires`.
- [ ] **Step 2: Write the failing RPC tests** (`Server.Tests`, existing fake-`ServerCallContext` style; ARM is faked at the `HttpClient` the service already takes from `IHttpClientFactory`): `StackWhatIf_ReturnsDetachesAndToken`, `StackWhatIf_DeleteAction_IsRefusedBeforeAnyArmCall`, `CreateStack_WithDeploymentWhatIfToken_IsBlocked`, `CreateStack_WithStackToken_Proceeds`, `CreateStack_PolicyChangedAfterPreview_IsBlocked`.
- [ ] **Step 3: Run both test projects; expect the new tests to fail.**
- [ ] **Step 4: Implement** proto, gate, service, client wrapper, drawer.
- [ ] **Step 5: Run everything**

  ```bash
  dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"
  dotnet test src/AzureDesignStudio.Server.Tests
  dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst
  ```
  Expected: all pass, Desktop builds.

- [ ] **Step 6: Update `AGENTS.md`** (the deployment-stacks note: api-version `2025-07-01`, stacks what-if gates `CreateStack`; the subnet note: explicit `defaultOutboundAccess`, NAT gateway association) and tick Wave A in `docs/feature-plan-2026-11.md`.
- [ ] **Step 7: Commit** `feat(deploy): gate stack deployments on the stacks what-if`

---

## Not in this plan

- Load balancer outbound rules, route tables and public IPs on VM NICs as modelled outbound paths (the canvas has none of them today).
- `validationLevel` and `resourcesWithoutDeleteSupport` on the stack body.
- Front Door classic and CDN classic retirement dates (seeded as `review_due` until confirmed).
- A live-Azure test of the stacks what-if. The first real run needs a subscription where the caller has `deploymentStacksWhatIfResults/write`; do it by hand from the Desktop app before release.
