using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Analysis;

/// <summary>How one resource references another; every kind maps to a concrete ARM property.</summary>
public enum TopologyEdgeKind
{
    /// <summary>VM references a NIC (networkProfile.networkInterfaces).</summary>
    Uses,
    /// <summary>Disk is attached to a compute resource (disks.properties.managedBy, dataDisks).</summary>
    AttachedTo,
    /// <summary>Resource sits in a subnet (ipConfigurations[].subnet, privateEndpoints.subnet).</summary>
    InSubnet,
    /// <summary>NSG guards a NIC or subnet (networkSecurityGroup reference).</summary>
    ProtectedBy,
    /// <summary>VNet peering to another vnet (virtualNetworkPeerings).</summary>
    PeersWith,
    /// <summary>Load balancer fronts a NIC / uses a public IP (backend pools, frontend IPs).</summary>
    LoadBalances,
    /// <summary>Private endpoint connects to its service (privateLinkServiceId).</summary>
    ConnectsTo,
    /// <summary>Site/app hosted in a plan or environment (serverFarmId, managedEnvironmentId).</summary>
    HostedIn,
    /// <summary>Subnet sends traffic through a UDR (subnet.routeTable).</summary>
    RoutesVia,
    /// <summary>Resource streams diagnostics to a Log Analytics workspace (diagnosticSettings).</summary>
    DiagnosticsTo,
}

public sealed record TopologyNode(string Id, string Name, string Type, string? ResourceGroup, bool External)
{
    /// <summary>Parent by ARM id nesting (sql db → server, subnet → vnet); null at top level.</summary>
    public string? ParentId
    {
        get
        {
            var i = Id.LastIndexOf("/" + Name, StringComparison.OrdinalIgnoreCase);
            return i > 0 ? Id[..i] : null;
        }
    }
}

public sealed record TopologyEdge(string From, string To, TopologyEdgeKind Kind, string? Detail = null);

/// <summary>Derived graph over one scan. Node ids are ARM ids - stable across rescans (8.2).</summary>
public sealed class TopologyGraph
{
    public required IReadOnlyDictionary<string, TopologyNode> Nodes { get; init; }
    public required IReadOnlyList<TopologyEdge> Edges { get; init; }
}

/// <summary>
/// Relationship derivation (v2 plan 8.1): edges come ONLY from ids present in resource
/// configuration - never inferred by proximity, naming or decoration. References pointing
/// outside the scanned scope become External ghost nodes so the drawing never silently
/// drops a connection. Subnets are materialized as nodes because NICs, endpoints and NSGs
/// reference them even though ARG does not list them as resources.
/// </summary>
public static class TopologyDeriver
{
    public static TopologyGraph Derive(IReadOnlyList<InventoryItem> items)
    {
        var nodes = new Dictionary<string, TopologyNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<TopologyEdge>();
        var builder = new GraphBuilder(nodes, edges);

        foreach (var item in items)
        {
            var self = builder.Real(item);
            var props = item.Raw;
            if (props is null) continue;

            switch (item.Type.ToLowerInvariant())
            {
                case "microsoft.compute/virtualmachines":
                    foreach (var nic in IdsAt(props, "networkProfile", "networkInterfaces"))
                        builder.Edge(self, builder.Reference(nic), TopologyEdgeKind.Uses);
                    foreach (var diskName in Arr(props, "storageProfile", "dataDisks")
                                 .Select(d => AtString(d, "managedDisk", "name")).OfType<string>())
                        builder.Edge(self, builder.DiskNamed(item, diskName), TopologyEdgeKind.AttachedTo, "data disk");
                    break;

                case "microsoft.compute/disks":
                    if (StringAt(props, "managedBy") is { } owner)
                        builder.Edge(self, builder.Reference(owner), TopologyEdgeKind.AttachedTo);
                    break;

                case "microsoft.network/networkinterfaces":
                    if (IdAt(props.Value, "networkSecurityGroup") is { } nicNsg)
                        builder.Edge(self, builder.Reference(nicNsg), TopologyEdgeKind.ProtectedBy);
                    foreach (var cfg in Arr(props, "ipConfigurations"))
                    {
                        var p = cfg.TryGetProperty("properties", out var cp) ? cp : cfg;
                        if (IdAt(p, "subnet") is { } nicSubnet)
                            builder.Edge(self, builder.Reference(nicSubnet), TopologyEdgeKind.InSubnet);
                        if (IdAt(p, "publicIPAddress") is { } pip)
                            builder.Edge(self, builder.Reference(pip), TopologyEdgeKind.LoadBalances, "public IP");
                    }
                    break;

                case "microsoft.network/virtualnetworks":
                    foreach (var peering in Arr(props, "virtualNetworkPeerings"))
                    {
                        var pp = peering.TryGetProperty("properties", out var peeringProps) ? peeringProps : peering;
                        if (IdAt(pp, "remoteVirtualNetwork") is { } remote)
                            builder.Edge(self, builder.Reference(remote), TopologyEdgeKind.PeersWith,
                                StrOf(peering, "name"));
                    }
                    foreach (var subnet in Arr(props, "subnets"))
                    {
                        if (StrOf(subnet, "name") is not { } subnetName) continue;
                        var node = builder.Subnet(item, subnetName);
                        var sp = subnet.TryGetProperty("properties", out var subnetProps) ? subnetProps : subnet;
                        if (IdAt(sp, "networkSecurityGroup") is { } subnetNsg)
                            builder.Edge(node, builder.Reference(subnetNsg), TopologyEdgeKind.ProtectedBy);
                        if (IdAt(sp, "routeTable") is { } routeTable)
                            builder.Edge(node, builder.Reference(routeTable), TopologyEdgeKind.RoutesVia);
                    }
                    break;

                case "microsoft.network/loadbalancers":
                    foreach (var pool in Arr(props, "backendAddressPools"))
                    {
                        var poolProps = pool.TryGetProperty("properties", out var pp2) ? pp2 : pool;
                        foreach (var backend in IdsAt(poolProps, "backendIPConfigurations"))
                            builder.Edge(self, builder.Reference(NicOfIpConfig(backend)), TopologyEdgeKind.LoadBalances,
                                StrOf(pool, "name"));
                    }
                    foreach (var frontend in Arr(props, "frontendIPConfigurations"))
                    {
                        var fp = frontend.TryGetProperty("properties", out var fpp) ? fpp : frontend;
                        if (IdAt(fp, "publicIPAddress") is { } frontPip)
                            builder.Edge(self, builder.Reference(frontPip), TopologyEdgeKind.LoadBalances, "frontend IP");
                    }
                    break;

                case "microsoft.network/privateendpoints":
                    if (IdAt(props.Value, "subnet") is { } peSubnet)
                        builder.Edge(self, builder.Reference(peSubnet), TopologyEdgeKind.InSubnet);
                    foreach (var connectionSource in new[] { "manualPrivateLinkServiceConnections", "privateLinkServiceConnections" })
                        foreach (var connection in Arr(props, connectionSource))
                        {
                            var cp = connection.TryGetProperty("properties", out var cpp) ? cpp : connection;
                            if (StringAt(cp, "privateLinkServiceId") is { } service)
                                builder.Edge(self, builder.Reference(service), TopologyEdgeKind.ConnectsTo);
                        }
                    break;

                case "microsoft.web/sites":
                    if (StringAt(props, "serverFarmId") is { } farm)
                        builder.Edge(self, builder.Reference(farm), TopologyEdgeKind.HostedIn);
                    break;

                case "microsoft.app/containerapps":
                    if (StringAt(props, "managedEnvironmentId") is { } environment)
                        builder.Edge(self, builder.Reference(environment), TopologyEdgeKind.HostedIn);
                    break;

                case "microsoft.insights/diagnosticsettings":
                    var target = StringAt(props, "workspaceId")
                        ?? StringAt(props, "eventHubAuthorizationRuleId")
                        ?? StringAt(props, "storageAccountId");
                    var monitored = StringAt(props, "resourceUri");
                    if (target is not null && monitored is not null &&
                        !string.Equals(target, monitored, StringComparison.OrdinalIgnoreCase))
                        builder.Edge(builder.Reference(monitored), builder.Reference(target), TopologyEdgeKind.DiagnosticsTo);
                    break;
            }
        }

        return new TopologyGraph
        {
            Nodes = nodes,
            Edges = edges
                .DistinctBy(e => (e.From.ToLowerInvariant(), e.To.ToLowerInvariant(), e.Kind, e.Detail))
                .ToList(),
        };
    }

    /// <summary>ipConfig ids nest under their NIC: …/networkInterfaces/nic/ipConfigurations/cfg → nic id.</summary>
    private static string NicOfIpConfig(string id)
    {
        var i = id.IndexOf("/ipConfigurations/", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? id[..i] : id;
    }

    /// <summary>
    /// First registration wins the id casing (edges share one key per resource); a later
    /// real row promotes an earlier ghost instead of creating a second node.
    /// </summary>
    private sealed class GraphBuilder(
        Dictionary<string, TopologyNode> nodes,
        List<TopologyEdge> edges)
    {
        private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);

        public string Real(InventoryItem item)
        {
            Register(item.Id, item.Name, item.Type, item.ResourceGroup, external: false);
            return _canonical[item.Id];
        }

        /// <summary>A referenced id: real when scanned, otherwise an External ghost so edges stay honest.</summary>
        public string Reference(string id)
        {
            if (!_canonical.ContainsKey(id))
                Register(id, id[(id.LastIndexOf('/') + 1)..], GuessType(id), ResourceGroupOf(id), external: true);
            return _canonical[id];
        }

        public string Subnet(InventoryItem vnet, string subnetName)
        {
            Register($"{vnet.Id}/subnets/{subnetName}", subnetName,
                "Microsoft.Network/virtualNetworks/subnets", vnet.ResourceGroup, external: false);
            return _canonical[$"{vnet.Id}/subnets/{subnetName}"];
        }

        /// <summary>Disks are named in VM config; rebuild the canonical id from the VM's scope.</summary>
        public string DiskNamed(InventoryItem vm, string diskName) =>
            Reference($"/subscriptions/{vm.SubscriptionId}/resourceGroups/{vm.ResourceGroup}/providers/Microsoft.Compute/disks/{diskName}");

        public void Edge(string from, string to, TopologyEdgeKind kind, string? detail = null)
        {
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                edges.Add(new TopologyEdge(from, to, kind, detail));
        }

        private void Register(string id, string name, string type, string? resourceGroup, bool external)
        {
            if (!_canonical.TryGetValue(id, out var canonical))
            {
                _canonical[id] = id;
                nodes[id] = new TopologyNode(id, name, type, resourceGroup, external);
            }
            else if (!external && nodes[canonical].External)
            {
                // real row arrived for a previously-referenced ghost: same key, honest metadata
                nodes[canonical] = nodes[canonical] with { Name = name, Type = type, ResourceGroup = resourceGroup, External = false };
            }
        }

        private static string GuessType(string id)
        {
            // …/providers/NS/TYPE[/child…]/name — first NS/TYPE pair after "providers".
            var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 2; i++)
                if (string.Equals(parts[i], "providers", StringComparison.OrdinalIgnoreCase))
                    return $"{parts[i + 1]}/{parts[i + 2]}";
            return "external";
        }

        private static string? ResourceGroupOf(string id)
        {
            var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
                if (string.Equals(parts[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1];
            return null;
        }
    }

    // --- raw JSON access: everything optional, ARM shapes vary by api-version ---

    private static JsonElement? ChildBoxed(JsonElement? e, string name) =>
        e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.Object || v.ValueKind == JsonValueKind.Array) ? v : null;

    private static IEnumerable<JsonElement> Arr(JsonElement? e, params string[] path)
    {
        var current = ChildBoxed(e, path[0]);
        for (var i = 1; i < path.Length && current is not null; i++)
            current = ChildBoxed(current, path[i]);
        return current is { } el && el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : [];
    }

    private static string? StringAt(JsonElement? e, string name) =>
        e is { } el && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? AtString(JsonElement e, params string[] path)
    {
        var current = e;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next)) return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>id of a property that is either a plain string or an object with an id member.</summary>
    private static string? IdAt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString()
                : v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() : null
            : null;

    private static string? StrOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>id fields of every element of an array at the given path (objects with .id, or plain strings).</summary>
    private static IEnumerable<string> IdsAt(JsonElement? e, params string[] path)
    {
        foreach (var element in Arr(e, path))
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } s) yield return s;
            else if (element.ValueKind == JsonValueKind.Object && IdAt(element, "id") is { } id) yield return id;
        }
    }
}
