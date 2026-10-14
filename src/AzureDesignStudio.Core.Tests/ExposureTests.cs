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
          {"name":"defaultDenyAll","properties":{"direction":"Inbound","access":"Allow","sourceAddressPrefix":"*","destinationPortRange":"*"}}
        ]
        """;
        Assert.Empty(Run(NsgWith(rules)));
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
