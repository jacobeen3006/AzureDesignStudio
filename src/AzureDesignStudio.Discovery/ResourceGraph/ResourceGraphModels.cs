using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureDesignStudio.Discovery.ResourceGraph;

/// <summary>Tunables for <see cref="ResourceGraphClient"/>. Endpoint/scope vary per cloud (7.6).</summary>
public sealed class ResourceGraphOptions
{
    public string Endpoint { get; init; } = "https://management.azure.com";
    public string ApiVersion { get; init; } = "2024-04-01";
    public string Scope { get; init; } = "https://management.azure.com/.default";

    /// <summary>ARG hard limit per response.</summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>Concurrent in-flight queries; the quota window is ~15/5s, keep this small.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>429 retries before giving up (bounded - scans must fail, not hang).</summary>
    public int MaxThrottleRetries { get; init; } = 5;
}

/// <summary>One ARG page in objectArray format; rows are raw ARM JSON (typeless layer 7.4).</summary>
public sealed record ResourceQueryPage(
    IReadOnlyList<JsonElement> Rows,
    long TotalRecords,
    bool ResultTruncated,
    JsonNode? SkipToken)
{
    public static ResourceQueryPage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var rows = new List<JsonElement>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var row in data.EnumerateArray()) rows.Add(row.Clone());

        var total = root.TryGetProperty("totalRecords", out var t) && t.TryGetInt64(out var tv) ? tv : rows.Count;
        var truncated = root.TryGetProperty("resultTruncated", out var rt) && ReadBool(rt);
        JsonNode? skip = root.TryGetProperty("$skipToken", out var st) && st.ValueKind is JsonValueKind.Object or JsonValueKind.String
            ? JsonNode.Parse(st.GetRawText())
            : null;

        return new ResourceQueryPage(rows, total, truncated, skip);
    }

    // REST sends resultTruncated as "True"/"False" strings in some API versions; SDK-style bools in others.
    private static bool ReadBool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.TryParse(e.GetString(), out var b) && b,
        _ => false,
    };
}

/// <summary>Full result of a paged query. <see cref="Truncated"/> = cap hit with no page token (7.5 manifest).</summary>
public sealed record ResourceQueryResult(
    IReadOnlyList<JsonElement> Rows,
    long TotalRecords,
    bool Truncated);

/// <summary>ARG/ARM failure with the service error code kept for preflight triage (7.2).</summary>
public sealed class ResourceGraphException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }
    public bool Throttled { get; }

    public ResourceGraphException(string code, string message, int statusCode = 0, bool throttled = false)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Throttled = throttled;
    }

    internal static ResourceGraphException FromErrorBody(int statusCode, string body)
    {
        string code = "Unknown", message = string.IsNullOrWhiteSpace(body) ? $"HTTP {statusCode} with empty error body" : body;
        try
        {
            if (JsonNode.Parse(body)?["error"] is JsonObject error)
            {
                code = error["code"]?.GetValue<string>() ?? code;
                message = error["message"]?.GetValue<string>() ?? message;
            }
        }
        catch (JsonException) { /* keep raw body as message */ }
        return new ResourceGraphException(code, message, statusCode);
    }
}
