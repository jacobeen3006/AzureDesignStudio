using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static AzureDesignStudio.Discovery.Arm.JsonStr;

namespace AzureDesignStudio.Discovery.Inventory;

/// <summary>
/// One inventory row, typeless by construction (v2 plan 7.4): the ARG row's known columns
/// are projected and every unknown field stays in <see cref="Raw"/> untouched, so brand-new
/// Azure services flow through with zero code change.
/// </summary>
public sealed record InventoryItem(
    string Id,
    string Name,
    string Type,
    string Location,
    string ResourceGroup,
    string SubscriptionId,
    IReadOnlyDictionary<string, string> Tags,
    JsonElement? Raw)
{
    /// <summary>e.g. "Microsoft.Network" from "Microsoft.Network/virtualNetworks".</summary>
    public string ProviderNamespace => Type.Contains('/') ? Type[..Type.IndexOf('/')] : Type;

    public string ResourceTypeSegment => Type.Contains('/') ? Type[(Type.IndexOf('/') + 1)..] : "";

    /// <summary>Parent chain from the ARM id (…/providers/P/T/name/childP/childT/name).</summary>
    public string? ParentId
    {
        get
        {
            var i = Id.LastIndexOf("/" + Name, StringComparison.OrdinalIgnoreCase);
            return i > 0 ? Id[..i] : null;
        }
    }

    /// <summary>Owner facet (7.6): first hit among conventional tags, case-insensitive.</summary>
    public string? Owner =>
        Tags.TryGetValue("owner", out var o) ? o
        : Tags.TryGetValue("ownername", out var o2) ? o2
        : Tags.TryGetValue("cost-center-owner", out var o3) ? o3
        : null;

    public bool HasCostEscalationSignal =>
        Tags.TryGetValue("do-not-touch", out var d) && bool.TryParse(d, out var b) && b;
}

/// <summary>ARG objectArray row → <see cref="InventoryItem"/>. Unknown columns survive in Raw.</summary>
public static class InventoryNormalizer
{
    public static InventoryItem FromArgRow(JsonElement row)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var prop in t.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String) tags[prop.Name] = prop.Value.GetString() ?? "";

        JsonElement? raw = row.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : null;

        return new InventoryItem(
            Str(row, "id"), Str(row, "name"), Str(row, "type"), Str(row, "location"),
            Str(row, "resourceGroup"), Str(row, "subscriptionId"), tags, raw);
    }

    /// <summary>Stable hash over the result set: identical hash ⇒ inventory unchanged.</summary>
    public static string ContentHash(IEnumerable<InventoryItem> items)
    {
        var joined = new StringBuilder();
        foreach (var i in items.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
            joined.Append(i.Id).Append('\u001f').Append(i.Type).Append('\u001f').Append(i.Name)
                  .Append('\u001f').Append(i.Location).Append('\u001f')
                  .Append(string.Join(';', i.Tags.OrderBy(k => k.Key))).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString())));
    }
}
