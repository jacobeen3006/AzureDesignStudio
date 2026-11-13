# Core / ARM / Source Generation Review

Reviewed: 2026-10-30
Depth: deep, targeted cross-file tracing
Status: COMPLETE — 1 blocker, 4 warnings (issues_found)
Baseline: fa62bb9001fccb35bae7bea9df27499c4b31fa92 (`fa62bb9`)
Scope: `AzureDesignStudio.Core`, `AzureDesignStudio.AzureResources`, `AzureDesignStudio.SourceGeneration`, `AzureDesignStudio.SharedModels`, and relevant `AzureDesignStudio.Core.Tests`.
Constraints honored: source-only; no builds/tests, source edits, live Azure calls, user database access, or credentials/.env reads.

## Context and Phase 1 disposition

Phase 1's `01-REVIEW.md` and `01-REVIEW-DISPOSITION.md` list the subnet delegation defect (M1), inconsistent case handling for required firewall/Bastion subnet names (M2), and faulty `NextFreeSubnetPrefix` fallback (L3) as open/advisory findings. Current source still contains each defect (findings CR-01, CR-02, WR-01 below); they were not treated as newly discovered fixes or assumed resolved. Phase 1 explicitly routes them to follow-up work; this review does not modify source. The proto contract also still includes `SubscriptionInfo.client_secret` (`src/AzureDesignStudio.SharedModels/deploy_model.proto:34-40`); this maps to planned CRED-03 / Phase 9 credential migration, so it is recorded as an existing milestone gap rather than a newly raised finding.

## Findings (Blocker first, then warnings)

### CR-01 — Subnet form exposes an invalid ARM delegation service

**Severity:** BLOCKER

**File:** `src/AzureDesignStudio.Core/Network/SubnetForm.razor:11-19`; duplicate option at `VirtualNetworkForm.razor:40-47`; verbatim export at `SubnetModel.cs:148-157`.

**Issue:** Both editable subnet forms offer `Microsoft.Database/custom`. Selecting it persists the string as `SubnetModel.Delegation`, and `PopulateArmAttributes` emits it as `delegations[].properties.serviceName`. It is not a valid Azure service delegation, so a reachable UI selection creates an ARM template Azure rejects. Microsoft Learn describes delegation as designation of a subnet for a particular Azure PaaS service and says each Azure service defines its own supported subnet model: [Subnet Delegation in Azure Virtual Network](https://learn.microsoft.com/en-us/azure/virtual-network/subnet-delegation-overview). The named `Microsoft.Database/custom` value is not supported by the app's modeled/exported resource catalog or the listed service choices.

**Reproduction:** Add/edit a subnet, select “Microsoft.Database/custom”, export the design; inspect the subnet's `properties.delegations` service name.

**Minimal fix:** Remove the option from both forms (or replace it only with a verified Azure-supported delegation); validate the stored value at the model/export boundary so stale saved designs cannot emit it.

**Regression test:** Assert both catalogs expose only supported values, and that exporting a subnet with an unsupported legacy delegation fails with a typed validation diagnostic rather than emitting ARM.

### CR-02 — Required-subnet name rules disagree on casing

**Severity:** WARNING

**File:** `src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs:45-53,64-66`; `src/AzureDesignStudio.Core/Network/BastionsModel.cs:26-35,45-47`; auto-provision lookup noted at Phase 1 review `src/AzureDesignStudio/Components/DiagramPanel.razor.cs:160-178` (outside this review's edit/scope ownership).

**Issue:** Provisioning searches preset names case-insensitively, while `IsDrappable`, `IsValid`, and the forms compare exact case. A subnet named `azurefirewallsubnet` / `azurebastionsubnet` is therefore found as the preset, then rejected as a placement target and marked invalid despite the provisioning path accepting it. This is still open in Phase 1 disposition M2.

**Reproduction:** Create a VNet subnet with the required name in lowercase, then drop the corresponding appliance / inspect its validity. The lookup and validation disagree.

**Minimal fix:** Centralize the required subnet-name predicate and use the same ordinal-ignore-case comparison in lookup, placement, model validity, and forms.

**Regression test:** Parameterize Firewall and Bastion placement/validity tests with canonical and mixed/lowercase subnet names, and test reuse of an existing mixed-case preset subnet through the auto-provision decision.

## Warning findings

### WR-01 — Subnet allocation returns an occupied or out-of-range `/24` for narrow address spaces

**Severity:** WARNING

**File:** `src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs:63-87`.

**Issue:** For `spacePrefix >= 24`, the method immediately returns the address-space base as `/24` without checking existing subnets (lines 75-76). A `/24` VNet with that subnet already allocated returns the duplicate. A `/25` address space returns a `/24` that extends outside the configured address space. If all `/24`s in a wider range are used, the fallback at line 87 also returns the first occupied block. Auto-provisioning consumes this helper, so it can create invalid/overlapping subnet resources. The defect is still open in Phase 1 disposition L3.

**Reproduction:** Set VNet address space to `192.168.5.0/24`, add a subnet with that prefix, then call `NextFreeSubnetPrefix()`; it returns `192.168.5.0/24` again. For `192.168.5.0/25`, it returns a `/24` outside the VNet range.

**Minimal fix:** Allocate only blocks fully contained by the VNet range and check overlap using parsed network ranges, not exact string equality. Return an explicit no-capacity result (nullable/result type) instead of returning the first block on exhaustion; reject invalid address spaces instead of using the unrelated `10.0.0.0/24` fallback.

**Regression test:** Cover used `/24`, `/25`, overlapping-but-differently-spelled prefixes, full `/24` exhaustion, and malformed/missing address space; assert no returned prefix overlaps or escapes the VNet range.

### WR-02 — Address-prefix validity and overlap are not enforced before ARM export

**Severity:** WARNING

**File:** `src/AzureDesignStudio.Core/Network/SubnetModel.cs:57-63,104-117`; `src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs:34-41,109-125`; `src/AzureDesignStudio.Core/Validation/DesignValidator.cs:60-100,132-138`.

**Issue:** Subnet address prefix is a free-form string. `[Required]` is metadata only here: `SubnetModel` inherits the always-valid group implementation and `GetArmResources` populates/export the value without validation. `DesignValidator` checks subnet placement but does not validate CIDR syntax, containment within a VNet address space, or overlaps among sibling subnets. Thus malformed, out-of-range, and overlapping prefixes can reach ARM export without a blocking diagnostic. Microsoft Learn confirms that subnet address spaces cannot overlap one another ([Virtual Network FAQ](https://learn.microsoft.com/en-us/azure/virtual-network/virtual-networks-faq)).

**Reproduction:** Set a subnet prefix to `not-a-cidr`, to a valid range outside the parent VNet, or to an overlapping sibling range; run `DesignValidator.Validate` and export via the model path. No address-specific issue is produced, and the prefix is emitted.

**Minimal fix:** Add one shared IPv4 CIDR parser/range validator; report invalid syntax, out-of-parent prefixes, and sibling overlap in `DesignValidator`, and prevent direct export of invalid subnet models (without relying on UI form validation).

**Regression test:** Test malformed syntax, subnet outside each of multiple VNet prefixes, exact and partial overlap, and valid boundary-contained ranges; assert both validator diagnostics and export refusal.

### WR-03 — Missing parent-group references silently flatten nodes during load

**Severity:** WARNING

**File:** `src/AzureDesignStudio.Core/DataModelFactory.cs:301-312,314-327`.

**Issue:** When a DTO has a nonempty `GroupId` that is absent from `groupById`, group and node loading proceeds and `group?.AddChild(...)` silently does nothing. The item remains in the diagram layer as a root object, so a stale/corrupt/forward-incompatible graph loses its hierarchy without an error; a subsequent save can permanently persist the flattened structure.

**Reproduction:** Load a `DiagramGraph` containing a node DTO whose `GroupId` names no loaded group (or a child-group DTO with an unknown parent); the method returns with that object ungrouped rather than rejecting or diagnosing the graph.

**Minimal fix:** Treat a nonempty unresolved parent ID as a typed load error/diagnostic, or explicitly preserve it as an unresolved relationship; do not silently convert it to root-level placement.

**Regression test:** Add node and nested-group fixtures with missing parent IDs and assert load fails with the offending ID (and that normal valid parent-child graphs still round-trip).

## Coverage / limitations

- ARM serialization/export: traced `ArmTemplate`, `ResourceBaseJsonConverter`, `AzureNodeBase`, VNet/subnet resource creation, and related export tests; verified the findings above.
- Graph/undo: traced DTO save/load and `DiagramHistory` add/remove/move/link commands; WR-03 is the verified load integrity defect. No build/runtime execution was performed.
- Subnet/address validation: traced forms, models, cross-design validator, and `SubnetPlacementTests`; the tests cover allocator success cases but not exhaustion, narrow-space occupancy, CIDR containment, or overlap.
- Link rules: traced `AzurePort`, `LinkTypeClassifier`, DTO anchor mapping, and `LinkTypeTests`; no additional defect was confirmed in this source-only pass.
- Source generation: reviewed `AzureNodeDtoGenerator` and `RoslynExtensions` and traced generated DTO/profile/factory integration; no additional correctness defect was verified. The parse-error branch computes an `errors` string but does not use it (`AzureNodeDtoGenerator.cs:157-162`); treated as diagnostics quality, not a demonstrated product bug.
- SharedModels: included in requested scope; this review found no issue to report from protobuf contract tracing.
- Verification: current HEAD matched the requested baseline; working tree already contained user-owned untracked `.planning` artifacts at start. No source files changed, no tests/builds run per assignment, and no claims of runtime verification are made.
