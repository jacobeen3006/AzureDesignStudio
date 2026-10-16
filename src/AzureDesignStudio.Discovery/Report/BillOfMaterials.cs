using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Report;

/// <summary>
/// [32] Tenant bill-of-materials: the whole typeless inventory as one deterministic JSON
/// document, so customers commit tenant state to git and review it in pull requests.
/// Determinism is the product: resources sorted by ARM id, tag keys and every property
/// object key sorted ordinally, provider serialization order stripped, no clock inside -
/// the same tenant state always renders byte-identical, and the content hash binds the
/// resources section so a diff shows in one line exactly when state changed.
/// </summary>
public static class BillOfMaterials
{
    public const string Schema = "ads-bom/1";

    private static readonly JsonSerializerOptions Compact = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Canonical document of a scan's inventory; duplicate rows (a resumed scan may
    /// hold them) collapse case-insensitively, first row wins, matching the reader convention.</summary>
    public static string Render(IReadOnlyList<InventoryItem> items, string scope)
    {
        var resources = new JsonArray();
        foreach (var item in items
                 .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                 .Select(g => g.First())
                 .OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            var tags = new JsonObject();
            foreach (var (k, v) in item.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
                tags[k] = v;

            resources.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["type"] = item.Type,
                ["location"] = item.Location,
                ["resourceGroup"] = item.ResourceGroup,
                ["subscriptionId"] = item.SubscriptionId,
                ["tags"] = tags,
                ["properties"] = item.Raw is { } raw ? Canonical(raw) : null,
            });
        }

        // The hash covers the compact canonical serialization of the resources section only -
        // reformatting the document around it (indentation, the scope line) never fakes a change.
        var resourcesJson = resources.ToJsonString(Compact);
        var doc = new JsonObject
        {
            ["schema"] = Schema,
            ["scope"] = scope,
            ["resourceCount"] = items.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Count(),
            ["contentHash"] = "sha256:" + Sha256Hex(resourcesJson),
            ["resources"] = JsonNode.Parse(resourcesJson),
        };
        return doc.ToJsonString(Indented);
    }

    /// <summary>Re-serialize a raw ARM property object with keys sorted at every depth;
    /// provider response order is transport noise, not tenant state.</summary>
    private static JsonNode Canonical(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var o = new JsonObject();
            foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                o[p.Name] = Canonical(p.Value);
            return o;
        }
        if (e.ValueKind == JsonValueKind.Array)
        {
            var a = new JsonArray();
            foreach (var v in e.EnumerateArray())
                a.Add(Canonical(v));
            return a;
        }
        if (e.ValueKind == JsonValueKind.Null) return null!; // JsonObject stores it as a JSON null member
        return JsonNode.Parse(e.GetRawText()) ?? JsonValue.Create(e.ToString())!;
    }

    private static string Sha256Hex(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}
