using System.Text.Json;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Discovery.ChangeFeed;

/// <summary>
/// Change feed collector (v2 plan 9.1): change history with caller attribution over the
/// ARG <c>resourcechanges</c> table. One source - Change Actor already carries who
/// (changedBy), which client (clientType) and which RBAC operation initiated the change,
/// and the existing Reader role covers the table; the Activity Log stays the optional
/// enrichment path (probed in preflight). Rows are ordered newest-first so a UI can page
/// the feed without re-sorting.
/// </summary>
public sealed class ChangeFeedCollector(ResourceGraphClient graph)
{
    public async Task<IReadOnlyList<ChangeFeedEntry>> CollectAsync(
        IReadOnlyList<string> subscriptionIds,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        var result = await graph.QueryAllAsync(BuildQuery(since), subscriptionIds, ct).ConfigureAwait(false);
        var entries = new List<ChangeFeedEntry>(result.Rows.Count);
        foreach (var row in result.Rows)
            if (ParseRow(row) is { } entry)
                entries.Add(entry);
        return entries;
    }

    public static string BuildQuery(DateTimeOffset since) =>
        "resourcechanges"
        + " | extend changeTime = todatetime(properties.changeAttributes.timestamp),"
        + " targetResourceId = tostring(properties.targetResourceId),"
        + " targetResourceType = tostring(properties.targetResourceType),"
        + " changeType = tostring(properties.changeType),"
        + " correlationId = tostring(properties.changeAttributes.correlationId),"
        + " changedBy = tostring(properties.changeAttributes.changedBy),"
        + " changedByType = tostring(properties.changeAttributes.changedByType),"
        + " clientType = tostring(properties.changeAttributes.clientType),"
        + " operation = tostring(properties.changeAttributes.operation),"
        + " changesCount = toint(properties.changeAttributes.changesCount),"
        + " changedProperties = properties.changes"
        + $" | where changeTime > datetime({since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ})"
        + " | order by changeTime desc"
        + " | project id, changeTime, targetResourceId, targetResourceType, changeType,"
        + " correlationId, changedBy, changedByType, clientType, operation, changesCount, changedProperties";

    /// <summary>
    /// One objectArray row (flat columns from <see cref="BuildQuery"/>) to an entry.
    /// Rows without a usable id/timestamp/target are dropped: change records land in ARG
    /// within ~5 min and partially-written ones can lack fields.
    /// </summary>
    public static ChangeFeedEntry? ParseRow(JsonElement row)
    {
        var key = Str(row, "id");
        var target = Str(row, "targetResourceId");
        if (key is null || target is null) return null;
        if (!TryTime(row, "changeTime", out var changeTime)) return null;

        return new ChangeFeedEntry(
            key,
            changeTime,
            target,
            Str(row, "targetResourceType"),
            Str(row, "changeType") ?? "Update",
            NullPlaceholder(Str(row, "correlationId")),
            NullPlaceholder(Str(row, "changedBy")),
            NullPlaceholder(Str(row, "changedByType")),
            NullPlaceholder(Str(row, "clientType")),
            NullPlaceholder(Str(row, "operation")),
            row.TryGetProperty("changesCount", out var cc) && cc.TryGetInt32(out var ccv) ? ccv : 0,
            row.TryGetProperty("changedProperties", out var cp) && cp.ValueKind == JsonValueKind.Object ? cp.Clone() : null);
    }

    // "Unspecified" = no identity/client was available; "Unknown" = client not recognized.
    // Both mean "no useful attribution" - normalize to null.
    private static string? NullPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value is "Unspecified" or "Unknown" ? null : value;

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryTime(JsonElement row, string name, out DateTimeOffset value)
    {
        value = default;
        return row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), out value);
    }
}
