using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.App;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Common;

/// <summary>One live resource offered for adoption (the typeless scan shape, ARM id casing kept).</summary>
/// <param name="Id">ARM resource id, e.g. <c>/subscriptions/…/storageAccounts/x</c>.</param>
/// <param name="Type">ARM type, e.g. <c>Microsoft.Storage/storageAccounts</c>.</param>
/// <param name="Name">Live resource name.</param>
/// <param name="Location">ARM location; nullable for global/namespace-scoped types.</param>
public sealed record AdoptableResource(string Id, string Type, string Name, string? Location = null);

/// <summary>What adoption decided for one resource; <see cref="Mapped"/> false = canvas keeps it as an unknown placeholder.</summary>
public sealed record AdoptedItem(string Id, string TypeKey, bool Mapped);

/// <summary>Adoption output: a canvas-loadable graph plus per-resource decisions.</summary>
public sealed record AdoptionResult(DiagramGraph Graph, IReadOnlyList<AdoptedItem> Items);

/// <summary>
/// Adopt (v2 plan 12.2, read-only half): live inventory → editable <c>.ads.json</c> graph.
/// Nothing is executed and nothing is invented: every node carries only what ARM states
/// (id, name, location) and a stencil key resolved at runtime from the very models the
/// canvas uses - the <see cref="DataModelFactory"/> golden path (create fresh model →
/// export ARM → read its type) so this map can never drift from the catalog. Types this
/// build cannot model arrive as unknown nodes (openable, export nothing, never a crash)
/// and are listed so callers can say so honestly. ARM-nested children (subnets in vnets,
/// databases on servers) land inside their parent's frame by id prefix; port-to-port
/// links are a designer decision, so adoption never fabricates edges.
/// Deterministic: same inventory in any input order → byte-identical canonical text.
/// </summary>
public static class AdoptEngine
{
    internal const int Columns = 4;
    internal const double CellWidth = 260;
    internal const double CellHeight = 220;
    internal const double ChildInset = 24;
    internal const double ChildStepX = 150;
    internal const double ChildStepY = 120;
    internal const int ChildColumns = 2;

    private sealed record Item(AdoptableResource Source, string TypeKey, bool Mapped)
    {
        public string Id => Source.Id;
        public bool IsGroup => Mapped && GroupKeysLazy.Value.Contains(TypeKey);
    }

    private static readonly Lazy<Dictionary<string, string>> KeyByTypeLazy = new(BuildKeyByType, isThreadSafe: true);
    private static readonly Lazy<HashSet<string>> GroupKeysLazy = new(BuildGroupKeys, isThreadSafe: true);

    /// <summary>ARM type → stencil key, case-insensitive, derived once from the node models' own ARM output.</summary>
    public static IReadOnlyDictionary<string, string> StencilKeyByArmType => KeyByTypeLazy.Value;

    /// <summary>Stencil keys whose canvas model renders as a group frame that can host children.</summary>
    public static IReadOnlyCollection<string> GroupStencilKeys => GroupKeysLazy.Value;

    public static bool IsGroupStencil(string stencilKey) => GroupKeysLazy.Value.Contains(stencilKey);

    /// <summary>
    /// Adopt a scope's inventory into a design graph. <paramref name="iconPathByStencilKey"/>
    /// lets the host resolve catalog icons (seam pattern: hosts own the catalog file);
    /// the fallback is the catalog's own <c>assets/azure/{key}.svg</c> convention.
    /// </summary>
    public static AdoptionResult Adopt(IEnumerable<AdoptableResource> resources, Func<string, string?>? iconPathByStencilKey = null)
    {
        var items = (resources ?? throw new ArgumentNullException(nameof(resources)))
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Type))
            .Select(r =>
            {
                var type = r.Type.Trim();
                var mapped = KeyByTypeLazy.Value.TryGetValue(type, out var key);
                return new Item(r with { Id = r.Id.TrimStart('/') }, mapped ? key! : type, mapped);
            })
            .Where(r => r.Id.Length > 0)
            // Determinism before anything positional: id-ordinal order fixes every grid slot.
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        var byId = items.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var parents = items.ToDictionary(r => r, r => FindParentId(r.Id, byId), ReferenceComparer<Item>.Instance);

        var graph = new DiagramGraph();
        var adopted = new List<AdoptedItem>(items.Count);
        var placed = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        var slots = 0;

        foreach (var item in items.Where(i => parents[i] is null))
        {
            var position = new Point(slots % Columns * CellWidth, slots / Columns * CellHeight);
            slots++;
            placed[item.Id] = position;
            Add(graph, item, groupId: null, position, iconPathByStencilKey);
            adopted.Add(new AdoptedItem(item.Id, item.TypeKey, item.Mapped));
        }

        // ARM-nested children sit inside their parent's frame, ordinal order within the parent.
        foreach (var family in items.Where(i => parents[i] is not null)
                     .GroupBy(i => parents[i]!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var parentPos = placed.TryGetValue(family.Key, out var p) ? p : Point.Zero;
            var i = 0;
            foreach (var child in family.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var position = new Point(
                    parentPos.X + ChildInset + i % ChildColumns * ChildStepX,
                    parentPos.Y + ChildInset + i / ChildColumns * ChildStepY);
                placed[child.Id] = position;
                Add(graph, child, groupId: family.Key, position, iconPathByStencilKey);
                adopted.Add(new AdoptedItem(child.Id, child.TypeKey, child.Mapped));
                i++;
            }
        }

        return new AdoptionResult(graph, adopted);
    }

    private static void Add(DiagramGraph graph, Item item, string? groupId, Point position, Func<string, string?>? iconPathByStencilKey)
    {
        var dto = new AzureNodeDto
        {
            Id = item.Id,
            ArmId = item.Id,
            TypeKey = item.TypeKey,
            Name = item.Source.Name,
            Location = item.Source.Location ?? string.Empty,
            ImagePath = ResolveIcon(item, iconPathByStencilKey),
            GroupId = groupId ?? string.Empty,
            Position = position,
            Size = item.IsGroup ? new Size(141, 141) : new Size(81, 81),
        };

        // Group frames live in the groups section (top-level or child - the loader's
        // parent/child group split is the empty GroupId); ordinary nodes always in nodes.
        if (item.IsGroup)
            graph.Groups.Add(dto);
        else
            graph.Nodes.Add(dto);
    }

    private static string ResolveIcon(Item item, Func<string, string?>? iconPathByStencilKey)
    {
        if (!item.Mapped) return string.Empty; // unknown node: canvas fallback icon, never a broken image path
        return iconPathByStencilKey?.Invoke(item.TypeKey) ?? $"assets/azure/{item.TypeKey}.svg";
    }

    /// <summary>Longest in-set ARM-id ancestor whose stencil renders as a group; null = top-level.</summary>
    private static string? FindParentId(string id, Dictionary<string, Item> byId)
    {
        string? best = null;
        for (var slash = id.LastIndexOf('/'); slash > 0; slash = id.LastIndexOf('/', slash - 1))
        {
            var candidate = id[..slash];
            if (candidate.Length == 0) break;
            if (byId.TryGetValue(candidate, out var entry) && entry.IsGroup
                && (best is null || candidate.Length > best.Length))
                best = candidate;
        }
        return best;
    }

    private static Dictionary<string, string> BuildKeyByType()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // One contested type decided up front: typeless ARM cannot tell a web app from a
        // function app (both are Microsoft.Web/sites); web-app is the general container.
        if (KeysWithClaims().Contains(AdsConstants.WebApp))
            map["Microsoft.Web/sites"] = AdsConstants.WebApp;
        foreach (var info in EnumerateStencils())
            foreach (var type in info.ArmTypes)
                map.TryAdd(type, info.Key); // otherwise first key in ordinal order wins
        return map;
    }

    private static HashSet<string> KeysWithClaims() =>
        EnumerateStencils().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> BuildGroupKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in EnumerateStencils())
            if (info.IsGroup) keys.Add(info.Key);
        return keys;
    }

    private sealed record StencilInfo(string Key, IReadOnlySet<string> ArmTypes, bool IsGroup);

    // Derive, per stencil key: the ARM types it exports and whether it renders as a group.
    // Same calls the canvas itself makes on drop (the golden suite's path); a model that
    // misbehaves skips its key, never the adoption.
    private static IEnumerable<StencilInfo> EnumerateStencils()
    {
        var keys = typeof(AdsConstants)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(k => k != AdsConstants.ResourceGroup) // frames are canvas-only scaffolding, never live ARM types
            .OrderBy(k => k, StringComparer.Ordinal);

        foreach (var key in keys)
        {
            List<string> types = [];
            bool isGroup;
            try
            {
                var model = CreateProbe(key);
                isGroup = model is AzureGroupBase;
                if (model is IAzureResource resource)
                {
                    // GetArmResources()[0] is the model's own resource (AzureNodeBase convention;
                    // extras are dependency add-ons like a function app's diagnostic storage).
                    var own = resource.GetArmResources().FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(own?.Type)) types.Add(own!.Type);
                }
            }
            catch
            {
                continue; // keys needing diagram wiring (gateway/bastion/firewall) stay honest unknowns
            }

            if (types.Count > 0)
                yield return new StencilInfo(key, types.ToHashSet(StringComparer.OrdinalIgnoreCase), isGroup);
        }
    }

    // A fresh exportable instance of one stencil key. Child-scoped models export only
    // inside their ARM parent (subnet in a vnet, database on a server, app in a plan) -
    // wrapped here with the same throwaway parents the golden export suite uses, minus
    // its asserts and the diagram-linked cases, which stay skipped.
    private static NodeModel CreateProbe(string key)
    {
        var name = key == AdsConstants.DnsZone ? "ads-probe.com" : NamingEngine.Suggest(key, "adopt", "prod"); // zone names must be domains
        var model = DataModelFactory.CreateNodeModelFromKey(key, name, "assets/azure/adopt-probe.svg");
        switch (model)
        {
            case SubnetModel subnet:
                subnet.AddressPrefix = "10.0.0.0/24";
                new VirtualNetworkModel { Name = "ads-probe" }.AddSubnet(subnet);
                break;
            case SqlDatabaseModel:
                new SqlServerModel { Name = "ads-probe" }.AddChild((AzureNodeBase)model);
                break;
            case ContainerAppModel or ContainerAppsJobModel:
                new ContainerAppsEnvironmentModel { Name = "ads-probe" }.AddChild((AzureNodeBase)model);
                break;
            case APIMModel apim:
                apim.PublisherName = "ads-probe";
                apim.PublisherEmail = "probe@contoso.com";
                break;
            case WebAppModel or FunctionAppModel:
                new AppServicePlanModel { Name = "ads-probe", Kind = "linux" }.AddChild((AzureNodeBase)model);
                if (model is FunctionAppModel func)
                    func.HostingPlan = "consumption";
                model.GetType().GetProperty("RuntimeStack")?.SetValue(model, "dotnet|v6.0");
                break;
        }
        return model;
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
