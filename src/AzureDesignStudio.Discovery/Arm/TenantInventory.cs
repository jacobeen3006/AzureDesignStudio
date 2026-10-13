using System.Text.Json;
using static AzureDesignStudio.Discovery.Arm.JsonStr;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Discovery.Arm;

public sealed record SubscriptionInfo(string SubscriptionId, string DisplayName, string TenantId, string State);
public sealed record ManagementGroupInfo(string Name, string DisplayName, string? ParentId, IReadOnlyList<string> ChildIds);

/// <summary>
/// Scope tree (v2 plan 7.3): tenant → management group → subscription → resource group,
/// with unenrolled subscriptions flagged (visible in ARM, invisible to ARG).
/// </summary>
public sealed class TenantInventory(ArmTransport transport, ResourceGraphClient graph)
{
    public async Task<IReadOnlyList<SubscriptionInfo>> ListSubscriptionsAsync(CancellationToken ct = default)
    {
        var items = await transport.GetListAsync(
            $"{transport.Endpoints.ManagementEndpoint}/subscriptions?api-version={transport.Endpoints.ApiVersion}", "value", ct)
            .ConfigureAwait(false);
        return items.Select(s => new SubscriptionInfo(Str(s, "subscriptionId"), Str(s, "displayName"), Str(s, "tenantId"), Str(s, "state"))).ToList();
    }

    public async Task<IReadOnlyList<ManagementGroupInfo>> ListManagementGroupsAsync(CancellationToken ct = default)
    {
        var items = await transport.GetListAsync(
            $"{transport.Endpoints.ManagementEndpoint}/providers/Microsoft.Management/managementGroups?api-version=2023-04-01", "value", ct)
            .ConfigureAwait(false);

        return items.Select(mg =>
        {
            var props = mg.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var children = props.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array
                ? ch.EnumerateArray().Select(c => Str(c, "name")).Where(n => n.Length > 0).ToList()
                : [];
            return new ManagementGroupInfo(
                Str(mg, "name"),
                Str(props, "displayName"),
                props.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object ? Str(parent, "id") : null,
                children);
        }).ToList();
    }

    /// <summary>Resource groups per subscription via ARG (only enrolled subscriptions answer).</summary>
    public async Task<IReadOnlyDictionary<string, List<(string Id, string Name)>>> ListResourceGroupsAsync(CancellationToken ct = default)
    {
        var result = await graph.QueryAllAsync(
            "ResourceContainers | where type == 'microsoft.resources/subscriptions/resourcegroups' | project id, name, subscriptionId",
            ct: ct).ConfigureAwait(false);

        var bySub = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Rows)
        {
            var sub = Str(row, "subscriptionId");
            if (!bySub.TryGetValue(sub, out var list)) bySub[sub] = list = [];
            list.Add((Str(row, "id"), Str(row, "name")));
        }
        return bySub;
    }

    /// <summary>Assembles the full tree; unenrolled = ARM-known but ARG-silent.</summary>
    public async Task<ScopeNode> BuildScopeTreeAsync(CancellationToken ct = default)
    {
        var subs = await ListSubscriptionsAsync(ct).ConfigureAwait(false);
        IReadOnlyList<ManagementGroupInfo> mgs = [];
        try { mgs = await ListManagementGroupsAsync(ct).ConfigureAwait(false); }
        catch (ResourceGraphException) { /* no MG read permission - subscriptions hang under the tenant directly */ }
        var rgs = await ListResourceGroupsAsync(ct).ConfigureAwait(false);
        var enrolledKeys = new HashSet<string>(rgs.Keys, StringComparer.OrdinalIgnoreCase);

        var tenantId = subs.FirstOrDefault()?.TenantId ?? "";
        var mgLookup = mgs.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var childIds = new HashSet<string>(mgs.SelectMany(m => m.ChildIds), StringComparer.OrdinalIgnoreCase);
        var roots = mgs.Where(m => !childIds.Contains(m.Name)).ToList();

        ScopeNode SubNode(SubscriptionInfo s) => new(
            ScopeKind.Subscription,
            s.DisplayName.Length > 0 ? s.DisplayName : s.SubscriptionId,
            s.SubscriptionId,
            rgs.TryGetValue(s.SubscriptionId, out var list) ? [.. list.Select(r => new ScopeNode(ScopeKind.ResourceGroup, r.Name, r.Id))] : [],
            Enrolled: enrolledKeys.Contains(s.SubscriptionId));

        // Which MG each subscription hangs under (MG children entries carrying a subscription GUID).
        var subToMg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mg in mgs)
            foreach (var child in mg.ChildIds)
                if (subs.Any(s => string.Equals(s.SubscriptionId, child, StringComparison.OrdinalIgnoreCase)))
                    subToMg[child] = mg.Name;

        ScopeNode BuildMg(ManagementGroupInfo mg)
        {
            var children = mg.ChildIds
                .Where(mgLookup.ContainsKey)
                .Select(c => BuildMg(mgLookup[c]))
                .Concat(subs.Where(s => subToMg.GetValueOrDefault(s.SubscriptionId) == mg.Name).Select(SubNode))
                .ToList();
            return new ScopeNode(ScopeKind.ManagementGroup, mg.DisplayName.Length > 0 ? mg.DisplayName : mg.Name, mg.Name, children);
        }

        var treeRoots = roots.Select(BuildMg)
            .Concat(subs.Where(s => !subToMg.ContainsKey(s.SubscriptionId)).Select(SubNode))
            .ToList();

        return new ScopeNode(ScopeKind.Tenant, tenantId, tenantId, treeRoots);
    }
}

public enum ScopeKind { Tenant, ManagementGroup, Subscription, ResourceGroup }

/// <summary>One node of the scope tree. <see cref="Enrolled"/>=ARG sees it (only meaningful for subscriptions).</summary>
public sealed record ScopeNode(ScopeKind Kind, string Name, string Id, IReadOnlyList<ScopeNode>? Children = null, bool Enrolled = true)
{
    public IReadOnlyList<ScopeNode> Nodes => Children ?? [];
}

internal static class JsonStr
{
    public static string Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
