using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Security;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// [88] Data-holding answers. The contract: Yes/No only from flags the scan actually carries,
/// Unknown whenever the truth lives on child resources (keys, blobServices), and the two
/// blind spots are stated as caveats even on a perfectly green tenant.
/// </summary>
public class DataHoldingsTests
{
    private const string Sub = "/subscriptions/sub1";

    private static InventoryItem Item(string name, string type, string? props = null) =>
        new($"{Sub}/resourceGroups/rg/providers/{type}/{name}", name, type, "westeurope", "rg", "sub1",
            new Dictionary<string, string>(),
            props is null ? null : JsonDocument.Parse(props).RootElement.Clone());

    private static InventoryItem Storage(string name, string? props) =>
        Item(name, "Microsoft.Storage/StorageAccounts", props);

    [Fact]
    public void StorageCmkIsJudgedFromKeySourceOnly()
    {
        var cmk = Storage("st-cmk", """{"encryption":{"keySource":"Microsoft.Keyvault"}}""");
        var smk = Storage("st-smk", """{"encryption":{"keySource":"Microsoft.Storage"}}""");
        var blind = Storage("st-none", """{"provisioningState":"succeeded"}""");

        var audit = DataHoldings.Analyze([cmk, smk, blind]);

        Assert.Equal(DataEvidence.Yes, audit.Rows.Single(r => r.Name == "st-cmk").CmkEncryption);
        Assert.Equal(DataEvidence.No, audit.Rows.Single(r => r.Name == "st-smk").CmkEncryption);
        Assert.Equal(DataEvidence.Unknown, audit.Rows.Single(r => r.Name == "st-none").CmkEncryption);
        Assert.Contains("child resources", audit.Rows.Single(r => r.Name == "st-none").SoftDeleteDetail);
    }

    [Fact]
    public void PublicNetworkSwitchIsAuthorityNetworkAclsItsFallback()
    {
        var off = Storage("st-off", """{"publicNetworkAccess":"disabled","networkAcls":{"defaultAction":"Allow"}}""");
        var on = Storage("st-on", """{"publicNetworkAccess":"enabled"}""");
        var legacy = Storage("st-legacy", """{"networkAcls":{"defaultAction":"Deny","bypass":"None"}}""");
        var odd = Storage("st-odd", """{"publicNetworkAccess":"maybe"}""");
        var blind = Storage("st-blind", "{}");

        var audit = DataHoldings.Analyze([off, on, legacy, odd, blind]);

        // The authoritative switch wins over the older acl shape even when they disagree.
        Assert.Equal(DataEvidence.Yes, audit.Rows.Single(r => r.Name == "st-off").PrivateOnly);
        Assert.Equal(DataEvidence.No, audit.Rows.Single(r => r.Name == "st-on").PrivateOnly);
        Assert.Equal(DataEvidence.Yes, audit.Rows.Single(r => r.Name == "st-legacy").PrivateOnly);
        Assert.Equal(DataEvidence.Unknown, audit.Rows.Single(r => r.Name == "st-odd").PrivateOnly);
        Assert.Equal(DataEvidence.Unknown, audit.Rows.Single(r => r.Name == "st-blind").PrivateOnly);
    }

    [Fact]
    public void ServersAndCosmosAnswerFromPublicNetworkAccess()
    {
        var sql = Item("sql1", "microsoft.sql/servers", """{"publicNetworkAccess":"Disabled"}""");
        var pg = Item("pg1", "microsoft.dbforpostgresql/flexibleServers", """{"publicNetworkAccess":"Enabled"}""");
        var cosmos = Item("cosmos1", "Microsoft.DocumentDB/databaseAccounts", "{}");

        var audit = DataHoldings.Analyze([sql, pg, cosmos]);

        Assert.Equal(DataEvidence.Yes, audit.Rows.Single(r => r.Name == "sql1").PrivateOnly);
        Assert.Equal(DataEvidence.No, audit.Rows.Single(r => r.Name == "pg1").PrivateOnly);
        Assert.Equal(DataEvidence.Unknown, audit.Rows.Single(r => r.Name == "cosmos1").PrivateOnly);
        Assert.Equal(DataEvidence.NotApplicable, audit.Rows.Single(r => r.Name == "sql1").SoftDelete);
    }

    [Fact]
    public void KeyVaultSoftDeleteReportsPurgeProtectionState()
    {
        var soft = Item("kv1", "Microsoft.KeyVault/vaults", """{"enableSoftDelete":true}""");
        var both = Item("kv2", "Microsoft.KeyVault/vaults", """{"enableSoftDelete":true,"enablePurgeProtection":true}""");
        var hard = Item("kv3", "Microsoft.KeyVault/vaults", """{"enableSoftDelete":false}""");

        var audit = DataHoldings.Analyze([soft, both, hard]);

        Assert.Equal(DataEvidence.Yes, audit.Rows.Single(r => r.Name == "kv1").SoftDelete);
        Assert.Contains("purge protection off or not reported", audit.Rows.Single(r => r.Name == "kv1").SoftDeleteDetail);
        Assert.Contains("purge protection on", audit.Rows.Single(r => r.Name == "kv2").SoftDeleteDetail);
        Assert.Equal(DataEvidence.No, audit.Rows.Single(r => r.Name == "kv3").SoftDelete);
    }

    [Fact]
    public void OnlyDataHoldingFamiliesAreRowsEverythingIsCounted()
    {
        var vm = Item("vm1", "microsoft.compute/virtualmachines");
        var nic = Item("nic1", "microsoft.network/networkinterfaces");
        var st = Storage("st1", "{}");

        var audit = DataHoldings.Analyze([vm, nic, st]);

        Assert.Single(audit.Rows);
        Assert.Equal("st1", Assert.Single(audit.Rows).Name);
        Assert.Equal(3, audit.TotalResources);
    }

    [Fact]
    public void FabricCapacityHoldsDataButShowsNoPerResourceFlags()
    {
        var fab = Item("fab1", "Microsoft.Fabric/capacities", """{"administrationConfiguration":{}}""");

        var audit = DataHoldings.Analyze([fab]);

        var row = Assert.Single(audit.Rows);
        Assert.Equal(DataEvidence.Unknown, row.CmkEncryption);
        Assert.Equal(DataEvidence.NotApplicable, row.PrivateOnly);
    }

    [Fact]
    public void CaveatsRideEvenOnAGreenTenant()
    {
        var good = Storage("st-good", """{"encryption":{"keySource":"Microsoft.Keyvault"},"publicNetworkAccess":"disabled"}""");

        var audit = DataHoldings.Analyze([good]);

        Assert.Contains(audit.Caveats, c => c.StartsWith("keys-not-scanned"));
        Assert.Contains(audit.Caveats, c => c.StartsWith("network-path-not-simulated"));
    }
}
