using System;
using System.IO;
using System.Linq;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Orphan/idle/broken-reference detection and retirement-catalog freshness (v2 plan 7.7),
/// computed purely from typeless inventory rows - no network.
/// </summary>
public class OrphanAnalyzerTests
{
    private static InventoryItem Item(string id, string type, string? propsJson = null)
    {
        System.Text.Json.JsonElement? raw = null;
        if (propsJson is not null)
            raw = System.Text.Json.JsonDocument.Parse(propsJson).RootElement.Clone();
        return new InventoryItem(id, id.Split('/')[^1], type, "westeurope", "rg", "sub1",
            new System.Collections.Generic.Dictionary<string, string>(), raw);
    }

    private const string Vnet = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/v1";
    private const string Nic = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/n1";
    private const string Vm = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1";

    [Fact]
    public void DetachedNic_UnattachedDisk_AndUnassignedIp_AreOrphans()
    {
        var items = new[]
        {
            Item(Nic, "Microsoft.Network/networkInterfaces", """{"provisioningState":"Succeeded"}"""),
            Item("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/disks/d1", "Microsoft.Compute/disks", """{"diskState":"Unattached"}"""),
            Item("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/p1", "Microsoft.Network/publicIPAddresses", """{"publicIPAllocationMethod":"Static"}"""),
        };

        var findings = OrphanAnalyzer.Analyze(items);

        Assert.Equal(3, findings.Count(f => f.Kind == FindingKind.Orphan));
        Assert.Contains(findings, f => f.ResourceId == Nic && f.Detail.Contains("virtual machine"));
        Assert.Contains(findings, f => f.ResourceId.EndsWith("d1") && f.Detail.Contains("unattached"));
        Assert.Contains(findings, f => f.ResourceId.EndsWith("p1"));
    }

    // A Resource Graph row as the service returns it: managedBy is a column beside properties,
    // and a VM names its disks by managedDisk.id. Read only from properties, managedBy was
    // never found - every attached disk was reported as an orphan and no VM-to-disk edge existed.
    private static InventoryItem FromArg(string id, string type, string managedBy, string propertiesJson) =>
        InventoryNormalizer.FromArgRow(System.Text.Json.JsonDocument.Parse(
            "{\"id\":\"" + id + "\",\"name\":\"" + id.Split('/')[^1] + "\",\"type\":\"" + type + "\",\"location\":\"westeurope\",\"resourceGroup\":\"rg\",\"subscriptionId\":\"s\",\"tags\":{},\"managedBy\":\"" + managedBy + "\",\"properties\":" + propertiesJson + "}").RootElement);

    private const string OsDisk = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/disks/vm1_OsDisk";
    private const string DataDisk = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/disks/vm1_data0";
    private const string SpareDisk = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/disks/spare";

    private static InventoryItem[] VmWithDisksAsResourceGraphReturnsThem() =>
    [
        FromArg(Vm, "microsoft.compute/virtualmachines", "", """
            {"storageProfile":{"osDisk":{"name":"vm1_OsDisk","osType":"Linux","managedDisk":{"id":"@OS@","storageAccountType":"Premium_LRS"}},
                               "dataDisks":[{"lun":0,"name":"vm1_data0","managedDisk":{"id":"@DATA@"}}]}}
            """.Replace("@OS@", OsDisk).Replace("@DATA@", DataDisk)),
        FromArg(OsDisk, "microsoft.compute/disks", Vm, """{"diskState":"Attached","diskSizeGB":30}"""),
        FromArg(DataDisk, "microsoft.compute/disks", Vm, """{"diskState":"Attached","diskSizeGB":128}"""),
        FromArg(SpareDisk, "microsoft.compute/disks", "", """{"diskState":"Unattached","diskSizeGB":64}"""),
    ];

    [Fact]
    public void ResourceGraphRows_OnlyTheUnattachedDiskIsAnOrphan()
    {
        var orphans = OrphanAnalyzer.Analyze(VmWithDisksAsResourceGraphReturnsThem())
            .Where(f => f.Kind == FindingKind.Orphan).Select(f => f.ResourceId).ToArray();

        Assert.Equal(new[] { SpareDisk }, orphans);
    }

    [Fact]
    public void ResourceGraphRows_VmIsLinkedToItsOsAndDataDisks()
    {
        var graph = TopologyDeriver.Derive(VmWithDisksAsResourceGraphReturnsThem());

        var attached = graph.Edges.Where(e => e.Kind == TopologyEdgeKind.AttachedTo).ToList();
        Assert.Contains(attached, e => Same(e.From, Vm) && Same(e.To, OsDisk));
        Assert.Contains(attached, e => Same(e.From, Vm) && Same(e.To, DataDisk));
        Assert.Contains(attached, e => Same(e.From, DataDisk) && Same(e.To, Vm)); // from the disk's own managedBy
        Assert.DoesNotContain(attached, e => Same(e.From, SpareDisk) || Same(e.To, SpareDisk));
        Assert.DoesNotContain(graph.Nodes.Values, n => n.External); // every reference resolved to a scanned resource

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedByInsidePropertiesIsLeftAsItIs()
    {
        var item = FromArg(DataDisk, "microsoft.compute/disks", Vm, """{"managedBy":"already-there"}""");

        Assert.Equal("already-there", item.Raw!.Value.GetProperty("managedBy").GetString());
    }

    [Fact]
    public void AttachedResources_ProduceNothing()
    {
        var items = new[]
        {
            Item(Nic, "Microsoft.Network/networkInterfaces", """{"virtualMachine":{"id":"@VM@"}}""".Replace("@VM@", Vm)),
            Item("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/disks/d1", "Microsoft.Compute/disks", $$"""{"managedBy":"{{Vm}}"}"""),
            Item("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/p1", "Microsoft.Network/publicIPAddresses", """{"ipConfiguration":{"id":"@NIC@/ipConfigurations/c1"}}""".Replace("@NIC@", Nic)),
        };

        Assert.Empty(OrphanAnalyzer.Analyze(items));
    }

    [Fact]
    public void Nsg_UnassociatedIsOrphan_EmptyRulesIsIdle_ReferencedIsClean()
    {
        const string Nsg = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/networkSecurityGroups/nsg1";
        var orphan = Item(Nsg, "Microsoft.Network/networkSecurityGroups", """{"securityRules":[{"name":"r1"}]}""");
        var findings = OrphanAnalyzer.Analyze([orphan]);
        Assert.Single(findings, f => f.Kind == FindingKind.Orphan);

        var referenced = new[]
        {
            Item(Nsg, "Microsoft.Network/networkSecurityGroups", """{"securityRules":[{"name":"r1"}]}"""),
            Item(Nic, "Microsoft.Network/networkInterfaces", """{"virtualMachine":{"id":"@VM@"},"networkSecurityGroup":{"id":"@NSG@"}}""".Replace("@VM@", Vm).Replace("@NSG@", Nsg)),
        };
        Assert.Empty(OrphanAnalyzer.Analyze(referenced));

        var emptyRules = Item(Nsg, "Microsoft.Network/networkSecurityGroups", """{"securityRules":[]}""");
        var idle = OrphanAnalyzer.Analyze([emptyRules]);
        Assert.Contains(idle, f => f.Kind == FindingKind.Idle && f.Detail.Contains("no security rules"));
    }

    [Fact]
    public void LoadBalancer_WithoutListeners_IsIdle()
    {
        var lb = Item("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/loadBalancers/lb1",
            "Microsoft.Network/loadBalancers", """{"frontendIPConfigurations":[]}""");
        Assert.Contains(OrphanAnalyzer.Analyze([lb]), f => f.Kind == FindingKind.Idle);

        var used = Item("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/loadBalancers/lb1",
            "Microsoft.Network/loadBalancers", """{"loadBalancingRules":[{"name":"rdp"}]}""");
        Assert.DoesNotContain(OrphanAnalyzer.Analyze([used]), f => f.Kind == FindingKind.Idle);
    }

    [Fact]
    public void Peering_ToMissingVnet_IsBrokenReference()
    {
        const string Other = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/gone";
        var broken = Item(Vnet, "Microsoft.Network/virtualNetworks",
            """{"subnets":[{"name":"default","virtualNetworkPeerings":[{"remoteVirtualNetwork":{"id":"@OTHER@"}}]}]}""".Replace("@OTHER@", Other));

        var findings = OrphanAnalyzer.Analyze([broken]);

        Assert.Single(findings, f => f.Kind == FindingKind.BrokenReference && f.Detail.Contains("gone"));
    }

    [Fact]
    public void Peering_ToPresentVnet_IsClean()
    {
        var vnet1 = Item(Vnet, "Microsoft.Network/virtualNetworks",
            """{"subnets":[{"name":"default","virtualNetworkPeerings":[{"remoteVirtualNetwork":{"id":"@VM@"}}]}]}""".Replace("@VM@", Vm));
        var vm = Item(Vm, "Microsoft.Compute/virtualMachines", null);

        Assert.DoesNotContain(OrphanAnalyzer.Analyze([vnet1, vm]), f => f.Kind == FindingKind.BrokenReference);
    }

    [Fact]
    public void Catalog_FreshnessTiers_WildcardMatch_AndWatch()
    {
        var asOf = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = DeprecationCatalog.Seed;

        Assert.Equal(CatalogFreshness.Current, catalog.FreshnessAt(asOf.AddDays(30)));
        Assert.Equal(CatalogFreshness.ReviewDue, catalog.FreshnessAt(asOf.AddDays(120)));
        Assert.Equal(CatalogFreshness.Stale, catalog.FreshnessAt(asOf.AddDays(400)));

        Assert.Equal("retired", catalog.Match("Microsoft.ClassicStorage/storageAccounts")?.Status);
        Assert.Equal("retired", catalog.Match("Microsoft.ClassicCompute/somethingNew")?.Status); // wildcard
        Assert.Null(catalog.Match("Microsoft.Network/virtualNetworks"));

        var watch = catalog.Watch(
        [
            Item("/subscriptions/s/resourceGroups/rg/providers/Microsoft.ClassicCompute/virtualMachines/legacy", "Microsoft.ClassicCompute/virtualMachines", null),
            Item(Vnet, "Microsoft.Network/virtualNetworks", null),
        ]);
        Assert.Single(watch);
        Assert.Equal("legacy", watch[0].Item.Name);
    }

    [Fact]
    public void Catalog_LoadsSnapshotAndFallsBackOnCorruptFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ads-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = Path.Combine(dir, "catalog.json");
            File.WriteAllText(good, """{"dataAsOf":"2026-01-01T00:00:00+00:00","reviewIntervalDays":30,"entries":[{"resourceType":"Microsoft.Test/x","status":"retiring","retirementDate":null,"note":"soon"}]}""");
            var loaded = DeprecationCatalog.Load(good);
            Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), loaded.DataAsOf);
            Assert.Equal("retiring", loaded.Match("Microsoft.Test/x")?.Status);

            var bad = Path.Combine(dir, "broken.json");
            File.WriteAllText(bad, "{ not json");
            Assert.Same(DeprecationCatalog.Seed, DeprecationCatalog.Load(bad));
            Assert.Same(DeprecationCatalog.Seed, DeprecationCatalog.Load(null));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
