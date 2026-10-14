using System.Text.Json;

namespace AzureDesignStudio.Discovery.Iac.WhatIf;

/// <summary>Typed failure for what-if traffic: malformed payloads and ARM errors never masquerade as "no changes".</summary>
public sealed class WhatIfException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum WhatIfChangeKind
{
    Create,
    Modify,
    Delete,
    NoChange,
    Ignore,
    Bad,
    Unknown,
}

/// <summary>One predicted change. Payloads are the ARM what-if before/after documents when present.</summary>
public sealed record WhatIfChange(
    WhatIfChangeKind Kind,
    string? ResourceId,
    string? BeforeJson,
    string? AfterJson,
    int DeltaCount);

/// <summary>Parsed <c>WhatIfOperationResult</c>. Counts always include every change row, even unrecognized ones.</summary>
public sealed class WhatIfSummary
{
    public required string Status { get; init; }
    public required IReadOnlyList<WhatIfChange> Changes { get; init; }

    public int Creates => Count(WhatIfChangeKind.Create);
    public int Modifies => Count(WhatIfChangeKind.Modify);
    public int Deletes => Count(WhatIfChangeKind.Delete);

    public bool HasDelete => Deletes > 0;

    public IReadOnlyList<string> DeleteResourceIds =>
        Changes.Where(c => c.Kind == WhatIfChangeKind.Delete && c.ResourceId is not null)
            .Select(c => c.ResourceId!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Delete-safety predicate: a report is only deployable when ARM fully succeeded and every
    /// row was classified. Unknown status or a Bad/unrecognized row means the blast radius is
    /// unknown, which must never be approvable. null means determinate; otherwise the reason.
    /// </summary>
    public string? IndeterminacyReason =>
        !string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase)
            ? $"what-if report status is '{Status}', not 'Succeeded'"
            : Changes.FirstOrDefault(c => c.Kind is WhatIfChangeKind.Bad or WhatIfChangeKind.Unknown) is { } unclassified
                ? $"what-if could not classify the predicted change for {unclassified.ResourceId ?? "(unknown resource)"}"
                : null;

    /// <summary>Honesty contract: what-if is a prediction with known blind spots; every consumer shows these.</summary>
    public static IReadOnlyList<string> Caveats { get; } =
    [
        "reference() values can be wrong at what-if time; final values are only known at deployment.",
        "Dynamic resources are always predicted as Create with generated names.",
        "The result is a point-in-time prediction; changes made after it are not reflected in the deployment.",
    ];

    private int Count(WhatIfChangeKind kind) => Changes.Count(c => c.Kind == kind);

    /// <summary>Per-payload character cap for UI transport; truncation is flagged in the row.</summary>
    public const int PayloadCap = 1500;

    /// <summary>
    /// Deterministic review rows (changeType/resourceId/delta count plus truncated before/after
    /// payloads - ARM's default FullResourcePayloads detail), capped by <paramref name="maxRows"/>;
    /// row and payload truncation are the caller's to surface.
    /// </summary>
    public string ToChangesJson(int maxRows = 200)
    {
        var rows = Changes
            .OrderBy(c => c.ResourceId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(c => c.Kind)
            .Take(maxRows)
            .Select(c => new
            {
                changeType = c.Kind.ToString(),
                resourceId = c.ResourceId,
                delta = c.DeltaCount,
                before = Truncate(c.BeforeJson),
                after = Truncate(c.AfterJson),
                beforeTruncated = IsTruncated(c.BeforeJson),
                afterTruncated = IsTruncated(c.AfterJson),
            });
        return JsonSerializer.Serialize(rows);
    }

    private static string? Truncate(string? payload) =>
        payload is null || payload.Length <= PayloadCap ? payload : payload[..PayloadCap];

    private static bool IsTruncated(string? payload) => payload is not null && payload.Length > PayloadCap;
}

/// <summary>Strict parser over the ARM <c>WhatIfOperationResult</c> document (2025-04-01 shape).</summary>
public static class WhatIfParser
{
    public static WhatIfSummary Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new WhatIfException("MalformedWhatIf", $"What-if response is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new WhatIfException("MalformedWhatIf", "What-if response is not a JSON object.");

            var status = ReadString(doc.RootElement, "status")
                ?? (doc.RootElement.TryGetProperty("properties", out var rootProps) && rootProps.ValueKind == JsonValueKind.Object
                    ? ReadString(rootProps, "status")
                    : null)
                ?? "Unknown";

            var changes = new List<WhatIfChange>();
            if (doc.RootElement.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                && props.TryGetProperty("changes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var kind = MapKind(ReadString(item, "changeType"));
                    string? before = null, after = null;
                    if (item.TryGetProperty("before", out var b) && b.ValueKind == JsonValueKind.Object)
                        before = b.GetRawText();
                    if (item.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Object)
                        after = a.GetRawText();
                    var delta = item.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;

                    changes.Add(new WhatIfChange(kind, ReadString(item, "resourceId"), before, after, delta));
                }
            }

            return new WhatIfSummary { Status = status, Changes = changes };
        }
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static WhatIfChangeKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "create" => WhatIfChangeKind.Create,
        "modify" => WhatIfChangeKind.Modify,
        "delete" => WhatIfChangeKind.Delete,
        "nochange" => WhatIfChangeKind.NoChange,
        "ignore" => WhatIfChangeKind.Ignore,
        "bad" => WhatIfChangeKind.Bad,
        _ => WhatIfChangeKind.Unknown,
    };
}
