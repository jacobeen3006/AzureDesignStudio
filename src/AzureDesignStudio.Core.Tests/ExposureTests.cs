using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Exposure;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 9.4 - exposure analysis over typeless inventories: public IPs and what
/// they front, NSG rules open to the internet, public PaaS endpoints without private
/// endpoints, SQL 0.0.0.0 firewall, private-endpoint approval state. Offline fixtures.
/// </summary>
public class ExposureTests
{
    private const string Vm = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.compute/virtualmachines/vm1";
    private const string Nic = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/networkinterfaces/nic1";
    private const string Pip = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/publicipaddresses/pip1";
    private const string Nsg = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/networksecuritygroups/nsg1";
    private const string Sa = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.storage/storageaccounts/sa1";
    private const string Pe = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/privateendpoints/pe1";
    private const string Sql = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.sql/servers/sql1";
    private const string AppGw = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/applicationgateways/agw1";

    private static InventoryItem Item(string id, string type, string propertiesJson = "{}") =>
        new(id, id[(id.LastIndexOf('/') + 1)..], type, "westeurope", "rg1", "s1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            propertiesJson == "{}" ? null : JsonDocument.Parse(propertiesJson).RootElement.Clone());

    private static string PipJson(string addr = "20.30.40.50") =>
        "{\"ipAddress\":\"" + addr + "\",\"sku\":{\"name\":\"Standard\"}}";

    private static InventoryItem NicWith(string? nsgRef = null)
    {
        var json = "{\"ipConfigurations\":[{\"name\":\"ipconfig1\",\"properties\":{\"publicIPAddress\":{\"id\":\"" + Pip + "\"}}}]}";
        if (nsgRef is not null)
            json = json.TrimEnd('}') + ",\"networkSecurityGroup\":{\"id\":\"" + nsgRef + "\"}}";
        return Item(Nic, "microsoft.network/networkinterfaces", json);
    }

    private static InventoryItem VmWith() => Item(Vm, "microsoft.compute/virtualmachines",
        "{\"networkProfile\":{\"networkInterfaces\":[{\"id\":\"" + Nic + "\"}]}}");

    private static InventoryItem NsgWith(string rulesJson) =>
        Item(Nsg, "microsoft.network/networksecuritygroups", "{\"securityRules\":" + rulesJson + "}");

    private static InventoryItem PeWith(string status) => Item(Pe, "microsoft.network/privateendpoints",
        "{\"privateLinkServiceConnections\":[{\"name\":\"c\",\"properties\":{\"privateLinkServiceId\":\"" + Sa
        + "\",\"groupIds\":[\"blob\"],\"privateLinkServiceConnectionStateStatus\":\"" + status
        + "\",\"privateLinkServiceConnectionStateActionsRequired\":\"CreateGroupIds\"}}]}");

    private static IReadOnlyList<ExposureFinding> Run(params InventoryItem[] items) => ExposureAnalyzer.Analyze(items);

    // --- VM public IP ---

    [Fact]
    public void Vm_WithPublicIp_FlaggedHigh()
    {
        var findings = Run(VmWith(), NicWith(), Item(Pip, "microsoft.network/publicipaddresses", PipJson()));
        var f = findings.Single(x => x.Kind == "vm-public-ip");
        Assert.Equal(ExposureSeverity.High, f.Severity);
        Assert.Contains("20.30.40.50", f.Detail);
    }

    [Fact]
    public void Vm_PublicIpAndAdminPortOpen_EscalatedCritical()
    {
        var openRdp = "[{\"name\":\"open-rdp\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"*\",\"destinationPortRange\":\"3389\"}}]";
        var findings = Run(VmWith(), NicWith(Nsg), NsgWith(openRdp), Item(Pip, "microsoft.network/publicipaddresses", PipJson()));
        var f = findings.Single(x => x.Kind == "vm-public-ip");
        Assert.Equal(ExposureSeverity.Critical, f.Severity);
        Assert.Contains("3389", f.Detail);
    }

    [Fact]
    public void Vm_PrivateNic_NoFinding()
    {
        var privateNic = Item(Nic, "microsoft.network/networkinterfaces", "{\"ipConfigurations\":[{\"name\":\"ipconfig1\",\"properties\":{}}]}");
        Assert.DoesNotContain(Run(VmWith(), privateNic), f => f.Kind == "vm-public-ip");
    }

    // --- NSG rules ---

    [Fact]
    public void Nsg_AllPortsInternet_Critical()
    {
        var rules = "[{\"name\":\"open-all\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"0.0.0.0/0\",\"destinationPortRange\":\"*\"}}]";
        var f = Assert.Single(Run(NsgWith(rules)), x => x.ResourceId == Nsg);
        Assert.Equal(ExposureSeverity.Critical, f.Severity);
        Assert.Equal("nsg-open-all-inbound", f.Kind);
    }

    [Fact]
    public void Nsg_SshInternet_High()
    {
        var rules = "[{\"name\":\"ssh\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"Internet\",\"destinationPortRanges\":[\"22\",\"8080\"]}}]";
        var f = Assert.Single(Run(NsgWith(rules)));
        Assert.Equal("nsg-open-admin-ports", f.Kind);
        Assert.Contains("22", f.Detail);
    }

    [Fact]
    public void Nsg_DeniedOrScopedOrOutbound_Quiet()
    {
        var rules = """
        [
          {"name":"ssh-deny","properties":{"direction":"Inbound","access":"Deny","sourceAddressPrefix":"*","destinationPortRange":"22"}},
          {"name":"web-office","properties":{"direction":"Inbound","access":"Allow","sourceAddressPrefix":"40.40.0.0/16","destinationPortRange":"80"}},
          {"name":"egress","properties":{"direction":"Outbound","access":"Allow","sourceAddressPrefix":"*","destinationPortRange":"*"}},
          {"name":"web-internet","properties":{"direction":"Inbound","access":"Allow","sourceAddressPrefix":"*","destinationPortRange":"443"}},
          {"name":"high-ports","properties":{"direction":"Inbound","access":"Allow","sourceAddressPrefix":"*","destinationPortRange":"3390-4000"}}
        ]
        """;
        Assert.Empty(Run(NsgWith(rules)));
    }

    // The shape ARM and Resource Graph return: the singular property is filled and its
    // plural twin is an empty array (and the other way round). Reading the plural whenever
    // it was an array made every such rule port-less, so nothing was ever flagged.
    [Fact]
    public void Nsg_RealArmShape_SingularFilledPluralEmpty_IsJudged()
    {
        var rules = """
        [{"name":"ssh","properties":{"direction":"Inbound","access":"Allow","protocol":"Tcp",
          "sourceAddressPrefix":"*","sourceAddressPrefixes":[],
          "destinationPortRange":"22","destinationPortRanges":[]}}]
        """;
        var f = Assert.Single(Run(NsgWith(rules)));
        Assert.Equal("nsg-open-admin-ports", f.Kind);
        Assert.Contains("22", f.Detail);
    }

    // `az vm create` names the rules it opens "default-allow-ssh" / "default-allow-rdp". They
    // are user rules in securityRules; Azure's built-ins live in defaultSecurityRules.
    [Theory]
    [InlineData("default-allow-ssh", "22")]
    [InlineData("default-allow-rdp", "3389")]
    public void Nsg_RuleNamedDefault_IsStillAUserRule(string name, string port)
    {
        var rules = "[{\"name\":\"" + name + "\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"*\",\"sourceAddressPrefixes\":[],\"destinationPortRange\":\"" + port + "\",\"destinationPortRanges\":[]}}]";
        var f = Assert.Single(Run(NsgWith(rules)));
        Assert.Equal("nsg-open-admin-ports", f.Kind);
        Assert.Contains(port, f.Detail);
    }

    [Fact]
    public void Nsg_BuiltInDefaultRulesAreNotJudged()
    {
        var nsg = Item(Nsg, "microsoft.network/networksecuritygroups", """
        {"securityRules":[],
         "defaultSecurityRules":[{"name":"AllowVnetInBound","properties":{"direction":"Inbound","access":"Allow","sourceAddressPrefix":"VirtualNetwork","destinationPortRange":"*"}},
                                 {"name":"DenyAllInBound","properties":{"direction":"Inbound","access":"Deny","sourceAddressPrefix":"*","destinationPortRange":"*"}}]}
        """);
        Assert.Empty(Run(nsg));
    }

    [Theory]
    [InlineData("20-25", "22")]
    [InlineData("3000-4000", "3389")]
    [InlineData("22-3389", "22, 3389")]
    public void Nsg_PortRangeCoveringAnAdminPort_High(string range, string expectedPorts)
    {
        var rules = "[{\"name\":\"range\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"Internet\",\"destinationPortRange\":\"" + range + "\"}}]";
        var f = Assert.Single(Run(NsgWith(rules)));
        Assert.Equal("nsg-open-admin-ports", f.Kind);
        Assert.EndsWith($"admin port(s) {expectedPorts}.", f.Detail);
    }

    [Theory]
    [InlineData("0-65535")]
    [InlineData("1-65535")]
    public void Nsg_FullPortRange_IsAllPorts(string range)
    {
        var rules = "[{\"name\":\"all\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"*\",\"destinationPortRange\":\"" + range + "\"}}]";
        var f = Assert.Single(Run(NsgWith(rules)));
        Assert.Equal("nsg-open-all-inbound", f.Kind);
    }

    [Theory]
    [InlineData("[\"10.0.0.0/8\",\"0.0.0.0/0\"]")]   // the internet is not the first entry
    [InlineData("[\"::/0\"]")]
    [InlineData("[\"10.0.0.0/8\",\"Internet\"]")]
    public void Nsg_InternetAmongSeveralSources_IsInternet(string sources)
    {
        var rules = "[{\"name\":\"rdp\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"\",\"sourceAddressPrefixes\":" + sources + ",\"destinationPortRange\":\"3389\"}}]";
        var f = Assert.Single(Run(NsgWith(rules)));
        Assert.Equal("nsg-open-admin-ports", f.Kind);
    }

    [Fact]
    public void Vm_PublicIpAndAllPortsOpen_EscalatedCritical()
    {
        var openAll = "[{\"name\":\"open-all\",\"properties\":{\"direction\":\"Inbound\",\"access\":\"Allow\",\"sourceAddressPrefix\":\"*\",\"sourceAddressPrefixes\":[],\"destinationPortRange\":\"*\",\"destinationPortRanges\":[]}}]";
        var findings = Run(VmWith(), NicWith(Nsg), NsgWith(openAll), Item(Pip, "microsoft.network/publicipaddresses", PipJson()));
        Assert.Equal(ExposureSeverity.Critical, findings.Single(x => x.Kind == "vm-public-ip").Severity);
    }

    // --- PaaS public endpoint / private endpoint ---

    [Fact]
    public void Storage_PublicBlobEndpoint_FlaggedMedium()
    {
        var sa = Item(Sa, "microsoft.storage/storageaccounts", "{\"primaryEndpoints\":{\"blob\":\"https://sa1.blob.core.windows.net/\"}}");
        var f = Assert.Single(Run(sa));
        Assert.Equal("paas-public-endpoint", f.Kind);
        Assert.Contains("sa1.blob.core.windows.net", f.Detail);
    }

    [Fact]
    public void Storage_ProtectedByPrivateEndpoint_Quiet()
    {
        var sa = Item(Sa, "microsoft.storage/storageaccounts", "{\"primaryEndpoints\":{\"blob\":\"https://sa1.blob.core.windows.net/\"}}");
        Assert.Empty(Run(sa, PeWith("Approved")));
    }

    [Fact]
    public void PrivateEndpoint_PendingApproval_FlaggedLow()
    {
        var f = Assert.Single(Run(PeWith("PendingAccepting")));
        Assert.Equal("pe-pending-approval", f.Kind);
        Assert.Equal(ExposureSeverity.Low, f.Severity);
    }

    // --- SQL firewall + LB frontend ---

    [Fact]
    public void Sql_AllInternetFirewall_Critical()
    {
        var sql = Item(Sql, "microsoft.sql/servers",
            "{\"firewallRules\":[{\"name\":\"AllInternet\",\"properties\":{\"startIpAddress\":\"0.0.0.0\",\"endIpAddress\":\"0.0.0.0\"}}]}");
        var f = Assert.Single(Run(sql));
        Assert.Equal("sql-firewall-all", f.Kind);
        Assert.Equal(ExposureSeverity.Critical, f.Severity);
    }

    [Fact]
    public void AppGw_PublicFrontend_Medium()
    {
        var agw = Item(AppGw, "microsoft.network/applicationgateways",
            "{\"frontendIPConfigurations\":[{\"name\":\"fe\",\"properties\":{\"publicIPAddress\":{\"id\":\"" + Pip + "\"}}}]}");
        var f = Assert.Single(Run(agw, Item(Pip, "microsoft.network/publicipaddresses", PipJson())));
        Assert.Equal("lb-frontend-public", f.Kind);
    }

    [Fact]
    public void CrossScopeNicLink_DoesNotCrash()
    {
        // VM references a NIC that is not in the scan (cross-subscription) - no finding, no throw.
        Assert.Empty(Run(VmWith()));
    }
}
