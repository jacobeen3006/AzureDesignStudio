using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Core.DTO;

namespace AzureDesignStudio.Core.Common;

/// <summary>What happened to one entity or link between two design documents.</summary>
public enum DesignChangeKind { Added, Removed, Retyped, LinkAdded, LinkRemoved, Changed }

/// <summary>One payload-leaf difference; values are raw JSON text, verbatim from each document.</summary>
public sealed record DesignPropChange(string Path, string? OldJson, string? NewJson);

/// <summary>
/// One difference row. Entities carry <see cref="Key"/> = stable identity (see <see cref="DesignDiffEngine.EntityKey"/>);
/// links carry the composed "sourceKey-&gt;targetKey" plus the endpoints separately.
/// </summary>
public sealed record DesignChange(
    DesignChangeKind Kind,
    string SubjectKind,
    string Key,
    string? Name,
    string? SourceKey,
    string? TargetKey,
    IReadOnlyList<DesignPropChange> Props,
    bool PropsTruncated);

/// <summary>The full document-vs-document answer: change rows, counted diagnostics, entity totals.</summary>
public sealed record DesignDiff(
    IReadOnlyList<DesignChange> Changes,
    IReadOnlyList<string> Diagnostics,
    int EntityCountBefore,
    int EntityCountAfter)
{
    public bool AnyChanges => Changes.Count > 0;
}

/// <summary>
/// Pure, deterministic design-document diff (v2 plan 9.2 side-by-side diagram diff). Identity is the durable
/// ARM id when present (survives adoption and live-plane refresh), else the saved entity id. Payload comparison
/// is leaf-level over the raw JSON (canonical lowercase paths), so unknown payload types diff without code change.
/// The inputs are never mutated; document order never affects the answer.
/// </summary>
public static class DesignDiffEngine
{
    /// <summary>Per-entity property-diff cap (same convention as ScanDiffer); beyond it the row says so.</summary>
    public const int MaxPropertyDiffs = 200;

    /// <summary>Payload members handled by identity/structure, never reported as property changes.</summary>
    private static readonly HashSet<string> IdentityMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "typeKey", "armId",
    };

    /// <summary>
    /// Stable identity: non-empty (trimmed) <paramref name="armId"/> wins as "arm:" + lower-cased id;
    /// else non-empty <paramref name="id"/> as "id:" + id; else null (the entity cannot be identified or matched).
    /// </summary>
    public static string? EntityKey(string? armId, string? id)
    {
        var arm = armId?.Trim();
        if (!string.IsNullOrEmpty(arm)) return "arm:" + arm.ToLowerInvariant();
        if (!string.IsNullOrEmpty(id)) return "id:" + id;
        return null;
    }

    /// <summary>Diff two parsed graphs. Malformed input shapes are counted diagnostics, never exceptions.</summary>
    public static DesignDiff Diff(DiagramGraph before, DiagramGraph after)
    {
        var left = ReadSide(before, "before");
        var right = ReadSide(after, "after");

        var changes = new List<DesignChange>();
        var diagnostics = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in left.Diagnostics) diagnostics.Add(d);
        foreach (var d in right.Diagnostics) diagnostics.Add(d);

        foreach (var (key, entity) in right.ByIdKey)
            if (!left.ByIdKey.ContainsKey(key))
                changes.Add(new DesignChange(DesignChangeKind.Added, entity.SubjectKind, key, entity.Name, null, null, Array.Empty<DesignPropChange>(), false));

        foreach (var (key, entity) in left.ByIdKey)
            if (!right.ByIdKey.ContainsKey(key))
                changes.Add(new DesignChange(DesignChangeKind.Removed, entity.SubjectKind, key, entity.Name, null, null, Array.Empty<DesignPropChange>(), false));

        foreach (var (key, oldEntity) in left.ByIdKey)
        {
            if (!right.ByIdKey.TryGetValue(key, out var newEntity)) continue;

            var oldType = Member(oldEntity.Payload, "typeKey");
            var newType = Member(newEntity.Payload, "typeKey");
            if (!string.IsNullOrEmpty(oldType) && !string.IsNullOrEmpty(newType)
                && !string.Equals(oldType, newType, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new DesignChange(DesignChangeKind.Retyped, newEntity.SubjectKind, key, newEntity.Name, null, null, Array.Empty<DesignPropChange>(), false));
                continue;
            }

            var (props, truncated) = DiffPayloads(oldEntity.Payload, newEntity.Payload);
            if (props.Count > 0)
                changes.Add(new DesignChange(DesignChangeKind.Changed, newEntity.SubjectKind, key, newEntity.Name, null, null, props, truncated));
        }

        foreach (var key in right.LinkKeys.Except(left.LinkKeys, StringComparer.Ordinal))
        {
            var (source, target) = SplitLinkKey(key);
            changes.Add(new DesignChange(DesignChangeKind.LinkAdded, "link", key, null, source, target, Array.Empty<DesignPropChange>(), false));
        }
        foreach (var key in left.LinkKeys.Except(right.LinkKeys, StringComparer.Ordinal))
        {
            var (source, target) = SplitLinkKey(key);
            changes.Add(new DesignChange(DesignChangeKind.LinkRemoved, "link", key, null, source, target, Array.Empty<DesignPropChange>(), false));
        }

        changes = changes
            .OrderBy(c => (int)c.Kind)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        return new DesignDiff(changes, diagnostics.ToList(), left.EntryCount, right.EntryCount);
    }

    /// <summary>
    /// Diff two saved-design JSON documents (the format the canvas loader consumes, System.Text.Json).
    /// Unparseable text throws <see cref="AdsDesignFormatException"/> with the offending side named.
    /// </summary>
    public static DesignDiff DiffGraphJson(string beforeJson, string afterJson)
    {
        var before = ParseSide(beforeJson, "before");
        var after = ParseSide(afterJson, "after");
        return Diff(before, after);
    }

    private static DiagramGraph ParseSide(string text, string side)
    {
        try
        {
            return JsonSerializer.Deserialize<DiagramGraph>(text)
                ?? throw new AdsDesignFormatException($"The {side} design is empty.");
        }
        catch (JsonException ex)
        {
            throw new AdsDesignFormatException($"The {side} design is not valid JSON: {ex.Message}", ex);
        }
    }

    private sealed record SideEntity(string Key, string SubjectKind, string? Name, JsonObject? Payload);

    private sealed class Side
    {
        public Dictionary<string, SideEntity> ByIdKey { get; } = new(StringComparer.Ordinal);
        public List<SideEntity> Entities { get; } = new();
        public int EntryCount;
        public HashSet<string> LinkKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> IdToKey { get; } = new(StringComparer.Ordinal);
        public List<string> Diagnostics { get; } = new();
    }

    private static Side ReadSide(DiagramGraph graph, string side)
    {
        var result = new Side();
        int unidentified = 0, malformed = 0, duplicates = 0;
        string? firstDuplicate = null;

        void Scan(IList<object> items, string subjectKind)
        {
            foreach (var item in items)
            {
                result.EntryCount++;
                var payload = (item as JsonObject)?.DeepClone()?.AsObject() ?? ToObject(item);
                if (payload == null)
                {
                    malformed++;
                    continue;
                }

                var key = EntityKey(Member(payload, "armId"), Member(payload, "id"));
                if (key == null)
                {
                    unidentified++;
                    continue;
                }

                var entity = new SideEntity(key, subjectKind, Member(payload, "name"), payload);
                result.Entities.Add(entity);
                if (result.ByIdKey.TryAdd(key, entity))
                {
                    var rawId = Member(payload, "id");
                    if (!string.IsNullOrEmpty(rawId)) result.IdToKey.TryAdd(rawId, key);
                }
                else
                {
                    duplicates++;
                    firstDuplicate ??= key;
                }
            }
        }

        Scan(graph.Groups, "group");
        Scan(graph.Nodes, "node");

        if (unidentified > 0) result.Diagnostics.Add($"Unidentified entities in {side}: {unidentified}");
        if (malformed > 0) result.Diagnostics.Add($"Malformed entities in {side}: {malformed}");
        if (duplicates > 0) result.Diagnostics.Add($"Duplicate entity keys in {side}: {duplicates} (first: {firstDuplicate})");

        foreach (var link in graph.Links)
        {
            var source = ResolveEndpoint(link.SourcePortParentId, result, side);
            var target = ResolveEndpoint(link.TargetPortParentId, result, side);
            result.LinkKeys.Add(source + "->" + target);
        }

        return result;
    }

    private static string ResolveEndpoint(string? rawParentId, Side side, string graphSide)
    {
        var raw = rawParentId ?? string.Empty;
        if (side.IdToKey.TryGetValue(raw, out var key)) return key;
        side.Diagnostics.Add($"Unresolved link endpoint '{raw}' in {graphSide}");
        return "id:" + raw;
    }

    private static (string Source, string Target) SplitLinkKey(string key)
    {
        var at = key.IndexOf("->", StringComparison.Ordinal);
        return at < 0 ? (key, string.Empty) : (key[..at], key[(at + 2)..]);
    }

    private static JsonObject? ToObject(object? entity)
    {
        if (entity is JsonElement element && element.ValueKind != JsonValueKind.Object) return null;
        return JsonSerializer.SerializeToNode(entity)?.AsObject();
    }

    /// <summary>Case-insensitive string member of a payload object; null when absent or not a string.</summary>
    private static string? Member(JsonObject? payload, string name)
    {
        foreach (var kvp in payload ?? new JsonObject())
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
                return kvp.Value?.GetValueKind() == JsonValueKind.String ? kvp.Value.GetValue<string>() : null;
        return null;
    }

    private static (List<DesignPropChange> Props, bool Truncated) DiffPayloads(JsonObject? before, JsonObject? after)
    {
        var oldLeaves = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var newLeaves = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Flatten(string.Empty, before, oldLeaves);
        Flatten(string.Empty, after, newLeaves);

        var allPaths = oldLeaves.Keys.Concat(newLeaves.Keys).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal);
        var props = new List<DesignPropChange>();
        bool truncated = false;
        foreach (var path in allPaths)
        {
            oldLeaves.TryGetValue(path, out var oldJson);
            newLeaves.TryGetValue(path, out var newJson);
            if (oldJson == newJson) continue;
            if (props.Count >= MaxPropertyDiffs) { truncated = true; break; }
            props.Add(new DesignPropChange(path, oldJson, newJson));
        }
        return (props, truncated);
    }

    /// <summary>Leaf enumeration with canonical lowercase dotted paths (arrays indexed); identity members excluded at the root.</summary>
    private static void Flatten(string path, JsonNode? node, SortedDictionary<string, string> leaves)
    {
        switch (node)
        {
            case null:
                leaves[path] = "null";
                return;
            case JsonObject o:
                foreach (var kvp in o)
                {
                    var child = string.Equals(path, string.Empty, StringComparison.Ordinal)
                        ? kvp.Key.ToLowerInvariant()
                        : path + "." + kvp.Key.ToLowerInvariant();
                    if (path.Length == 0 && IdentityMembers.Contains(kvp.Key)) continue;
                    Flatten(child, kvp.Value, leaves);
                }
                return;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) Flatten(path + "[" + i + "]", a[i], leaves);
                return;
            default:
                leaves[path] = node.ToJsonString();
                return;
        }
    }
}
