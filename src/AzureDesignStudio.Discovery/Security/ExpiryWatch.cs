using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Security;

/// <summary>How close an expiry is, relative to the caller-supplied now.</summary>
public enum ExpiryStatus { Expired, Critical, Warning }

/// <summary>One resource whose scan properties carry a real expiry date. <c>PropertyPath</c>
/// is the evidence (e.g. <c>properties.expireDate</c>) - every row names where its date came from.</summary>
public sealed record ExpiryRow(
 string Id,
 string Name,
 string Type,
 string ResourceGroup,
 string SubscriptionId,
 DateTimeOffset ExpiresAt,
 string PropertyPath,
 ExpiryStatus Status,
 int DaysRemaining);

/// <summary>All dated resources plus the permanent blind spots, so a clean table never
/// reads as "everything checked".</summary>
public sealed record ExpiryAudit(
 IReadOnlyList<ExpiryRow> Rows,
 int ScannedResources,
 int DatedResources,
 IReadOnlyList<string> Caveats);

/// <summary>
/// Expiry watch (v2 plan 86 first cut): TLS certificates and dated ARM properties carry
/// their death date in plain view of a Reader scan - this finds every recognized date key
/// at any depth (<c>expireDate</c>, <c>expirationDate</c>, <c>expiresOn</c>, <c>expires</c>,
/// <c>notAfter</c>, <c>sslExpirationDate</c>), keeps the earliest per resource, and buckets
/// by days remaining against an injected now (deterministic, testable). Values that do not
/// parse are skipped, never guessed. Key Vault secret/key/cert expiries live on the data
/// plane Reader never touches - a permanent caveat, not an empty table.
/// </summary>
public static class ExpiryWatch
{
    public const int CriticalDays = 30;
    public const int WarningDays = 90;

    private static readonly string[] DateKeys =
    [
        "expireDate", "expirationDate", "expiresOn", "expires", "notAfter", "sslExpirationDate",
    ];

    private static readonly string[] Caveats =
    [
        "Key Vault secret/key/certificate expiries are data-plane facts a Reader ARM scan never sees",
        "only date keys the providers actually report are watched - an unlisted expiry stays invisible",
    ];

    public static ExpiryAudit Analyze(IReadOnlyList<InventoryItem> items, DateTimeOffset now)
    {
        var rows = new List<ExpiryRow>();
        foreach (var item in items)
        {
            if (item.Raw is not { } raw || raw.ValueKind != JsonValueKind.Object) continue;

            DateTimeOffset? earliest = null;
            string? path = null;
            Find(raw, "properties", ref earliest, ref path);
            // duplicate resumed-scan rows collapse to the earliest date seen for the id
            if (earliest is not { } date || path is null) continue;
            var days = (int)Math.Floor((date - now).TotalDays);
            var status = days <= 0 ? ExpiryStatus.Expired
                : days <= CriticalDays ? ExpiryStatus.Critical
                : days <= WarningDays ? ExpiryStatus.Warning
                : (ExpiryStatus?)null;
            if (status is not { } s) continue; // still dated, but not close enough to report
            rows.Add(new ExpiryRow(item.Id, item.Name, item.Type, item.ResourceGroup,
                item.SubscriptionId, date, path, s, days));
        }

        var distinct = rows
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.ExpiresAt).ThenBy(r => r.PropertyPath, StringComparer.Ordinal).First())
            .OrderBy(r => r.ExpiresAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        return new ExpiryAudit(distinct, items.Count, distinct.Count, Caveats);
    }

    /// <summary>Walks every object (and objects inside arrays) looking for recognized date
    /// keys; the earliest parseable value wins, its JSON path travels as evidence.</summary>
    private static void Find(JsonElement node, string at, ref DateTimeOffset? earliest, ref string? path)
    {
        foreach (var prop in node.EnumerateObject())
        {
            var childPath = at.Length == 0 ? prop.Name : at + "." + prop.Name;
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Find(prop.Value, childPath, ref earliest, ref path);
                    break;
                case JsonValueKind.Array:
                    foreach (var el in prop.Value.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.Object)
                            Find(el, childPath, ref earliest, ref path);
                    break;
                case JsonValueKind.String when Array.IndexOf(DateKeys, prop.Name) >= 0:
                    // exact key match, ordinal - providers spell these consistently
                    if (DateTimeOffset.TryParse(prop.Value.GetString(), out var date)
                        && (earliest is null || date < earliest))
                    {
                        earliest = date;
                        path = childPath;
                    }
                    break;
            }
        }
    }
}
