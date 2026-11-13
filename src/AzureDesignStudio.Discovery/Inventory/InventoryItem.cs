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

        // ARM reports who a resource is managed by (the VM a disk is attached to, the AKS
        // cluster behind a node resource group) as a top-level field. Every analyzer reads it
        // from Raw, so it is carried there - otherwise each disk reads as unattached.
        var managedBy = Str(row, "managedBy");
        if (managedBy.Length > 0 && (raw is null || !raw.Value.TryGetProperty("managedBy", out _)))
        {
            var merged = raw is null
                ? new System.Text.Json.Nodes.JsonObject()
                : System.Text.Json.Nodes.JsonObject.Create(raw.Value)!;
            merged["managedBy"] = managedBy;
            raw = JsonSerializer.SerializeToElement(merged);
        }

        // sku is a top-level column too (public IPs, load balancers); fold it into Raw unless
        // the resource keeps its own properties.sku (application gateways), which wins.
        if (row.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.Object
            && (raw is null || !raw.Value.TryGetProperty("sku", out _)))
        {
            var merged = raw is null
                ? new System.Text.Json.Nodes.JsonObject()
                : System.Text.Json.Nodes.JsonObject.Create(raw.Value)!;
            merged["sku"] = System.Text.Json.Nodes.JsonNode.Parse(sku.GetRawText());
            raw = JsonSerializer.SerializeToElement(merged);
        }

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
                  // Ordinal: the default comparer orders by the current culture, so the same
                  // inventory hashed differently on machines with different regional settings.
                  .Append(string.Join(';', i.Tags.OrderBy(k => k.Key, StringComparer.Ordinal))).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString())));
    }
}
