using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Unmanaged;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// 13.2 decommission planner ([63][64][65]). Fixtures mirror the ARM property shapes the
/// 8.1 deriver consumes, so ordering and refusals are pinned against the same config-derived
/// edges the topology draws. The core invariants: nothing refuses that ARM would allow, and
/// nothing stages that ARM would fail.
/// </summary>
public class DecommissionPlannerTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Rg = Sub + "/resourceGroups/rg";
    private const string Vnet = Rg + "/providers/Microsoft.Network/virtualNetworks/vnet1";
    private const string SubnetA = Vnet + "/subnets/subnetA";
    private const string Nic = Rg + "/providers/Microsoft.Network/networkInterfaces/nic1";
    private const string Vm = Rg + "/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string Disk = Rg + "/providers/Microsoft.Compute/disks/disk1";
    private const string Nsg = Rg + "/providers/Microsoft.Network/networkSecurityGroups/nsg1";
    private const string Vnet2 = Sub + "/resourceGroups/rg2/providers/Microsoft.Network/virtualNetworks/vnet2";
    private const string Law = Rg + "/providers/Microsoft.OperationalInsights/workspaces/law1";
    private const string Kv = Rg + "/providers/Microsoft.KeyVault/vaults/kv1";
    private const string Vault = Rg + "/providers/Microsoft.RecoveryServices/vaults/rv1";

    private static InventoryItem Item(string id, string type, string? propsJson = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        JsonElement? raw = null;
        if (propsJson is not null)
            raw = JsonDocument.Parse(propsJson).RootElement.Clone();
        return new InventoryItem(id, id.Split('/')[^1], type, "westeurope", "rg", "sub1",
            tags ?? new Dictionary<string, string>(), raw);
    }

    private static string Fill(string template) => template
        .Replace("__VM__", Vm).Replace("__NIC__", Nic).Replace("__DISK__", Disk)
        .Replace("__VNET__", Vnet).Replace("__SUBNET__", SubnetA).Replace("__NSG__", Nsg)
        .Replace("__LAW__", Law).Replace("__VNET2__", Vnet2);

    private static InventoryItem VmItem() => Item(Vm, "microsoft.compute/virtualmachines",
        Fill("""{"networkProfile":{"networkInterfaces":[{"id":"__NIC__"}]},"storageProfile":{"dataDisks":[{"managedDisk":{"name":"disk1"}}]}}"""));

    private static InventoryItem NicItem() => Item(Nic, "microsoft.network/networkinterfaces",
        Fill("""{"ipConfigurations":[{"properties":{"subnet":{"id":"__SUBNET__"}}}]}"""));

    private static InventoryItem DiskItem() => Item(Disk, "microsoft.compute/disks",
        Fill("""{"managedBy":"__VM__"}"""));

    private static InventoryItem VnetItem() => Item(Vnet, "microsoft.network/virtualNetworks",
        Fill("""{"subnets":[{"name":"subnetA","properties":{}}]}"""));

    private static DecommissionPlan PlanAll(IReadOnlyList<InventoryItem> inventory, IEnumerable<string> candidates) =>
        DecommissionPlanner.Plan(inventory, candidates,
            guardrails: GuardrailPolicy.Empty, locks: [], roleAssignments: [], backupItems: []);

    // --- [63] staged order ---

    [Fact]
    public void VmNicDiskVnetChain_StagesDependentsBeforeDependencies()
    {
        var plan = PlanAll(new[] { VnetItem(), NicItem(), VmItem(), DiskItem() }, new[] { Vnet, Nic, Vm, Disk });

        Assert.True(plan.Allowed);
        Assert.Equal(
            new[] { new[] { Vm }, new[] { Disk, Nic }, new[] { Vnet } },
            plan.Stages.Select(s => s.ToArray()).ToArray());
    }

    [Fact]
    public void PeeredNetworks_BothSelected_ImposesNoOrder()
    {
        var items = new[]
        {
            Item(Vnet, "microsoft.network/virtualNetworks", Fill("""{"virtualNetworkPeerings":[{"name":"p","properties":{"remoteVirtualNetwork":{"id":"__VNET2__"}}}]}""")),
            Item(Vnet2, "microsoft.network/virtualNetworks", Fill("""{"virtualNetworkPeerings":[{"name":"p","properties":{"remoteVirtualNetwork":{"id":"__VNET__"}}}]}""")),
        };
        var plan = PlanAll(items, new[] { Vnet, Vnet2 });

        Assert.True(plan.Allowed);
        Assert.Single(plan.Stages);
    }

    [Fact]
    public void CandidateNotInScan_Refuses()
    {
        var plan = PlanAll(new[] { VmItem() }, new[] { Rg + "/providers/Microsoft.Compute/virtualMachines/ghost" });

        Assert.False(plan.Allowed);
        Assert.Equal("candidate-not-in-scan", Assert.Single(plan.Blockers).Kind);
        Assert.Empty(plan.Stages);
    }

    // --- [64] dependency-safe refusal ---

    [Fact]
    public void ExternalVmUsingCandidateNic_RefusesDelete()
    {
        var plan = PlanAll(new[] { VmItem(), NicItem() }, new[] { Nic });

        Assert.False(plan.Allowed);
        var blocker = Assert.Single(plan.Blockers, b => b.Severity == DecommissionSeverity.Refusal);
        Assert.Equal(Nic, blocker.ResourceId);
        Assert.Equal("referenced-externally", blocker.Kind);
        Assert.Contains(Vm, blocker.Detail);
    }

    [Fact]
    public void CandidateVmWithExternalNic_WarnsOrphanSurvivor_NotRefusal()
    {
        var plan = PlanAll(new[] { VmItem(), NicItem() }, new[] { Vm });

        Assert.True(plan.Allowed);
        var impact = Assert.Single(plan.Impacts, i => i.Kind == "survivor-orphan");
        Assert.Equal(DecommissionSeverity.Warning, impact.Severity);
        Assert.Contains(Nic, impact.Detail);
    }

    [Fact]
    public void CandidateDiskStillAttachedToLiveVm_Refuses()
    {
        var plan = PlanAll(new[] { VmItem(), DiskItem() }, new[] { Disk });

        Assert.False(plan.Allowed);
        Assert.Contains(plan.Blockers, b => b.Kind == "disk-attached" && b.Severity == DecommissionSeverity.Refusal);
    }

    [Fact]
    public void CandidateVmWithAttachedDisk_WarnsDetachment()
    {
        var plan = PlanAll(new[] { VmItem(), DiskItem() }, new[] { Vm });

        Assert.True(plan.Allowed);
        Assert.Contains(plan.Impacts, i => i.Kind == "disks-detach");
    }

    [Fact]
    public void ExternalConsumerOfCandidateWorkspace_WarnsDiagnosticsBreak()
    {
        var items = new[]
        {
            Item(Law, "microsoft.operationalinsights/workspaces"),
            Item(Rg + "/providers/Microsoft.Insights/diagnosticSettings/ds1", "microsoft.insights/diagnosticsettings",
                Fill("""{"resourceUri":"__VM__","workspaceId":"__LAW__"}""")),
        };
        var plan = PlanAll(items, new[] { Law });

        Assert.True(plan.Allowed);
        var impact = Assert.Single(plan.Impacts, i => i.Kind == "diagnostics-break");
        Assert.Contains(Vm, impact.Detail);
    }

    [Fact]
    public void ExternalPeeringPeer_WarnsExactlyOnce()
    {
        var items = new[]
        {
            Item(Vnet, "microsoft.network/virtualNetworks", Fill("""{"virtualNetworkPeerings":[{"name":"p","properties":{"remoteVirtualNetwork":{"id":"__VNET2__"}}}]}""")),
            Item(Vnet2, "microsoft.network/virtualNetworks", Fill("""{"virtualNetworkPeerings":[{"name":"p","properties":{"remoteVirtualNetwork":{"id":"__VNET__"}}}]}""")),
        };
        var plan = PlanAll(items, new[] { Vnet });

        Assert.True(plan.Allowed);
        var impact = Assert.Single(plan.Impacts, i => i.Kind == "peering-removed");
        Assert.Equal(Vnet, impact.ResourceId);
    }

    [Fact]
    public void SelectedParentWithExternalSubnetConsumer_Refuses()
    {
        // The NIC references the subnet; the subnet is an unselected child of the selected vnet -
        // edges to cascade children must collapse onto the parent, or the refusal disappears.
        var plan = PlanAll(new[] { VnetItem(), NicItem() }, new[] { Vnet });

        Assert.False(plan.Allowed);
        var blocker = Assert.Single(plan.Blockers, b => b.Kind == "referenced-externally");
        Assert.Equal(Vnet, blocker.ResourceId);
        Assert.Contains(Nic, blocker.Detail);
    }

    [Fact]
    public void CascadeChildren_ReportedNotStaged()
    {
        var items = new[]
        {
            VnetItem(),
            Item(SubnetA, "microsoft.network/virtualNetworks/subnets"),
        };
        var plan = PlanAll(items, new[] { Vnet });

        Assert.True(plan.Allowed);
        Assert.Equal(new[] { Vnet }, Assert.Single(plan.Stages));
        var cascade = Assert.Single(plan.ImplicitChildren);
        Assert.Equal(Vnet, cascade.ParentId);
        Assert.Equal(SubnetA, cascade.ChildId);
    }

    [Fact]
    public void MutuallyReferencingSites_RefusesWithNamedCycle()
    {
        var siteA = Rg + "/providers/Microsoft.Web/sites/site-a";
        var siteB = Rg + "/providers/Microsoft.Web/sites/site-b";
        var items = new[]
        {
            Item(siteA, "microsoft.web/sites", $$"""{"serverFarmId":"{{siteB}}"}"""),
            Item(siteB, "microsoft.web/sites", $$"""{"serverFarmId":"{{siteA}}"}"""),
        };
        var plan = PlanAll(items, new[] { siteA, siteB });

        Assert.False(plan.Allowed);
        var cycle = Assert.Single(plan.Blockers, b => b.Kind == "dependency-cycle");
        Assert.Contains("->", cycle.Detail);
        Assert.Contains(siteA, cycle.Detail);
        Assert.Contains(siteB, cycle.Detail);
        Assert.Empty(plan.Stages);
    }

    // --- guardrails, locks, RBAC, backups ---

    [Fact]
    public void GuardrailProtectedCandidate_RefusesWithMatchedPattern()
    {
        var policy = GuardrailPolicy.FromJson("""{"types":["microsoft.compute/virtualmachines"]}""");
        var plan = DecommissionPlanner.Plan(new[] { VmItem() }, new[] { Vm },
            guardrails: policy, locks: [], roleAssignments: [], backupItems: []);

        Assert.False(plan.Allowed);
        var blocker = Assert.Single(plan.Blockers, b => b.Kind == "guardrail");
        Assert.Contains("guardrail-type", blocker.Detail);
    }

    [Fact]
    public void CanNotDeleteLock_InheritedFromResourceGroup_Refuses()
    {
        var plan = DecommissionPlanner.Plan(new[] { VmItem() }, new[] { Vm },
            guardrails: GuardrailPolicy.Empty,
            locks: [new ArmLock(Rg, ArmLockLevel.CanNotDelete, "prod-lock")],
            roleAssignments: [], backupItems: []);

        Assert.False(plan.Allowed);
        var blocker = Assert.Single(plan.Blockers, b => b.Kind == "lock-can-not-delete");
        Assert.Contains("inherited from scope", blocker.Detail);
        Assert.Contains("prod-lock", blocker.Detail);
    }

    [Fact]
    public void DenyAssignmentsLock_IsInfoDoesNotRefuse()
    {
        var plan = DecommissionPlanner.Plan(new[] { VmItem() }, new[] { Vm },
            guardrails: GuardrailPolicy.Empty,
            locks: [new ArmLock(Vm, ArmLockLevel.DenyAssignments, "rbac-deny")],
            roleAssignments: [], backupItems: []);

        Assert.True(plan.Allowed);
        Assert.Equal(DecommissionSeverity.Info, Assert.Single(plan.Blockers).Severity);
    }

    [Fact]
    public void RbacScopedOnResource_ListedAsImpact()
    {
        var plan = DecommissionPlanner.Plan(new[] { VmItem() }, new[] { Vm },
            guardrails: GuardrailPolicy.Empty, locks: [],
            roleAssignments: [new ScopeRoleAssignment(Vm, "Contributor", "alice")],
            backupItems: []);

        Assert.True(plan.Allowed);
        var impact = Assert.Single(plan.Impacts, i => i.Kind == "rbac-assignments");
        Assert.Contains("Contributor", impact.Detail);
        Assert.Contains("alice", impact.Detail);
    }

    [Fact]
    public void BackupProtectedVm_Refuses()
    {
        var plan = DecommissionPlanner.Plan(new[] { VmItem() }, new[] { Vm },
            guardrails: GuardrailPolicy.Empty, locks: [], roleAssignments: [],
            backupItems: [new BackupProtectionItem(Vault, Vm)]);

        Assert.False(plan.Allowed);
        var blocker = Assert.Single(plan.Blockers, b => b.Kind == "backup-protected");
        Assert.Contains(Vault, blocker.Detail);
    }

    [Fact]
    public void NonEmptyBackupVault_Refuses()
    {
        var items = new[]
        {
            Item(Vault, "microsoft.recoveryservices/vaults"),
            VmItem(),
        };
        var plan = DecommissionPlanner.Plan(items, new[] { Vault },
            guardrails: GuardrailPolicy.Empty, locks: [], roleAssignments: [],
            backupItems: [new BackupProtectionItem(Vault, Vm)]);

        Assert.False(plan.Allowed);
        Assert.Contains(plan.Blockers, b => b.Kind == "backup-vault-nonempty");
    }

    // --- [65] delete-impact preview ---

    [Fact]
    public void KeyVaultDefaults_SoftDeleteInfoWithNinetyDays()
    {
        var plan = PlanAll(new[] { Item(Kv, "microsoft.keyvault/vaults") }, new[] { Kv });

        var impact = Assert.Single(plan.Impacts, i => i.Kind == "keyvault-soft-delete");
        Assert.Contains("90 days", impact.Detail);
        Assert.DoesNotContain(plan.Impacts, i => i.Kind == "keyvault-purge-protection");
    }

    [Fact]
    public void KeyVaultPurgeProtectionAndNoSoftDelete_Reported()
    {
        var item = Item(Kv, "microsoft.keyvault/vaults",
            """{"enableSoftDelete":false,"enablePurgeProtection":true,"softDeleteRetentionInDays":14}""");
        var plan = PlanAll(new[] { item }, new[] { Kv });

        Assert.Contains(plan.Impacts, i => i.Kind == "keyvault-hard-delete" && i.Severity == DecommissionSeverity.Warning);
        Assert.Contains(plan.Impacts, i => i.Kind == "keyvault-purge-protection");
    }

    [Fact]
    public void StorageImmutableFlag_ChangesTheDestructionDetail()
    {
        var plain = PlanAll(new[] { Item(Rg + "/providers/Microsoft.Storage/storageaccounts/stplain", "microsoft.storage/storageaccounts") },
            new[] { Rg + "/providers/Microsoft.Storage/storageaccounts/stplain" });
        Assert.Contains("child scopes", Assert.Single(plain.Impacts).Detail);

        var immutable = PlanAll(new[] { Item(Rg + "/providers/Microsoft.Storage/storageaccounts/stim", "microsoft.storage/storageaccounts",
            """{"immutableStorageWithVersioning":{"enabled":true}}""") },
            new[] { Rg + "/providers/Microsoft.Storage/storageaccounts/stim" });
        Assert.Contains("immutable storage is enabled", Assert.Single(immutable.Impacts).Detail);
    }

    // --- honesty and determinism ---

    [Fact]
    public void NullEvidence_ListsEveryNotEvaluatedCaveat()
    {
        var plan = DecommissionPlanner.Plan(new[] { VmItem() }, new[] { Vm });

        Assert.Contains(plan.Caveats, c => c.StartsWith("guardrails-not-supplied"));
        Assert.Contains(plan.Caveats, c => c.StartsWith("locks-not-supplied"));
        Assert.Contains(plan.Caveats, c => c.StartsWith("role-assignments-not-supplied"));
        Assert.Contains(plan.Caveats, c => c.StartsWith("backup-items-not-supplied"));
    }

    [Fact]
    public void SuppliedEvidence_ListsNoCaveats()
    {
        var plan = PlanAll(new[] { VmItem() }, new[] { Vm });
        Assert.Empty(plan.Caveats);
    }

    [Fact]
    public void CaseInsensitiveCandidates_OutputsCarryScanCasing()
    {
        var plan = PlanAll(new[] { VmItem(), NicItem() }, new[] { Vm.ToUpperInvariant() });

        Assert.Equal(new[] { Vm }, plan.Candidates);
        Assert.Equal(new[] { Vm }, plan.Stages.Single());
    }

    [Fact]
    public void ShuffledInputs_ProduceIdenticalSerialization()
    {
        var items = new[] { VmItem(), NicItem(), DiskItem(), VnetItem() };
        var candidates = new[] { Vnet, Nic, Vm, Disk };

        var first = JsonSerializer.Serialize(PlanAll(items, candidates));
        var second = JsonSerializer.Serialize(PlanAll(items.Reverse().ToList(), candidates.Reverse()));

        Assert.Equal(first, second);
    }

    [Fact]
    public void EmptyCandidates_AllowsWithNoStages()
    {
        var plan = PlanAll(new[] { VmItem() }, []);

        Assert.True(plan.Allowed);
        Assert.Empty(plan.Stages);
        Assert.Empty(plan.Candidates);
    }
}
