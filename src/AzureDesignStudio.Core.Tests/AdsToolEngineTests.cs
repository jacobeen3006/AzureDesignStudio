using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Mcp;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Unmanaged;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// [93] local MCP server engine: read-only, deterministic, and honest - a refused decom plan
/// carries no runbook identity, and every caveat says what was NOT evaluated.
/// </summary>
public class AdsToolEngineTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Rg = Sub + "/resourceGroups/rg";
    private const string Vnet = Rg + "/providers/Microsoft.Network/virtualNetworks/vnet1";
    private const string Nic = Rg + "/providers/Microsoft.Network/networkInterfaces/nic1";
    private const string Vm = Rg + "/providers/Microsoft.Compute/virtualMachines/vm1";
    private const string Vm2 = Rg + "/providers/Microsoft.Compute/virtualMachines/vm2";
    private const string Disk = Rg + "/providers/Microsoft.Compute/disks/disk1";
    private const string Storage = Rg + "/providers/Microsoft.Storage/storageAccounts/store1";
    private const string Nsg = Rg + "/providers/Microsoft.Network/networkSecurityGroups/nsg1";
    private const string Sql = Rg + "/providers/Microsoft.Sql/servers/sql1";
    private const string Web = Rg + "/providers/Microsoft.Web/sites/web1";
    private const string Pip = Rg + "/providers/Microsoft.Network/publicIPAddresses/pip1";

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan PurgeDelay = TimeSpan.FromDays(7);

    private static InventoryItem Item(string id, string type, string? propsJson = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        JsonElement? raw = null;
        if (propsJson is not null)
            raw = JsonDocument.Parse(propsJson).RootElement.Clone();
        return new InventoryItem(id, id.Split('/')[^1], type, "westeurope", "rg", "sub1",
            tags ?? new Dictionary<string, string>(), raw);
    }

    private static InventoryItem VmItem() => Item(Vm, "microsoft.compute/virtualmachines",
        """{"networkProfile":{"networkInterfaces":[{"id":"__NIC__"}]},"storageProfile":{"dataDisks":[{"managedDisk":{"name":"disk1"}}]}}"""
            .Replace("__NIC__", Nic));

    private static InventoryItem NicItem() => Item(Nic, "microsoft.network/networkinterfaces",
        """{"ipConfigurations":[{"properties":{"subnet":{"id":"__SUBNET__"},"publicIPAddress":{"id":"__PIP__"}}}]}"""
            .Replace("__SUBNET__", Vnet + "/subnets/subnetA").Replace("__PIP__", Pip));

    private static InventoryItem DiskItem() => Item(Disk, "microsoft.compute/disks",
        """{"managedBy":"__VM__"}""".Replace("__VM__", Vm));

    private static InventoryItem VnetItem() => Item(Vnet, "microsoft.network/virtualNetworks",
        """{"subnets":[{"name":"subnetA","properties":{}}]}""");

    private static readonly GuardrailPolicy ProdTag = new([], [new ProtectedTag("environment", "prod")], []);

    // --- inventory query: filters, paging, determinism ---

    [Fact]
    public void InventoryQuery_TypeContains_IsCaseInsensitive()
    {
        var page = AdsToolEngine.InventoryQuery([VmItem(), VnetItem(), DiskItem()],
            new InventoryFilter(TypeContains: "VIRTUALMACHINES"), 10, 0);

        Assert.Single(page.Rows);
        Assert.Equal(Vm, page.Rows[0].Id);
        Assert.Equal(1, page.Total);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void InventoryQuery_ScopePrefix_MatchesPrefixOnly_AndOrdersOrdinalIgnoreCase()
    {
        var page = AdsToolEngine.InventoryQuery([VmItem(), VnetItem(), DiskItem()],
            new InventoryFilter(ScopePrefix: Rg + "/providers/Microsoft.Compute/"), 10, 0);

        Assert.Equal(2, page.Total);
        Assert.Equal([Disk, Vm], page.Rows.Select(r => r.Id).ToList());
    }

    [Fact]
    public void InventoryQuery_TagKey_MatchesKeyPresence_CaseInsensitiveLookup()
    {
        var tagged = Item(Storage, "microsoft.storage/storageaccounts",
            tags: new Dictionary<string, string> { ["Env"] = "prod" }); // ordinal dict, mixed-case key
        var page = AdsToolEngine.InventoryQuery([tagged, VmItem()], new InventoryFilter(TagKey: "env"), 10, 0);

        Assert.Single(page.Rows);
        Assert.Equal(Storage, page.Rows[0].Id);
    }

    [Fact]
    public void InventoryQuery_TagKeyAndValue_MatchesValue_CaseInsensitive()
    {
        var prod = Item(Storage, "microsoft.storage/storageaccounts",
            tags: new Dictionary<string, string> { ["env"] = "prod" });
        var dev = Item(Vm2, "microsoft.compute/virtualmachines",
            tags: new Dictionary<string, string> { ["env"] = "dev" });
        var page = AdsToolEngine.InventoryQuery([prod, dev],
            new InventoryFilter(TagKey: "env", TagValue: "PROD"), 10, 0);

        Assert.Single(page.Rows);
        Assert.Equal(Storage, page.Rows[0].Id);
    }

    [Fact]
    public void InventoryQuery_CombinedFilters_AllFacetsMustMatch()
    {
        var taggedVm = Item(Vm, "microsoft.compute/virtualmachines",
            tags: new Dictionary<string, string> { ["env"] = "prod" });
        var otherVm = Item(Vm2, "microsoft.compute/virtualmachines");
        var taggedVnet = Item(Vnet, "microsoft.network/virtualNetworks",
            tags: new Dictionary<string, string> { ["env"] = "prod" });

        var page = AdsToolEngine.InventoryQuery([taggedVm, otherVm, taggedVnet],
            new InventoryFilter(TypeContains: "virtualmachines", ScopePrefix: Rg, TagKey: "env"), 10, 0);

        Assert.Single(page.Rows);
        Assert.Equal(Vm, page.Rows[0].Id);
    }

    [Fact]
    public void InventoryQuery_Paging_TotalIsPostFilterCount_AndTruncates()
    {
        var items = new List<InventoryItem> { VmItem(), Item(Vm2, "microsoft.compute/virtualmachines"), VnetItem(), DiskItem(), NicItem() };
        // 5 distinct rows
        var mid = AdsToolEngine.InventoryQuery(items, InventoryFilter.None, 2, 1);
        Assert.Equal(5, mid.Total);
        Assert.Equal(2, mid.Rows.Count);
        Assert.True(mid.Truncated);

        var tail = AdsToolEngine.InventoryQuery(items, InventoryFilter.None, 2, 4);
        Assert.Single(tail.Rows);
        Assert.False(tail.Truncated);
    }

    [Fact]
    public void InventoryQuery_OutOfRangeOffset_YieldsEmptyRows_NeverThrows()
    {
        IReadOnlyList<InventoryItem> items = [VmItem(), VnetItem(), DiskItem()];
        var page = AdsToolEngine.InventoryQuery(items, InventoryFilter.None, 5, 10);

        Assert.Empty(page.Rows);
        Assert.Equal(3, page.Total);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void InventoryQuery_IsDeterministic_AcrossInputOrderAndCalls()
    {
        IReadOnlyList<InventoryItem> items = [VmItem(), VnetItem(), DiskItem()];
        var first = AdsToolEngine.InventoryQuery(items, new InventoryFilter(TypeContains: "microsoft"), 100, 0);
        var second = AdsToolEngine.InventoryQuery(items.Reverse().ToList(), new InventoryFilter(TypeContains: "MICROSOFT"), 100, 0);

        Assert.Equal(first.Total, second.Total);
        Assert.Equal(first.Rows.Select(r => r.Id), second.Rows.Select(r => r.Id));
        Assert.Equal(first.Rows.Select(r => r.Tags), second.Rows.Select(r => r.Tags));
    }

    // --- type summary ---

    [Fact]
    public void TypeSummary_CountsPerType_GroupsCaseInsensitively_SortedOrdinal()
    {
        IReadOnlyList<InventoryItem> items = [VmItem(), Item(Vm2, "Microsoft.Compute/virtualMachines"), VnetItem(), DiskItem(),
            Item(Storage, "microsoft.storage/storageaccounts")];

        var summary = AdsToolEngine.TypeSummary(items);

        Assert.Equal(
            [
                new TypeCount("microsoft.compute/disks", 1),
                new TypeCount("microsoft.compute/virtualmachines", 2),
                new TypeCount("microsoft.network/virtualnetworks", 1),
                new TypeCount("microsoft.storage/storageaccounts", 1),
            ],
            summary);
    }

    // --- scan diff: drift vs volatile churn ---

    [Fact]
    public void ScanDiff_ChurnOnly_NeverCountsAsDrift()
    {
        var before = Item(Vm, "microsoft.compute/virtualmachines", """{"provisioningState":"Creating"}""");
        var after = Item(Vm, "microsoft.compute/virtualmachines", """{"provisioningState":"Succeeded"}""");

        var diff = AdsToolEngine.ScanDiff([before], [after], 10);

        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
        Assert.Equal(1, diff.Changed);
        Assert.Equal(0, diff.ChangedDrift);
        Assert.Equal(1, diff.ChangedChurn);
        Assert.Single(diff.Top);
        Assert.Equal("Changed", diff.Top[0].Kind);
        Assert.Contains("provisioningState", diff.Top[0].Detail);
    }

    [Fact]
    public void ScanDiff_SummaryCounts_TopCap_DeterministicOrder()
    {
        IReadOnlyList<InventoryItem> before = [VmItem(), NicItem(), VnetItem()];
        IReadOnlyList<InventoryItem> after = [NicItem(), DiskItem()];

        var diff = AdsToolEngine.ScanDiff(before, after, 2);

        Assert.Equal(1, diff.Added);
        Assert.Equal(2, diff.Removed);
        Assert.Equal(0, diff.Changed);
        Assert.Equal(0, diff.PropertyDiffs);
        Assert.NotEqual(diff.BeforeHash, diff.AfterHash);
        // differ's deterministic order (ordinal-ignore-case by id), capped at topN
        Assert.Equal([Disk, Vm], diff.Top.Select(r => r.Id).ToList());
        Assert.Equal("Added", diff.Top[0].Kind);
        Assert.Equal("Removed", diff.Top[1].Kind);
    }

    // --- exposure: counts, ordering, honest caveats ---

    private static InventoryItem NsgOpenAll() => Item(Nsg, "microsoft.network/networksecuritygroups",
        """{"securityRules":[{"name":"open-all","properties":{"direction":"Inbound","access":"Allow","sourceAddressPrefix":"0.0.0.0/0","destinationPortRanges":["*"]}}]}""");

    private static InventoryItem SqlOpen() => Item(Sql, "microsoft.sql/servers",
        """{"firewallRules":[{"name":"all","properties":{"startIpAddress":"0.0.0.0","endIpAddress":"0.0.0.0"}}]}""");

    private static InventoryItem StoragePublic() => Item(Storage, "microsoft.storage/storageaccounts",
        """{"primaryEndpoints":{"blob":"https://store1.blob.core.windows.net"}}""");

    private static InventoryItem WebHttp() => Item(Web, "microsoft.web/sites",
        """{"hostNames":["web1.azurewebsites.net"],"httpsOnly":false}""");

    [Fact]
    public void Exposure_SeverityCounts_MatchRowCounts()
    {
        IReadOnlyList<InventoryItem> items = [NsgOpenAll(), VmItem(), NicItem(), SqlOpen(), StoragePublic(), WebHttp()];

        var report = AdsToolEngine.Exposure(items);

        // nsg open-all (Critical) + sql firewall (Critical); vm public ip (High, no NSG on the NIC);
        // storage + web public endpoints (Medium); web http-allowed (Low)
        Assert.Equal(2, report.Critical);
        Assert.Equal(1, report.High);
        Assert.Equal(2, report.Medium);
        Assert.Equal(1, report.Low);
        Assert.Equal(report.Critical + report.High + report.Medium + report.Low, report.Rows.Count);
        Assert.Equal(report.Critical, report.Rows.Count(r => r.Severity == "Critical"));
        Assert.Equal(report.High, report.Rows.Count(r => r.Severity == "High"));
        Assert.Equal(report.Medium, report.Rows.Count(r => r.Severity == "Medium"));
        Assert.Equal(report.Low, report.Rows.Count(r => r.Severity == "Low"));
    }

    [Fact]
    public void Exposure_Rows_SeverityThenId_AndCaveatsNameTheCoverageGaps()
    {
        IReadOnlyList<InventoryItem> items = [NsgOpenAll(), VmItem(), NicItem(), SqlOpen(), StoragePublic(), WebHttp()];

        var report = AdsToolEngine.Exposure(items);

        Assert.Equal([
            "Critical", "Critical", "High", "Medium", "Medium", "Low"
        ], report.Rows.Select(r => r.Severity).ToList());
        // within Critical, ids are ordinal-ignore-case sorted: nsg1 before sql1
        Assert.Equal(Nsg, report.Rows[0].ResourceId);
        Assert.Equal(Sql, report.Rows[1].ResourceId);
        Assert.Contains(report.Caveats, c => c.StartsWith("coverage:", StringComparison.Ordinal));
        Assert.Contains(report.Caveats, c => c.Contains("never assumed safe", StringComparison.Ordinal));
    }

    // --- unmanaged: statuses, matched-pattern reasons, evidence caveats ---

    [Fact]
    public void Unmanaged_Protected_CarriesTheMatchedGuardrailPattern()
    {
        var prod = Item(Storage, "microsoft.storage/storageaccounts",
            tags: new Dictionary<string, string> { ["environment"] = "prod" });
        var plain = Item(Vm2, "microsoft.compute/virtualmachines");
        var byProperty = Item(Disk, "microsoft.compute/disks", """{"managedBy":"__VM__"}""".Replace("__VM__", Vm));
        var vmss = Item(Rg + "/providers/Microsoft.Compute/virtualMachineScaleSets/vmss1/virtualMachines/member0",
            "microsoft.compute/virtualmachines");
        var byTag = Item(Nic, "microsoft.network/networkinterfaces",
            tags: new Dictionary<string, string> { ["provisioned-by"] = "terraform" });

        var report = AdsToolEngine.Unmanaged([prod, plain, byProperty, vmss, byTag], ProdTag);

        Assert.Equal([Vm2], report.UnmanagedIds);
        Assert.Single(report.Protected);
        Assert.Equal(Storage, report.Protected[0].Id);
        Assert.Equal("guardrail-tag:environment=prod", report.Protected[0].Reason);
        Assert.Equal(3, report.ManagedCount);
        Assert.Contains(report.Caveats, c => c.StartsWith("evidence:", StringComparison.Ordinal));
    }

    // --- decom: plan-only, refusal oracle, honest caveats ---

    [Fact]
    public void Decom_Refused_NoRunbookIdentity_ReferrerNamed_CaveatsSayNotEvaluated()
    {
        var prodVm = Item(Vm2, "microsoft.compute/virtualmachines",
            tags: new Dictionary<string, string> { ["environment"] = "prod" });
        IReadOnlyList<InventoryItem> inventory = [VmItem(), VnetItem(), prodVm];

        var report = AdsToolEngine.DecomPlan(inventory, [Vm, Vm2], ProdTag, Now, PurgeDelay);

        Assert.False(report.Allowed);
        Assert.Single(report.Refusals);
        Assert.Equal(Vm2, report.Refusals[0].ResourceId);
        Assert.Equal("guardrail", report.Refusals[0].Kind);
        Assert.Null(report.PlanHash);
        Assert.Null(report.WindowId);
        Assert.Null(report.PurgeNotBefore);
        // the refused candidate is never staged; the allowed one still is
        var staged = report.Stages.SelectMany(s => s).ToList();
        Assert.DoesNotContain(Vm2, staged);
        Assert.Contains(Vm, staged);
        Assert.Contains(report.Caveats, c => c.StartsWith("locks-not-evaluated:", StringComparison.Ordinal));
        Assert.Contains(report.Caveats, c => c.StartsWith("rbac-not-evaluated:", StringComparison.Ordinal));
        Assert.Contains(report.Caveats, c => c.StartsWith("backups-not-evaluated:", StringComparison.Ordinal));
    }

    [Fact]
    public void Decom_Allowed_StagesInPlannerOrder_IdentityFromInjectedNow()
    {
        IReadOnlyList<InventoryItem> inventory = [VmItem(), NicItem(), DiskItem(), VnetItem()];

        var report = AdsToolEngine.DecomPlan(inventory, [Vm, Nic, Disk, Vnet], GuardrailPolicy.Empty, Now, PurgeDelay);

        Assert.True(report.Allowed);
        Assert.Empty(report.Refusals);
        Assert.StartsWith("sha256:", report.PlanHash);
        Assert.False(string.IsNullOrEmpty(report.WindowId));
        Assert.Equal(Now + PurgeDelay, report.PurgeNotBefore);
        // planner's staged order: the VM goes first (its disk must outlive it), then the disk,
        // the NIC after its VM, and the vnet last
        var staged = report.Stages.SelectMany(s => s).ToList();
        Assert.True(staged.IndexOf(Vm) < staged.IndexOf(Disk));
        Assert.True(staged.IndexOf(Vm) < staged.IndexOf(Nic));
        Assert.True(staged.IndexOf(Nic) < staged.IndexOf(Vnet));
        Assert.Contains(report.Caveats, c => c.StartsWith("locks-not-evaluated:", StringComparison.Ordinal));
        Assert.Contains(report.Caveats, c => c.StartsWith("rbac-not-evaluated:", StringComparison.Ordinal));
        Assert.Contains(report.Caveats, c => c.StartsWith("backups-not-evaluated:", StringComparison.Ordinal));
    }

    [Fact]
    public void Decom_IsDeterministic_AcrossCalls()
    {
        IReadOnlyList<InventoryItem> inventory = [VmItem(), NicItem(), DiskItem(), VnetItem()];
        var first = AdsToolEngine.DecomPlan(inventory, [Vm, Nic, Disk, Vnet], GuardrailPolicy.Empty, Now, PurgeDelay);
        var second = AdsToolEngine.DecomPlan(inventory, [Disk, Vnet, Vm, Nic], GuardrailPolicy.Empty, Now, PurgeDelay);

        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(first.WindowId, second.WindowId);
        Assert.Equal(first.PurgeNotBefore, second.PurgeNotBefore);
        Assert.Equal(first.Stages.SelectMany(s => s), second.Stages.SelectMany(s => s));
    }
}
