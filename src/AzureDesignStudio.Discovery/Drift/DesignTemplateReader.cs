using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;
using static AzureDesignStudio.Discovery.Arm.JsonStr;

namespace AzureDesignStudio.Discovery.Drift;

/// <summary>
/// ARM deployment-template resources → <see cref="InventoryItem"/> rows (v2 plan 9.2),
/// so a design and a live scan compare through the exact same typeless shape. The
/// difference the differ then reports is what deploying the design would change.
/// </summary>
public static class DesignTemplateReader
{
    /// <summary>Accepts the full template document or a bare resource array.</summary>
    public static IReadOnlyList<InventoryItem> Read(string templateJson)
    {
        using var doc = JsonDocument.Parse(templateJson);
        var root = doc.RootElement;
        var resources = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("resources", out var r) && r.ValueKind == JsonValueKind.Array ? r : default;
        if (resources.ValueKind != JsonValueKind.Array) return [];

        var items = new List<InventoryItem>();
        foreach (var res in resources.EnumerateArray())
        {
            if (res.ValueKind != JsonValueKind.Object) continue;
            var id = Str(res, "id");
            var name = Str(res, "name");
            // ARM ids are case-insensitive; normalize both sides so the differ's key matches live ids.
            if (id.Length == 0 && name.Length > 0) id = name;
            var type = Str(res, "type");
            var rg = "";
            var sub = "";
            var parts = id.Split('/');
            for (var i = 1; i + 1 < parts.Length; i += 2)
            {
                if (parts[i].Equals("resourcegroups", StringComparison.OrdinalIgnoreCase)) rg = parts[i + 1];
                else if (parts[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)) sub = parts[i + 1];
            }

            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (res.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var prop in t.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String) tags[prop.Name] = prop.Value.GetString() ?? "";

            JsonElement? raw = res.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : null;

            items.Add(new InventoryItem(
                id.ToLowerInvariant(), name, type.ToLowerInvariant(), Str(res, "location").ToLowerInvariant(),
                rg, sub, tags, raw));
        }

        return items;
    }
}
