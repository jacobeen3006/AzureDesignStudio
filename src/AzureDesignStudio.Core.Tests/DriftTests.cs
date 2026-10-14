using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.ChangeFeed;
using AzureDesignStudio.Discovery.Drift;
using AzureDesignStudio.Discovery.Inventory;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 9.2 - drift engine: property-tree diff over typeless inventories
/// (scan↔scan / design↔design), scan↔now overlay over the change feed, and the ARM
/// template reader that lets a design participate in both. Offline.
/// </summary>
public class DriftTests
{
    private const string Vnet = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/virtualnetworks/vnet1";
    private const string Vm = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.compute/virtualmachines/vm1";

    // Raw is the properties object itself (matches InventoryNormalizer), so leaf paths
    // come out as "properties.<name>".
    private static InventoryItem Item(string id, string type, string name, string propertiesJson = "{}", string location = "westeurope", Dictionary<string, string>? tags = null) =>
        new(id, name, type, location, "rg1", "s1", tags ?? new(StringComparer.OrdinalIgnoreCase),
            propertiesJson == "{}" ? null : JsonDocument.Parse(propertiesJson).RootElement.Clone());

    private static ChangeFeedEntry Change(string target, DateTimeOffset when, string type = "Update", string? by = null) =>
        new($"{target}:{when:HHmmss}", when, target, "microsoft.compute/virtualmachines", type,
            null, by, null, null, null, 1, null);

    // --- ScanDiffer ---

    [Fact]
    public void Diff_IdenticalSets_IsEmpty()
    {
        var items = new[] { Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", """{"a":1}""") };
        var diff = new ScanDiffer().Diff(items, items);
        Assert.True(diff.IsEmpty);
        Assert.Equal(diff.BeforeHash, diff.AfterHash);
    }

    [Fact]
    public void Diff_AddedRemovedAndChanged_ClassifiesEachResource()
    {
        var before = new[]
        {
            Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", """{"a":1,"b":2}"""),
            Item(Vm, "microsoft.compute/virtualmachines", "vm1"),
        };
        var after = new[]
        {
            Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", """{"a":9,"c":3}"""),
            Item("/subscriptions/s1/resourcegroups/rg1/providers/microsoft.storage/storageaccounts/sa1", "microsoft.storage/storageaccounts", "sa1"),
        };

        var diff = new ScanDiffer().Diff(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(1, diff.ChangedResources);

        var vnet = diff.Drift.Single(d => d.ResourceId == Vnet);
        Assert.Equal(ResourceChangeKind.Changed, vnet.Kind);
        Assert.Contains(vnet.Properties, p => p.Path == "properties.a" && p.PreviousJson == "1" && p.NewJson == "9");
        Assert.Contains(vnet.Properties, p => p.Path == "properties.b" && p.NewJson is null);   // removed leaf
        Assert.Contains(vnet.Properties, p => p.Path == "properties.c" && p.PreviousJson is null); // added leaf
    }

    [Fact]
    public void Diff_NestedArraysAndTags_PathsAreStableLeafKeys()
    {
        var before = new[] { Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", """{"x":{"y":[{"z":1}]}}""", tags: new(StringComparer.OrdinalIgnoreCase) { ["env"] = "dev" }) };
        var after = new[] { Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", """{"x":{"y":[{"z":2}]}}""", tags: new(StringComparer.OrdinalIgnoreCase) { ["env"] = "prod" }) };

        var diff = new ScanDiffer().Diff(before, after);
        var vnet = diff.Drift.Single();

        Assert.Contains(vnet.Properties, p => p.Path == "properties.x.y[0].z" && p.PreviousJson == "1" && p.NewJson == "2");
        Assert.Contains(vnet.Properties, p => p.Path == "tags.env");
        Assert.Equal(1, diff.ChangedResources);
    }

    [Fact]
    public void Diff_LocationDrift_IsDetected()
    {
        var before = new[] { Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", location: "westeurope") };
        var after = new[] { Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", location: "northeurope") };
        var diff = new ScanDiffer().Diff(before, after);
        Assert.Contains(diff.Drift.Single().Properties, p => p.Path == "location");
    }

    [Fact]
    public void Diff_OnlyVolatileChurn_DoesNotCountAsChanged()
    {
        var before = new[] { Item(Vm, "microsoft.compute/virtualmachines", "vm1", """{"provisioningState":"Succeeded","sku":"basic"}""") };
        var after = new[] { Item(Vm, "microsoft.compute/virtualmachines", "vm1", """{"provisioningState":"Updating","sku":"basic"}""") };

        var diff = new ScanDiffer().Diff(before, after);
        Assert.Equal(0, diff.ChangedResources);
        Assert.True(diff.IsEmpty); // provisioningState churn is not operator-relevant drift
    }

    [Fact]
    public void Diff_MassiveChange_IsCappedPerResource()
    {
        var big = "{" + string.Join(",", Enumerable.Range(0, ScanDiffer.MaxPropertyDiffs + 50).Select(i => $"\"p{i}\":1")) + "}";
        var before = new[] { Item(Vm, "microsoft.compute/virtualmachines", "vm1", "{" + string.Join(",", Enumerable.Range(0, ScanDiffer.MaxPropertyDiffs + 50).Select(i => $"\"p{i}\":0")) + "}") };
        var after = new[] { Item(Vm, "microsoft.compute/virtualmachines", "vm1", big) };

        var diff = new ScanDiffer().Diff(before, after);
        Assert.InRange(diff.Drift.Single().Properties.Count, ScanDiffer.MaxPropertyDiffs, ScanDiffer.MaxPropertyDiffs + 1);
    }

    // --- LiveDrift overlay ---

    [Fact]
    public void Overlay_FlagsModifiedNewAndDeleted_IgnoresPreScanChanges()
    {
        var scannedAt = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var snapshot = new[]
        {
            Item(Vm, "microsoft.compute/virtualmachines", "vm1"),
            Item("/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/virtualnetworks/gone", "microsoft.network/virtualnetworks", "gone"),
        };
        var changes = new[]
        {
            Change(Vm, scannedAt.AddHours(1), by: "alice@contoso.com"),          // modified after scan
            Change("/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/virtualnetworks/gone", scannedAt.AddHours(2), "Delete"), // deleted
            Change("/subscriptions/s1/resourcegroups/rg1/providers/microsoft.web/sites/newapp", scannedAt.AddHours(3)), // not in scan -> new
            Change(Vm, scannedAt.AddHours(-1)),                                   // before the scan -> ignore
        };

        var drift = LiveDrift.Overlay(snapshot, changes, scannedAt);

        Assert.Equal(3, drift.Count);
        Assert.Equal(LiveDriftStatus.New, drift[0].Status);          // newest first
        Assert.Equal(LiveDriftStatus.Deleted, drift[1].Status);
        var vm = drift[2];
        Assert.Equal(LiveDriftStatus.Modified, vm.Status);
        Assert.Equal("alice@contoso.com", vm.ChangedBy);
    }

    [Fact]
    public void Overlay_KeepsOnlyNewestChangePerResource()
    {
        var scannedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var snapshot = new[] { Item(Vm, "microsoft.compute/virtualmachines", "vm1") };
        var changes = new[]
        {
            Change(Vm, scannedAt.AddHours(1), by: "bob"),
            Change(Vm, scannedAt.AddHours(5), by: "carol"),
        };

        var drift = LiveDrift.Overlay(snapshot, changes, scannedAt);
        Assert.Single(drift);
        Assert.Equal("carol", drift[0].ChangedBy);
    }

    // --- DesignTemplateReader ---

    [Fact]
    public void TemplateReader_ParsesDesignSideIntoComparableItems()
    {
        const string template = """
        {
          "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
          "resources": [
            {
              "type": "Microsoft.Network/virtualNetworks",
              "apiVersion": "2023-04-01",
              "name": "vnet1",
              "id": "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Network/virtualNetworks/vnet1",
              "location": "WestEurope",
              "tags": { "env": "prod" },
              "properties": { "addressSpace": { "addressPrefixes": ["10.0.0.0/16"] } }
            }
          ]
        }
        """;

        var items = DesignTemplateReader.Read(template);
        var vnet = Assert.Single(items);

        Assert.Equal(Vnet, vnet.Id);                              // id normalized lowercase
        Assert.Equal("microsoft.network/virtualnetworks", vnet.Type);
        Assert.Equal("westeurope", vnet.Location);
        Assert.Equal("rg1", vnet.ResourceGroup);
        Assert.Equal("s1", vnet.SubscriptionId);
        Assert.Equal("prod", vnet.Tags["env"]);
    }

    [Fact]
    public void TemplateReader_ToLiveItems_ComparisonIsDeploymentDiff()
    {
        const string template = """
        { "resources": [ { "type": "Microsoft.Network/virtualNetworks", "name": "vnet1", "id": "/SUBSCRIPTIONS/s1/resourcegroups/rg1/providers/Microsoft.Network/virtualNetworks/vnet1", "location": "westeurope", "properties": { "a": 1 } } ] }
        """;
        var design = DesignTemplateReader.Read(template);
        var live = new[] { Item(Vnet, "microsoft.network/virtualnetworks", "vnet1", """{"a":2}""") };

        var diff = new ScanDiffer().Diff(design, live); // design → live: what deploy would change

        Assert.Equal(1, diff.ChangedResources);
        var prop = diff.Drift.Single().Properties.Single();
        Assert.Equal("properties.a", prop.Path);
        Assert.Equal("1", prop.PreviousJson);
        Assert.Equal("2", prop.NewJson);
    }
}
