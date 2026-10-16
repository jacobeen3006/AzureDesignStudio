using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Security;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// [7] structural orphan detection. The contract: candidates come only from attachment
/// facts the Reader scan carries, contradictions resolve toward "attached" (never claim),
/// every row names its evidence, and the blind spots are permanent caveats.
/// </summary>
public class OrphanDetectorTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Rg = Sub + "/resourceGroups/rg";

    private static InventoryItem Item(string name, string type, string? props = null) =>
        new($"{Rg}/providers/{type}/{name}", name, type, "westeurope", "rg", "sub1",
            new Dictionary<string, string>(),
            props is null ? null : JsonDocument.Parse(props).RootElement.Clone());

    [Fact]
    public void DiskWithoutManagedBy_IsUnattached_WithEvidence()
    {
        var audit = OrphanDetector.Analyze([Item("d1", "Microsoft.Compute/disks", """{"diskState":"Unattached"}""")]);
        var row = Assert.Single(audit.Rows);
        Assert.Equal(OrphanKind.UnattachedDisk, row.Kind);
        Assert.Contains("diskState: Unattached", row.Evidence);
    }

    [Fact]
    public void AttachedDisk_NeverClaimed()
    {
        var audit = OrphanDetector.Analyze([Item("d1", "Microsoft.Compute/disks", """{"managedBy":"/subscriptions/sub1/providers/Microsoft.Compute/virtualMachines/vm1"}""")]);
        Assert.Empty(audit.Rows);
    }

    [Fact]
    public void DiskContradiction_ResolvesTowardAttached()
    {
        // no managedBy but diskState says Attached - a provider contradiction; claiming
        // an attached disk as orphan is the worse error, so stay silent.
        var audit = OrphanDetector.Analyze([Item("d1", "Microsoft.Compute/disks", """{"diskState":"Attached"}""")]);
        Assert.Empty(audit.Rows);
    }

    [Fact]
    public void BothSignalsMissing_StillUnattached_NamingTheGap()
    {
        var audit = OrphanDetector.Analyze([Item("d1", "Microsoft.Compute/disks", """{"provisioningState":"succeeded"}""")]);
        var row = Assert.Single(audit.Rows);
        Assert.Contains("no managedBy and no diskState", row.Evidence);
    }

    [Fact]
    public void IpRules_Attached_EmptyOrphan_MissingOrphan_PrefixExcepted()
    {
        var attached = Item("p1", "Microsoft.Network/publicIPAddresses", """{"ipConfiguration":{"id":"/x/nic/ipConfig1"}}""");
        var empty = Item("p2", "Microsoft.Network/publicIPAddresses", """{"ipConfiguration":[]}""");
        var missing = Item("p3", "Microsoft.Network/publicIPAddresses", """{"provisioningState":"succeeded"}""");
        var prefixMember = Item("p4", "Microsoft.Network/publicIPAddresses", """{"publicIPPrefix":{"id":"/x/prefix1"},"ipConfiguration":[]}""");
        var audit = OrphanDetector.Analyze([attached, empty, missing, prefixMember]);
        Assert.Equal(["p2", "p3"], audit.Rows.Select(r => r.Name));
        Assert.All(audit.Rows, r => Assert.Equal(OrphanKind.UnassociatedIp, r.Kind));
        Assert.Contains("empty", audit.Rows.First(r => r.Name == "p2").Evidence);
        Assert.Contains("no ipConfiguration", audit.Rows.First(r => r.Name == "p3").Evidence);
    }

    [Fact]
    public void IpUnusableConfigurationShape_SaysNothing()
    {
        var audit = OrphanDetector.Analyze([Item("p1", "Microsoft.Network/publicIPAddresses", """{"ipConfiguration":"corrupt"}""")]);
        Assert.Empty(audit.Rows);
    }

    [Fact]
    public void NicWithoutVm_IsCandidate_VerifyWordingCarried()
    {
        var withVm = Item("n1", "Microsoft.Network/networkInterfaces", """{"virtualMachine":{"id":"/x/vm1"}}""");
        var bare = Item("n2", "Microsoft.Network/networkInterfaces", """{"provisioningState":"succeeded"}""");
        var audit = OrphanDetector.Analyze([withVm, bare]);
        var row = Assert.Single(audit.Rows);
        Assert.Equal("n2", row.Name);
        Assert.Equal(OrphanKind.NicWithoutVm, row.Kind);
        Assert.Contains("private endpoint", row.Evidence);
    }

    [Fact]
    public void NsgUsage_FromVnetSubnetsAndNics_CaseInsensitive()
    {
        var usedBySubnet = Item("sg1", "Microsoft.Network/networkSecurityGroups");
        var usedByNic = Item("sg2", "Microsoft.Network/networkSecurityGroups");
        var unreferenced = Item("sg3", "Microsoft.Network/networkSecurityGroups");
        var vnet = Item("v1", "Microsoft.Network/virtualnetworks",
            """{"subnets":[{"name":"s","networkSecurityGroup":{"id":"/SUBSCRIPTIONS/SUB1/RESOURCEGROUPS/RG/providers/Microsoft.Network/networkSecurityGroups/sg1"}}]}""");
        var nic = Item("n1", "Microsoft.Network/networkInterfaces",
            """{"virtualMachine":{"id":"/x/vm1"},"networkSecurityGroup":{"id":"/Subscriptions/Sub1/resourceGroups/RG/providers/Microsoft.Network/networkSecurityGroups/SG2"}}""");
        var audit = OrphanDetector.Analyze([usedBySubnet, usedByNic, unreferenced, vnet, nic]);
        var row = Assert.Single(audit.Rows);
        Assert.Equal("sg3", row.Name);
        Assert.Equal(OrphanKind.NsgUnreferenced, row.Kind);
        Assert.Contains("outside the scan", row.Evidence);
    }

    [Fact]
    public void OutputIsDeterministic_KindThenId()
    {
        var items = new[]
        {
            Item("b-nic", "Microsoft.Network/networkInterfaces", """{"provisioningState":"succeeded"}"""),
            Item("a-disk", "Microsoft.Compute/disks", """{"diskState":"Unattached"}"""),
            Item("z-nsg", "Microsoft.Network/networkSecurityGroups"),
            Item("c-ip", "Microsoft.Network/publicIPAddresses", """{"provisioningState":"succeeded"}"""),
        };
        var first = OrphanDetector.Analyze(items);
        var shuffled = OrphanDetector.Analyze([items[2], items[3], items[0], items[1]]);
        Assert.Equal(first.Rows.Select(r => (r.Kind, r.Id)), shuffled.Rows.Select(r => (r.Kind, r.Id)));
        Assert.Equal(4, first.Rows.Count);
    }

    [Fact]
    public void OtherFamilies_AreUntouched()
    {
        var audit = OrphanDetector.Analyze([Item("vm1", "Microsoft.Compute/virtualMachines", """{}""")]);
        Assert.Empty(audit.Rows);
        Assert.Equal(1, audit.ScannedResources);
    }

    [Fact]
    public void Caveats_AlwaysCarried()
    {
        var audit = OrphanDetector.Analyze([]);
        Assert.Contains(audit.Caveats, c => c.Contains("private endpoint", StringComparison.Ordinal));
        Assert.Contains(audit.Caveats, c => c.Contains("metrics", StringComparison.Ordinal));
        Assert.Contains(audit.Caveats, c => c.Contains("outside", StringComparison.Ordinal));
    }
}
