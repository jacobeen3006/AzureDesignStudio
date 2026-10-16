using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 [16]: reachability answers only what the snapshot proves, and says what it never inspected.</summary>
public class ReachabilityEngineTests
{
    private const string Sub = "/subscriptions/SUB-1";
    private static string Rg(string suffix) => $"{Sub}/resourceGroups/rg-a/providers/{suffix}";

    private static InventoryItem Item(string id, string type, string name, string? props = null) =>
        new(id, name, type, "westeurope", "rg-a", "SUB-1", new Dictionary<string, string>(),
            props is null ? null : JsonDocument.Parse(props).RootElement.Clone());

    private static readonly InventoryItem[] Empty = [];

    [Fact]
    public void TargetOutsideScan_IsHonestErrorAndNeverAClaim()
    {
        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Whatever/x"), [Item(Rg("Microsoft.Compute/virtualMachines/vm1"), "Microsoft.Compute/virtualMachines", "vm1")]);

        Assert.False(answer.InternetReachable);
        Assert.Empty(answer.Paths);
        Assert.Contains(answer.Diagnostics, d => d.Code == "ReachTargetNotInScan");
        Assert.Contains(answer.Caveats, c => c.StartsWith("internet-path-not-proven"));
    }

    [Fact]
    public void PublicIpAttachedToVm_IsAProvenInternetPath()
    {
        var inv = new[]
        {
            Item(Rg("Microsoft.Compute/virtualMachines/vm1"), "Microsoft.Compute/virtualMachines", "vm1",
                """{"networkProfile":{"networkInterfaces":[{"id":"/subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/networkInterfaces/nic1"}]}}"""),
            Item(Rg("Microsoft.Network/networkInterfaces/nic1"), "Microsoft.Network/networkInterfaces", "nic1",
                """{"ipConfigurations":[{"name":"c","properties":{"publicIPAddress":{"id":"/subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/publicIPAddresses/pip1"}}}]}"""),
            Item(Rg("Microsoft.Network/publicIPAddresses/pip1"), "Microsoft.Network/publicIPAddresses", "pip1",
                """{"ipAddress":"20.30.40.50","sku":{"name":"Standard"}}"""),
        };

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Compute/virtualMachines/vm1"), inv);

        Assert.True(answer.InternetReachable);
        Assert.Contains(answer.Paths, p => p.Origin == ReachOrigin.Internet && p.Evidence.Any(e => e.StartsWith("vm-public-ip")));
        var viaNic = answer.Paths.Single(p => p.Via.EndsWith("pip1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("vm1", viaNic.Hops[^1]);
        Assert.Contains(viaNic.Hops, h => h.StartsWith("NIC nic1"));
    }

    [Fact]
    public void PublicPaaSEndpoint_WithoutPrivateEndpoint_IsInternetPath_ForStorage()
    {
        var storage = Item(Rg("Microsoft.Storage/storageAccounts/stprod"), "Microsoft.Storage/storageAccounts", "stprod",
            """{"primaryEndpoints":{"blob":"https://stprod.blob.core.windows.net/"}}""");

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Storage/storageAccounts/stprod"), [storage]);

        Assert.True(answer.InternetReachable);
        Assert.Contains(answer.Paths.Single().Evidence, e => e.StartsWith("paas-public-endpoint"));
    }

    [Fact]
    public void PrivateEndpoint_ConnectsTarget_AndSuppressesThePublicPaasFinding()
    {
        var storage = Item(Rg("Microsoft.Storage/storageAccounts/stprod"), "Microsoft.Storage/storageAccounts", "stprod",
            """{"primaryEndpoints":{"blob":"https://stprod.blob.core.windows.net/"}}""");
        var pe = Item(Rg("Microsoft.Network/privateEndpoints/pe1"), "Microsoft.Network/privateEndpoints", "pe1",
            """{"subnet":{"id":"@@SUB@@"},"privateLinkServiceConnections":[{"properties":{"privateLinkServiceId":"@@ST@@"}}]}""".Replace("@@SUB@@", Rg("Microsoft.Network/virtualNetworks/vnet1/subnets/snet1")).Replace("@@ST@@", Rg("Microsoft.Storage/storageAccounts/stprod")));

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Storage/storageAccounts/stprod"), [storage, pe]);

        Assert.False(answer.InternetReachable); // exposure engine suppresses the public finding behind the PE
        var via = Assert.Single(answer.Paths);
        Assert.Equal(ReachOrigin.PrivateEndpoint, via.Origin);
        Assert.Contains("private endpoint pe1", via.Hops[0]);
    }

    [Fact]
    public void InternetOpenNsgOnSubnet_ReachesTargetThroughTheNsgPath()
    {
        var inv = new[]
        {
            Item(Rg("Microsoft.Compute/virtualMachines/vm1"), "Microsoft.Compute/virtualMachines", "vm1",
                """{"networkProfile":{"networkInterfaces":[{"id":"/subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/networkInterfaces/nic1"}]}}"""),
            Item(Rg("Microsoft.Network/networkInterfaces/nic1"), "Microsoft.Network/networkInterfaces", "nic1",
                """{"ipConfigurations":[{"name":"c","properties":{"subnet":{"id":"@@SUB@@"}}}]}""".Replace("@@SUB@@", Rg("Microsoft.Network/virtualNetworks/vnet1/subnets/snet1"))),
            Item(Rg("Microsoft.Network/virtualNetworks/vnet1"), "Microsoft.Network/virtualNetworks", "vnet1",
                """{"subnets":[{"name":"snet1","properties":{"networkSecurityGroup":{"id":"@@NSG@@"}}}]}""".Replace("@@NSG@@", Rg("Microsoft.Network/networkSecurityGroups/nsg-open"))),
            Item(Rg("Microsoft.Network/networkSecurityGroups/nsg-open"), "Microsoft.Network/networkSecurityGroups", "nsg-open",
                """{"securityRules":[{"name":"openall","properties":{"direction":"Inbound","access":"Allow","sourceAddressPrefix":"*","destinationPortRange":"*"}}]}"""),
        };

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Compute/virtualMachines/vm1"), inv);

        Assert.True(answer.InternetReachable);
        var nsgPath = Assert.Single(answer.Paths, p => p.Via.EndsWith("nsg-open", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("subnet snet1", nsgPath.Hops);
        Assert.Contains(nsgPath.Evidence, e => e.StartsWith("nsg-open-all-inbound"));
    }

    [Fact]
    public void LoadBalancerPublicFrontend_WithTargetInBackend_IsAnInternetPathViaGateway()
    {
        var nicCfg = Rg("Microsoft.Network/networkInterfaces/nic1/ipConfigurations/c1");
        var inv = new[]
        {
            Item(Rg("Microsoft.Compute/virtualMachines/vm1"), "Microsoft.Compute/virtualMachines", "vm1",
                """{"networkProfile":{"networkInterfaces":[{"id":"/subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/networkInterfaces/nic1"}]}}"""),
            Item(Rg("Microsoft.Network/loadBalancers/lb1"), "Microsoft.Network/loadBalancers", "lb1",
                """{"backendAddressPools":[{"name":"bepool","properties":{"backendIPConfigurations":[{"id":"@@CFG@@"}]}}],"frontendIPConfigurations":[{"name":"fe","properties":{"publicIPAddress":{"id":"@@PIP@@"}}}]}""".Replace("@@CFG@@", nicCfg).Replace("@@PIP@@", Rg("Microsoft.Network/publicIPAddresses/pip-fe"))),
            Item(Rg("Microsoft.Network/publicIPAddresses/pip-fe"), "Microsoft.Network/publicIPAddresses", "pip-fe", """{"ipAddress":"20.1.2.3"}"""),
        };

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Compute/virtualMachines/vm1"), inv);

        var via = Assert.Single(answer.Paths, p => p.Via.EndsWith("lb1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ReachOrigin.Internet, via.Origin);
        Assert.Contains("backend nic1", via.Hops);
        Assert.Contains(via.Evidence, e => e.StartsWith("lb-frontend-public"));
    }

    [Fact]
    public void PeeredVnet_ListsPeerVnetAndItsScanResources()
    {
        var inv = new[]
        {
            Item(Rg("Microsoft.Network/networkInterfaces/nic1"), "Microsoft.Network/networkInterfaces", "nic1",
                """{"ipConfigurations":[{"name":"c","properties":{"subnet":{"id":"@@SUB@@"}}}]}""".Replace("@@SUB@@", Rg("Microsoft.Network/virtualNetworks/vnet1/subnets/snet1"))),
            Item(Rg("Microsoft.Network/virtualNetworks/vnet1"), "Microsoft.Network/virtualNetworks", "vnet1",
                """{"subnets":[{"name":"snet1"}],"virtualNetworkPeerings":[{"name":"peer-to-2","properties":{"remoteVirtualNetwork":{"id":"@@R@@"}}}]}""".Replace("@@R@@", Rg("Microsoft.Network/virtualNetworks/vnet2"))),
            Item(Rg("Microsoft.Network/virtualNetworks/vnet2"), "Microsoft.Network/virtualNetworks", "vnet2",
                """{"subnets":[{"name":"snet2"}],"virtualNetworkPeerings":[{"name":"peer-to-1","properties":{"remoteVirtualNetwork":{"id":"@@R@@"}}}]}""".Replace("@@R@@", Rg("Microsoft.Network/virtualNetworks/vnet1"))),
            Item(Rg("Microsoft.Network/networkInterfaces/nic2"), "Microsoft.Network/networkInterfaces", "nic2",
                """{"ipConfigurations":[{"name":"c","properties":{"subnet":{"id":"@@SUB@@"}}}]}""".Replace("@@SUB@@", Rg("Microsoft.Network/virtualNetworks/vnet2/subnets/snet2"))),
        };

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Network/networkInterfaces/nic1"), inv);

        var peerPaths = answer.Paths.Where(p => p.Origin == ReachOrigin.PeerVnet).ToList();
        Assert.Contains(peerPaths, p => p.Via.EndsWith("vnet2", StringComparison.OrdinalIgnoreCase));
        var toV2 = peerPaths.Single(p => p.Via.EndsWith("vnet2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(toV2.Evidence, e => e.Contains("nic2"));
        // determinism: same input in shuffled order answers identically
        var shuffled = inv.Reverse().ToArray();
        var again = ReachabilityEngine.Analyze(Rg("Microsoft.Network/networkInterfaces/nic1"), shuffled);
        Assert.Equal(string.Join("|", peerPaths.Select(p => (p.Origin, p.Via, string.Join(",", p.Hops), string.Join(",", p.Evidence)))),
            string.Join("|", again.Paths.Where(p => p.Origin == ReachOrigin.PeerVnet).Select(p => (p.Origin, p.Via, string.Join(",", p.Hops), string.Join(",", p.Evidence)))));
    }

    [Fact]
    public void SameSubnetNeighbours_AreListedOnceAsSameSubnetPath()
    {
        var subnetId = Rg("Microsoft.Network/virtualNetworks/vnet1/subnets/snet1");
        var inv = new[]
        {
            Item(Rg("Microsoft.Network/networkInterfaces/nicA"), "Microsoft.Network/networkInterfaces", "nicA",
                """{"ipConfigurations":[{"name":"c","properties":{"subnet":{"id":"@@S@@"}}}]}""".Replace("@@S@@", subnetId)),
            Item(Rg("Microsoft.Network/networkInterfaces/nicB"), "Microsoft.Network/networkInterfaces", "nicB",
                """{"ipConfigurations":[{"name":"c","properties":{"subnet":{"id":"@@S@@"}}}]}""".Replace("@@S@@", subnetId)),
            Item(Rg("Microsoft.Network/virtualNetworks/vnet1"), "Microsoft.Network/virtualNetworks", "vnet1",
                """{"subnets":[{"name":"snet1"}]}"""),
        };

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Network/networkInterfaces/nicA"), inv);

        var same = Assert.Single(answer.Paths, p => p.Origin == ReachOrigin.SameSubnet);
        Assert.Contains("shares the subnet with: nicB", string.Join(",", same.Evidence));
        Assert.DoesNotContain("nicA", same.Evidence.Single());
    }

    [Fact]
    public void PrivateVm_HasNoInternetPathAndNeverClaimsClosure()
    {
        var subnetId = Rg("Microsoft.Network/virtualNetworks/vnet1/subnets/snet1");
        var inv = new[]
        {
            Item(Rg("Microsoft.Compute/virtualMachines/vm-quiet"), "Microsoft.Compute/virtualMachines", "vm-quiet",
                """{"networkProfile":{"networkInterfaces":[{"id":"/subscriptions/SUB-1/resourceGroups/rg-a/providers/Microsoft.Network/networkInterfaces/nic-q"}]}}"""),
            Item(Rg("Microsoft.Network/networkInterfaces/nic-q"), "Microsoft.Network/networkInterfaces", "nic-q",
                """{"ipConfigurations":[{"name":"c","properties":{"subnet":{"id":"@@S@@"}}}]}""".Replace("@@S@@", subnetId)),
            Item(Rg("Microsoft.Network/virtualNetworks/vnet1"), "Microsoft.Network/virtualNetworks", "vnet1", """{"subnets":[{"name":"snet1"}]}"""),
        };

        var answer = ReachabilityEngine.Analyze(Rg("Microsoft.Compute/virtualMachines/vm-quiet"), inv);

        Assert.False(answer.InternetReachable);
        Assert.DoesNotContain(answer.Paths, p => p.Origin == ReachOrigin.Internet);
        Assert.Contains(answer.Caveats, c => c.StartsWith("internet-path-not-proven") && c.Contains("NOT proof of closure"));
    }

    [Fact]
    public void Principals_OnlyFromSuppliedExactScopeAssignments()
    {
        var vm = Item(Rg("Microsoft.Compute/virtualMachines/vm1"), "Microsoft.Compute/virtualMachines", "vm1", null);

        var without = ReachabilityEngine.Analyze(vm.Id, [vm]);
        Assert.Empty(without.Principals);
        Assert.Contains(without.Caveats, c => c.StartsWith("role-assignments-not-supplied"));

        var with = ReachabilityEngine.Analyze(vm.Id, [vm],
        [
            new ScopeRoleAssignment(vm.Id + "/", "Reader", "ops-team"),
            new ScopeRoleAssignment(Sub, "Owner", "root"),           // parent scope: deliberately not expanded
        ]);
        Assert.Equal(["Reader - ops-team"], with.Principals);
        Assert.Contains(with.Caveats, c => c.StartsWith("rbac-exact-scope-only"));
    }

    [Fact]
    public void Caveats_AlwaysCarryTheNeverInspectedLayers()
    {
        var answer = ReachabilityEngine.Analyze("anything", Empty);
        Assert.Contains(answer.Caveats, c => c.StartsWith("public-flags-not-read"));
        Assert.Contains(answer.Caveats, c => c.StartsWith("nsg-precedence-not-simulated"));
        Assert.Contains(answer.Caveats, c => c.StartsWith("policy-layers-not-evaluated"));
    }
}
