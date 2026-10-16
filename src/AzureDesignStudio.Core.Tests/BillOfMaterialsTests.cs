using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Report;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// [32] Bill of materials as git evidence. The whole point is that the bytes only move when
/// tenant state moves: shuffled input and re-ordered provider payloads render identical
/// documents, and the self-describing hash changes exactly when content changes.
/// </summary>
public class BillOfMaterialsTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Vm = Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vmA";

    private static InventoryItem Item(string id, string? props = null,
        IReadOnlyDictionary<string, string>? tags = null) =>
        new(id, id.Split('/')[^1], "microsoft.compute/virtualmachines", "westeurope", "rg", "sub1",
            tags ?? new Dictionary<string, string>(),
            props is null ? null : JsonDocument.Parse(props).RootElement.Clone());

    [Fact]
    public void ByteStabilityAcrossInputAndProviderOrdering()
    {
        var a = Item(Vm, """{"zeta":1,"alpha":{"y":2,"x":[3,1]},"mid":"m"}""",
            new Dictionary<string, string> { ["team"] = "t", ["env"] = "p" });
        var b = Item(Sub + "/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vmB", """{"provisioningState":"Succeeded"}""");

        var first = BillOfMaterials.Render([a, b], "subscriptions/sub1");
        var shuffled = BillOfMaterials.Render([b, a], "subscriptions/sub1");
        // Same tenant state, different provider serialization order (a re-scan can reorder keys).
        var reordered = BillOfMaterials.Render(
            [Item(Vm, """{"mid":"m","alpha":{"x":[3,1],"y":2},"zeta":1}""",
                new Dictionary<string, string> { ["env"] = "p", ["team"] = "t" }), b],
            "subscriptions/sub1");

        Assert.Equal(first, shuffled);
        Assert.Equal(first, reordered); // property and tag keys are canonicalized, order is noise
    }

    [Fact]
    public void ContentHashBindsResourcesAndMovesOnlyWithState()
    {
        var clean = Item(Vm, """{"size":1}""");
        var edited = Item(Vm, """{"size":2}""");

        var doc = BillOfMaterials.Render([clean], "subscriptions/sub1");
        var changed = BillOfMaterials.Render([edited], "subscriptions/sub1");

        using var json = JsonDocument.Parse(doc);
        var hash = json.RootElement.GetProperty("contentHash").GetString()!;
        Assert.StartsWith("sha256:", hash);
        Assert.DoesNotContain(hash, changed); // one property value flips the whole hash line
        // A second render of the same state carries the exact hash back.
        Assert.Contains(hash, BillOfMaterials.Render([clean], "subscriptions/sub1"));
        // Scope is envelope, not content: naming it differently never fakes a change.
        Assert.Contains(hash, BillOfMaterials.Render([clean], "renamed-scope"));
    }

    [Fact]
    public void DuplicateRowsCollapseAndCountReflectsRealResources()
    {
        var upper = Item(Vm);
        var dupe = Item(Vm.ToUpperInvariant());

        var doc = BillOfMaterials.Render([upper, dupe], "s");

        using var json = JsonDocument.Parse(doc);
        Assert.Equal(1, json.RootElement.GetProperty("resourceCount").GetInt32());
        Assert.Single(json.RootElement.GetProperty("resources").EnumerateArray());
    }

    [Fact]
    public void EmptyInventoryIsStillADocument()
    {
        var doc = BillOfMaterials.Render([], "subscriptions/none");

        using var json = JsonDocument.Parse(doc);
        Assert.Equal(BillOfMaterials.Schema, json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("resourceCount").GetInt32());
        Assert.StartsWith("sha256:", json.RootElement.GetProperty("contentHash").GetString());
        Assert.Empty(json.RootElement.GetProperty("resources").EnumerateArray());
    }

    [Fact]
    public void NoClockInsideTheDocument()
    {
        var doc = BillOfMaterials.Render([Item(Vm)], "s");

        Assert.DoesNotContain(DateTime.UtcNow.ToString("yyyy"), doc);
        foreach (var token in new[] { "asOf", "timestamp", "fetched" })
            Assert.DoesNotContain(token, doc, StringComparison.OrdinalIgnoreCase);
    }
}
