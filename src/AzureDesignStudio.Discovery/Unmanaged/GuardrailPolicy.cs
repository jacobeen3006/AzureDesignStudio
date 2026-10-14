using System.Text.Json;

namespace AzureDesignStudio.Discovery.Unmanaged;

/// <summary>
/// Do-not-touch guardrail list v1 (v2 plan 9.3): scopes, tags and types flagged everywhere -
/// a matching resource is reported as protected and is never a mutate candidate downstream.
/// Declarative JSON so the list travels with the workspace, not the build.
/// </summary>
public sealed record GuardrailPolicy(
    IReadOnlyList<string> ProtectedScopes,
    IReadOnlyList<ProtectedTag> ProtectedTags,
    IReadOnlyList<string> ProtectedTypes)
{
    public static GuardrailPolicy Empty { get; } = new([], [], []);

    /// <summary>
    /// `{"scopes":["subscriptions/s1/rgs/prod-core"],"tags":[{"key":"environment","value":"prod"}],"types":["microsoft.kusto/clusters"]}`
    /// - missing arrays stay empty, values are optional (key-only match), everything case-insensitive.
    /// </summary>
    public static GuardrailPolicy FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var scopes = ReadStrings(root, "scopes");
        var types = ReadStrings(root, "types");
        var tags = new List<ProtectedTag>();
        if (root.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
            {
                var key = t.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                if (key.Length == 0) continue;
                var value = t.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                tags.Add(new ProtectedTag(key, value));
            }

        return new GuardrailPolicy(scopes, tags, types);
    }

    /// <summary>Which guardrail protects this item, if any - the matched pattern is the audit reason.</summary>
    public string? ProtectionReason(Inventory.InventoryItem item)
    {
        foreach (var scope in ProtectedScopes)
            if (ScopeMatches(item.Id, scope))
                return "guardrail-scope:" + scope;

        foreach (var tag in ProtectedTags)
            if (item.Tags.TryGetValue(tag.Key, out var value)
                && (tag.Value is null || string.Equals(value, tag.Value, StringComparison.OrdinalIgnoreCase)))
                return "guardrail-tag:" + tag.Key + (tag.Value is null ? "" : "=" + tag.Value);

        foreach (var type in ProtectedTypes)
            if (TypeMatches(item.Type, type))
                return "guardrail-type:" + type;

        // Legacy blanket flag survives any config (InventoryItem.HasCostEscalationSignal).
        if (item.HasCostEscalationSignal) return "tag:do-not-touch";

        return null;
    }

    /// <summary>`subscriptions/s1` protects its whole subtree; match must land on an id segment boundary.</summary>
    private static bool ScopeMatches(string id, string scope)
    {
        var needle = "/" + scope.Trim().TrimStart('/').TrimEnd('/') + "/";
        return id.Replace("//", "/").StartsWith(needle, StringComparison.OrdinalIgnoreCase)
            || id.Replace("//", "/").Equals(needle[..^1], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`microsoft.compute` covers every child type; `.../virtualmachines` covers its children too.</summary>
    private static bool TypeMatches(string type, string pattern) =>
        type.Equals(pattern, StringComparison.OrdinalIgnoreCase)
        || type.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string property)
    {
        var list = new List<string>();
        if (root.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                    list.Add(s);
        return list;
    }
}

/// <summary>Tag key, optionally pinned to one value; item tag lookup itself is case-insensitive.</summary>
public sealed record ProtectedTag(string Key, string? Value = null);
