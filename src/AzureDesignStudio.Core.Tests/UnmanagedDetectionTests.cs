using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Unmanaged;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 9.3 - unmanaged detection + do-not-touch guardrail list v1 over fixture inventories.
/// </summary>
public class UnmanagedDetectionTests
{
    private const string Sub = "/subscriptions/s1/resourcegroups/rg1/providers";

    private static InventoryItem Item(string name, string type,
        string? id = null, IReadOnlyDictionary<string, string>? tags = null, string? propertiesJson = null) =>
        new(id ?? $"{Sub}/{type.Split('/')[1]}/{name}", name, type, "westeurope", "rg1", "s1",
            new Dictionary<string, string>(tags ?? new Dictionary<string, string>(), System.StringComparer.OrdinalIgnoreCase), // same comparer as InventoryNormalizer
            propertiesJson is null ? null : JsonDocument.Parse(propertiesJson).RootElement.Clone());

    private static ManagementAudit Run(GuardrailPolicy policy, params InventoryItem[] items) =>
        UnmanagedDetector.Audit(items, policy);

    // --- detection ---

    [Fact]
    public void PlainResource_IsUnmanaged()
    {
        var audit = Run(GuardrailPolicy.Empty, Item("vnet1", "microsoft.network/virtualnetworks"));
        var f = Assert.Single(audit.Unmanaged);
        Assert.Equal("no-orchestration-marker", f.Reason);
        Assert.True(audit.UnmanagedIds.Contains(f.Id));
    }

    [Fact]
    public void ManagedByProperty_IsSilent()
    {
        var disk = Item("disk1", "microsoft.compute/disks",
            propertiesJson: """{"managedBy":"/subscriptions/s1/rg/vm/vm1"}""");
        var audit = Run(GuardrailPolicy.Empty, disk);
        Assert.Empty(audit.Unmanaged);
        Assert.Empty(audit.ProtectedResources);
    }

    [Fact]
    public void OrchestratorTag_IsSilent_EmptyValueIsNotAMarker()
    {
        var aks = Item("nodepools1", "microsoft.containerservice/managedclusters",
            tags: new Dictionary<string, string> { ["providers/managedby"] = "aks_abc" });
        var stale = Item("x", "microsoft.storage/storageaccounts",
            tags: new Dictionary<string, string> { ["managed-by"] = "" });

        var audit = Run(GuardrailPolicy.Empty, aks, stale);
        Assert.Equal([stale.Id], audit.Unmanaged.Select(f => f.Id));
    }

    [Fact]
    public void VmssMember_IsSilent()
    {
        var vm = Item("vm0", "microsoft.compute/virtualmachines",
            id: "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachineScaleSets/set1/virtualMachines/vm0");
        Assert.Empty(Run(GuardrailPolicy.Empty, vm).Unmanaged);
    }

    [Fact]
    public void Audit_IsDeterministic_OrdinalById()
    {
        var b = Item("beta", "microsoft.network/virtualnetworks");
        var a = Item("alpha", "microsoft.network/virtualnetworks");
        Assert.Equal([a.Id, b.Id], Run(GuardrailPolicy.Empty, b, a).Unmanaged.Select(f => f.Id));
    }

    // --- guardrail policy ---

    [Fact]
    public void ScopeRule_ProtectsWholeSubtree_OnSegmentBoundary()
    {
        var inSub = Item("vnet1", "microsoft.network/virtualnetworks", id: "/Subscriptions/S1/resourceGroups/rg/providers/x/y/v");
        var sibling = Item("other", "microsoft.network/virtualnetworks", id: "/subscriptions/s12/resourceGroups/rg/providers/x/y/v");

        var policy = GuardrailPolicy.FromJson("""{"scopes":["subscriptions/s1"]}""");
        var audit = Run(policy, inSub, sibling);

        var f = Assert.Single(audit.ProtectedResources);
        Assert.Equal(inSub.Id, f.Id);
        Assert.Equal("guardrail-scope:subscriptions/s1", f.Reason);
        Assert.Contains(sibling.Id, audit.UnmanagedIds); // s12 is not under s1
    }

    [Fact]
    public void TagRule_ValuePinned_CaseInsensitive()
    {
        var prod = Item("sa1", "microsoft.storage/storageaccounts",
            tags: new Dictionary<string, string> { ["Environment"] = "PROD" });
        var dev = Item("sa2", "microsoft.storage/storageaccounts",
            tags: new Dictionary<string, string> { ["environment"] = "dev" });

        var policy = GuardrailPolicy.FromJson("""{"tags":[{"key":"environment","value":"prod"}]}""");
        var f = Assert.Single(Run(policy, prod, dev).ProtectedResources);
        Assert.Equal("guardrail-tag:environment=prod", f.Reason);
    }

    [Fact]
    public void TypeRule_CoversNamespaceAndChildren()
    {
        var cluster = Item("k1", "microsoft.kusto/clusters");
        var script = Item("s1", "microsoft.kusto/clusters/scripts");
        var vm = Item("vm1", "microsoft.compute/virtualmachines");

        var policy = GuardrailPolicy.FromJson("""{"types":["microsoft.kusto"]}""");
        var audit = Run(policy, cluster, script, vm);
        Assert.Equal(2, audit.ProtectedResources.Count);
        Assert.Contains(vm.Id, audit.UnmanagedIds);
    }

    [Fact]
    public void LegacyDoNotTouch_IsProtectedUnderAnyConfig()
    {
        var flagged = Item("db1", "microsoft.dbforpostgresql/servers",
            tags: new Dictionary<string, string> { ["do-not-touch"] = "true" });
        var f = Assert.Single(Run(GuardrailPolicy.Empty, flagged).ProtectedResources);
        Assert.Equal("tag:do-not-touch", f.Reason);
    }

    [Fact]
    public void GuardrailOutranksManagedMarker()
    {
        var managedAndProtected = Item("d1", "microsoft.compute/disks",
            id: "/subscriptions/locked/providers/microsoft.compute/disks/d1",
            propertiesJson: """{"managedBy":"/x"}""");
        var policy = GuardrailPolicy.FromJson("""{"scopes":["subscriptions/locked"]}""");
        Assert.Single(Run(policy, managedAndProtected).ProtectedResources);
    }

    [Fact]
    public void FromJson_TolerantShape()
    {
        var policy = GuardrailPolicy.FromJson("""{"tags":[{"key":"pinned"},{"value":"keyless-is-dropped"},{"key":"real"}],"types":[],"unknown":1}""");
        Assert.Empty(policy.ProtectedScopes);
        Assert.Equal(["pinned", "real"], policy.ProtectedTags.Select(t => t.Key));
        Assert.Null(policy.ProtectedTags[0].Value);
    }
}
