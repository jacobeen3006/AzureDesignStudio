# Wave B1: Offline Design Linter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A design gets deterministic Well-Architected advice, grouped by pillar with a Microsoft Learn link per rule, shown in the validation drawer and returned by one read-only MCP tool.

**Architecture:** A new `DesignLinter` in `Core/Validation` runs a fixed rule table over the same node list `DesignValidator` takes. The validator stays the deploy-correctness check (errors that Azure would refuse); the linter is advice and never produces `Error`. The drawer shows both. The MCP server loads a saved design headlessly through `DataModelFactory.LoadDiagramFromDto` and returns validator issues plus lint findings.

**Tech Stack:** .NET 10, xUnit, Blazor + AntDesign, ModelContextProtocol server attributes already used in `AzureDesignStudio.Mcp`.

**Spec:** `docs/feature-plan-2026-11.md`, Wave B item B1. Roadmap requirement POST-03 ("the architecture linter reports the same deterministic Well-Architected violations in the validation drawer and through one read-only MCP tool").

## Scope decisions

- Rules use only what the design models actually hold today: SKU/tier fields, placement, and links. The models carry no availability zones, TLS, firewall or diagnostic settings, so there are no rules about those. That is deliberate: a rule that cannot see its evidence would lie.
- No rule duplicates a `DesignValidator` rule (`outbound-access`, `retired-sku`, `required-children` stay where they are).
- Performance Efficiency and Cost Optimization get no rules in this plan. The pillar enum still lists all five so later rules need no schema change.
- Scan-side (live tenant) linting is not in this plan.

## Global Constraints

- Package versions only in `Directory.Packages.props`; no new packages.
- Warnings are errors in app projects.
- AntDesign 1.x: enum values, not raw strings (`ButtonType.Primary`); forms use `LabelCol`/`WrapperCol` objects.
- No `async void` handlers.
- The MCP server stays read-only: the new tool is `ReadOnly = true` and opens nothing for writing.
- Do not commit lock-file drift, `.DS_Store`, or anything under `.superpowers/`.
- Tests: `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` and `dotnet test src/AzureDesignStudio.Server.Tests`. Desktop build: `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst` (revert any `packages.maccatalyst.lock.json` change). MCP build: `dotnet build src/AzureDesignStudio.Mcp`.
- Learn links below were fetched on 2026-11-13 and answered 200. Use them verbatim.

## Review Focus

1. **An empty design, and a design of only groups**, must lint without throwing and return no findings.
2. **A node the linter does not know** (`UnknownNodeModel`, future types) must be ignored, never crash a rule.
3. **Ordering must be stable**: the same design gives the same findings in the same order on every run, on every platform (no dictionary-order or culture dependence).
4. **A saved design the MCP tool cannot load** (corrupt JSON, unknown schema) must return a clear error, not a partial "no findings" answer.
5. **A private endpoint typed by resource id rather than linked** must still count as covering its target.

---

### Task 1: Linter core

**Files:**
- Create: `src/AzureDesignStudio.Core/Validation/LintFinding.cs`
- Create: `src/AzureDesignStudio.Core/Validation/DesignLinter.cs`
- Test: `src/AzureDesignStudio.Core.Tests/DesignLinterTests.cs` (create)

**Interfaces:**
- Produces:

```csharp
public enum LintPillar { Reliability, Security, CostOptimization, OperationalExcellence, PerformanceEfficiency }

public sealed record LintRule(string Id, LintPillar Pillar, ValidationSeverity Severity, string Title, string LearnUrl);

public sealed record LintFinding(string RuleId, LintPillar Pillar, ValidationSeverity Severity,
    string NodeName, string Message, string LearnUrl);

public static class DesignLinter
{
    public static IReadOnlyList<LintRule> Rules { get; }          // the catalog, in a fixed order
    public static IReadOnlyList<LintFinding> Lint(IEnumerable<NodeModel> nodes);
}
```

- Findings are ordered by pillar (enum order), then rule id (ordinal), then node name (ordinal).
- Rule ids are `REL-…`, `SEC-…`, `OPS-…` upper-case with hyphens.
- `Lint` never returns `ValidationSeverity.Error`.
- Each rule is a private static method `IEnumerable<LintFinding> RuleName(List<NodeModel> nodes)` registered beside its `LintRule` in one table, so the catalog and the code cannot drift.

- [ ] **Step 1: Write the failing tests**
  - `Rules_HaveUniqueIds_AndLearnLinks`: ids unique; every `LearnUrl` starts with `https://learn.microsoft.com/`; no rule has severity `Error`; every id starts with `REL-`, `SEC-` or `OPS-` matching its pillar.
  - `Lint_EmptyDesign_ReturnsNothing`; `Lint_OnlyGroups_ReturnsNothing` (a virtual network with an empty subnet).
  - `Lint_UnknownNode_IsIgnored` (create one through `DataModelFactory.CreateNodeModelFromKey` with a key that does not exist; see how existing tests obtain an `UnknownNodeModel`).
  - `Lint_IsOrderedByPillarRuleAndName` (use Task 2/3 rules once they exist; in this task assert it on a stub-free design by adding the first rule of Task 2, `REL-VM-SINGLE`, here).
- [ ] **Step 2: Run** `dotnet test src/AzureDesignStudio.Core.Tests --filter DesignLinterTests`; expect failures.
- [ ] **Step 3: Implement** the types, the table, and `REL-VM-SINGLE` (defined in Task 2) as the first entry.
- [ ] **Step 4: Run the offline suite.**
- [ ] **Step 5: Commit** `feat(core): design linter core with a rule catalog`

---

### Task 2: Reliability rules

**Files:**
- Modify: `src/AzureDesignStudio.Core/Validation/DesignLinter.cs`
- Test: `src/AzureDesignStudio.Core.Tests/DesignLinterTests.cs`

**Rules** (message text is fixed; `{name}` is the node name):

| Id | Severity | Fires when | Message | Learn link |
|---|---|---|---|---|
| `REL-VM-SINGLE` | Warning | a `VirtualMachineModel` whose group is not an `AvailabilitySetModel` | "Virtual machine '{name}' is a single instance outside an availability set. Use an availability set or a scale set so maintenance or a host failure does not take the workload down." | `https://learn.microsoft.com/en-us/azure/well-architected/service-guides/virtual-machines` |
| `REL-AVSET-ONE` | Warning | an `AvailabilitySetModel` with exactly one virtual machine | "Availability set '{name}' holds one virtual machine. It needs at least two to protect against a host failure." | `https://learn.microsoft.com/en-us/azure/virtual-machines/availability-set-overview` |
| `REL-APPPLAN-TIER` | Warning | an `AppServicePlanModel` with at least one app child and `SkuTier` in `Free`, `Shared`, `Basic` (case-insensitive) | "App service plan '{name}' is on the {tier} tier, which cannot scale out automatically. Use Standard or higher for production workloads." | `https://learn.microsoft.com/en-us/azure/well-architected/service-guides/app-service-web-apps` |
| `REL-APPGW-INSTANCES` | Warning | an `AppGatewayModel` with `Autoscaling == false` and `InstanceCount < 2` | "Application gateway '{name}' runs a single fixed instance. Use at least two instances or turn on autoscaling." | `https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-application-gateway` |
| `REL-SQL-BASIC` | Info | a `SqlDatabaseModel` with `SkuTier` equal to `Basic` (case-insensitive) | "SQL database '{name}' is on the Basic tier, intended for development and small workloads." | `https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-sql-database` |
| `REL-STORAGE-LRS` | Info | a `StorageAccountModel` whose `SkuName` ends with `_LRS` (case-insensitive) | "Storage account '{name}' uses locally redundant storage: three copies in one datacenter. Consider zone-redundant or geo-redundant storage for data that must survive a datacenter outage." | `https://learn.microsoft.com/en-us/azure/storage/common/storage-redundancy` |

Not fired: a function app's embedded `StorageAccount` field (it is not a canvas node); an app service plan the consumption/premium function rule already re-tiers at export (a plan with a function child whose `HostingPlan` is `consumption` or `premium`).

- [ ] **Step 1: Write the failing tests**: for each rule one "fires" test asserting rule id, severity, node name and the full Learn link, and one "does not fire" test for the nearest safe configuration (VM in a two-VM availability set; plan on Standard; gateway with autoscaling; `Standard_ZRS`; etc.). Add `REL_APPPLAN_TIER_SkipsFunctionReTieredPlans`.
- [ ] **Step 2: Run** `--filter DesignLinterTests`; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the offline suite.**
- [ ] **Step 5: Commit** `feat(core): reliability lint rules`

---

### Task 3: Security and operations rules

**Files:**
- Modify: `src/AzureDesignStudio.Core/Validation/DesignLinter.cs`
- Modify: `src/AzureDesignStudio.Core/Network/PrivateEndpointModel.cs` (expose the supported-type check)
- Test: `src/AzureDesignStudio.Core.Tests/DesignLinterTests.cs`

**Interfaces:**
- Produces: `public static bool PrivateEndpointModel.SupportsPrivateLink(string armResourceType)` (wraps the existing private `GroupIdByType` lookup; no behaviour change).

**Rules:**

| Id | Severity | Fires when | Message | Learn link |
|---|---|---|---|---|
| `SEC-APPGW-NO-WAF` | Warning | an `AppGatewayModel` with `Tier == "Standard_v2"` | "Application gateway '{name}' has no web application firewall. Use the WAF v2 tier for internet-facing applications." | `https://learn.microsoft.com/en-us/azure/web-application-firewall/ag/ag-overview` |
| `SEC-NO-PRIVATE-ENDPOINT` | Info | the design has at least one `VirtualNetworkModel`, and an `AzureNodeBase` whose `ArmResourceType` satisfies `SupportsPrivateLink` is not the target of any `PrivateEndpointModel` | "{service} '{name}' is reachable only over its public endpoint. Add a private endpoint to keep traffic on the virtual network." (`{service}` is the node's `ServiceName`) | `https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-overview` |
| `SEC-NO-BASTION` | Info | the design has at least one virtual machine or scale set and no `BastionsModel` | one finding for the design, `NodeName` = `"design"`: "The design has virtual machines but no Azure Bastion. Plan how administrators reach them without public IP addresses." | `https://learn.microsoft.com/en-us/azure/bastion/bastion-overview` |
| `OPS-NO-MONITORING` | Info | the design has at least one compute or app node (virtual machine, scale set, web app, function app, container app, AKS cluster) and neither a Log Analytics workspace nor an Application Insights node | one finding, `NodeName` = `"design"`: "The design has no Log Analytics workspace or Application Insights. Add one so the workload can be observed." | `https://learn.microsoft.com/en-us/azure/well-architected/operational-excellence/observability` |

"Is the target of a private endpoint" means: some `PrivateEndpointModel` has `LinkedTarget` reference-equal to the node, or its `EffectiveTargetId` equals the node's `ResourceId` (ordinal, case-insensitive). A web app counts as `Microsoft.Web/sites`; do not fire `SEC-NO-PRIVATE-ENDPOINT` for a SQL database (the endpoint belongs to its server).

Find the exact model class names for scale sets, AKS, container apps, Log Analytics and Application Insights with `DataModelFactory` (the `AdsConstants` keys are `LogAnalyticsWorkspace` and the Insights folder's model).

- [ ] **Step 1: Write the failing tests**: fires / does-not-fire per rule; `SEC_NO_PRIVATE_ENDPOINT_NoVirtualNetwork_DoesNotFire`; `SEC_NO_PRIVATE_ENDPOINT_EndpointTypedById_CountsAsCovered`; `SEC_NO_PRIVATE_ENDPOINT_LinkedEndpoint_CountsAsCovered`; `SEC_NO_BASTION_FiresOncePerDesign` (three VMs, one finding); `OPS_NO_MONITORING_WorkspacePresent_DoesNotFire`.
- [ ] **Step 2: Run** `--filter DesignLinterTests`; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the offline suite.**
- [ ] **Step 5: Commit** `feat(core): security and operations lint rules`

---

### Task 4: Gallery calibration

The three gallery designs are what a new user opens first. Pin what the linter says about them, so a rule change that floods or silences them is a visible test diff.

**Files:**
- Modify: `src/AzureDesignStudio.Core.Tests/GalleryDesignTests.cs`

- [ ] **Step 1: Write the test** `GalleryDesigns_LintFindingsArePinned`: for each of `hub-spoke-final`, `aks-baseline`, `apim-landingzone`, built by the existing builders in this file, assert the exact ordered list of `"{RuleId}:{NodeName}"` strings. Run the linter once to obtain the lists, read them critically, and only then pin them.
- [ ] **Step 2: Judge the output.** If a gallery design gets a `Warning` that is a false positive for a reference architecture, stop and report it with the finding text rather than pinning it or changing a rule. `Info` findings are expected.
- [ ] **Step 3: Run the offline suite.**
- [ ] **Step 4: Commit** `test(core): pin lint findings for the gallery designs`

---

### Task 5: Validation drawer shows lint findings

**Files:**
- Modify: `src/AzureDesignStudio/Components/TopMenu.razor.cs` (where `DesignValidator.Validate` is called, ~line 254)
- Modify: `src/AzureDesignStudio/Components/MenuDrawer/ValidationDrawerTemplate.razor`
- Create: `src/AzureDesignStudio.Core/Validation/ValidationReport.cs`
- Test: `src/AzureDesignStudio.Core.Tests/DesignLinterTests.cs`

**Interfaces:**
- Produces: `public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues, IReadOnlyList<LintFinding> Findings)` with `public static ValidationReport For(IEnumerable<NodeModel> nodes)` (runs both). Serialized with the existing `ValidationJson.Options`.
- The drawer's `Options` string becomes a serialized `ValidationReport`. It must still accept the old payload (a bare JSON array of `ValidationIssue`) so nothing else that opens the drawer breaks: an array parses as issues with no findings.

**Drawer layout** (text sketch):

```
Validation                      2 issues
  [Error]   pip1 — Public IP 'pip1' uses the Basic SKU …
  [Warning] snet1 — Subnet 'snet1' relies on default outbound access …

Well-Architected advice         4 findings
  Reliability
    [Warning] vm1 — Virtual machine 'vm1' is a single instance …   Learn more ↗
  Security
    [Info] st1 — Storage Account 'st1' is reachable only over …    Learn more ↗
```

- "No issues found. The design passes all validation rules." stays for an empty issue list; the advice section is omitted when there are no findings.
- Pillar headings are the enum names with spaces ("Operational Excellence").
- "Learn more" is an `<a>` with `target="_blank"` and `rel="noopener noreferrer"`.
- Reuse the existing `SeverityColor`.

- [ ] **Step 1: Write the failing tests**: `ValidationReport_For_RunsBoth`; `ValidationReport_RoundTripsThroughJson`; `ValidationReport_ParsesLegacyIssueArray` (put the tolerant parse in a static `ValidationReport.Parse(string json)` in Core so it is testable).
- [ ] **Step 2: Run and confirm failure.**
- [ ] **Step 3: Implement** the record, the parse, the TopMenu call and the drawer markup.
- [ ] **Step 4: Build the hosts**: `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst`; run both test suites.
- [ ] **Step 5: Commit** `feat(client): show Well-Architected advice in the validation drawer`

---

### Task 6: MCP tool `ads_design_lint`

**Files:**
- Create: `src/AzureDesignStudio.Core/Validation/DesignCheck.cs`
- Modify: `src/AzureDesignStudio.Mcp/AdsMcpTools.cs`
- Modify: `AGENTS.md`, `README.md` (tool count 13 → 14 wherever stated), `docs/feature-plan-2026-11.md` (mark B1 as shipped, list the rule ids and the scope decisions)
- Test: `src/AzureDesignStudio.Core.Tests/DesignCheckTests.cs` (create); extend the existing MCP tool tests (`AdsToolEngineTests.cs` or wherever the tool manifest is asserted) for the new tool name

**Interfaces:**
- Produces: `public static class DesignCheck { public static ValidationReport Run(string designDataJson); }`: deserializes the stored design payload to `DiagramGraph` (default options, as `ads_design_get` does), loads it into a headless diagram with `DataModelFactory.LoadDiagramFromDto` (see `DesignRoundTripTests.Load` for the exact setup, including the link factory and the AutoMapper instance), and returns `ValidationReport.For(nodes ∪ groups)`. Throws `InvalidDataException` with a plain message when the payload is not a design graph.
- MCP tool `ads_design_lint(designId)`, `ReadOnly = true`. Description: "Checks one saved design offline: deploy-blocking validation issues and Well-Architected advice (rule id, pillar, severity, resource name, message, Microsoft Learn link). Deterministic; reads only the local workspace; never contacts Azure." Returns issues, findings, and `ruleCount` (the catalog size). An unknown id throws `McpException("design not found: …")` like `ads_design_get`; an unloadable payload throws `McpException` carrying the `InvalidDataException` message.
- If Core needs an AutoMapper instance that today only the client constructs, find how `Core.Tests` and `AdsDesignText` obtain one and reuse that path; do not add a second mapper configuration.

- [ ] **Step 1: Write the failing tests**: `Run_GalleryDesign_MatchesTheDirectLint` (save a gallery design to its stored JSON form, run `DesignCheck.Run`, compare rule ids with linting the built models directly); `Run_CorruptJson_ThrowsInvalidData`; `Run_JsonThatIsNotAGraph_ThrowsInvalidData` (e.g. `"[]"` or `"{}"` with no nodes is a valid empty design: decide by what `DiagramGraph` deserialization gives, and make `null` or a non-object the failure); the manifest test now expects `ads_design_lint`.
- [ ] **Step 2: Run and confirm failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Build and test**: `dotnet build src/AzureDesignStudio.Mcp`; both test suites.
- [ ] **Step 5: Update the docs** listed above.
- [ ] **Step 6: Commit** `feat(mcp): ads_design_lint returns validation and Well-Architected findings`

---

## Not in this plan

- Rules needing data the models lack (zones, TLS, public network access, diagnostics, NSG association).
- Linting a scanned tenant.
- A link-rot check in CI for the Learn URLs (roadmap SHELL-06 covers it).
- Suppressing or configuring rules per design.
