using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Discovery.Iac;

namespace AzureDesignStudio.Discovery.Arm;

public enum ResourceApplyState
{
    Accepted,
    Running,
    Succeeded,
    Failed,
    Canceled,
    /// <summary>ARM reported a display status this build does not recognize; never guessed.</summary>
    Unknown,
}

/// <summary>Latest known state of one resource within a deployment, folded from its operations.</summary>
public sealed record ResourceOperation(string ResourceKey, ResourceApplyState State, string? StatusCode, DateTimeOffset? TimestampUtc);

/// <summary>
/// Per-resource apply state from ARM's deployment-operations payload (v2 plan 11.2/[52][56]
/// monitor): each operation maps to a state, and per resource the newest timestamp wins -
/// a resource that started, failed and was retried reports the retry's state, never the old
/// failure. Unrecognized states stay Unknown rather than being coerced to failed.
/// Malformed payloads produce a diagnostic, never an exception.
///
/// ARM nests everything about an operation under <c>properties</c>
/// (<c>targetResource</c>, <c>provisioningState</c>, <c>statusCode</c>, <c>timestamp</c>).
/// The flat <c>targetResource</c>/<c>displayStatus.display</c>/<c>timestamp</c> shape this
/// parser first read is still accepted, so callers that normalise entries keep working.
/// </summary>
public static class DeploymentOperationsParser
{
    /// <summary>Folds an ARM deployments/{name}/operations JSON payload; ordinal-stable output.</summary>
    public static (IReadOnlyList<ResourceOperation> Operations, IReadOnlyList<ArmDiagnostic> Diagnostics) Parse(string payloadJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return ([], [new(ArmDiagnosticSeverity.Error, "InvalidOperationsPayload", "Operations payload is not valid JSON.")]);
        }

        return ParsePayload(root);
    }

    /// <summary>Folds already-paged operation entries (e.g. from ArmTransport.GetListAsync) with the
    /// same newest-wins rules as <see cref="Parse"/>; a malformed entry is reported, never dropped
    /// silently.</summary>
    public static (IReadOnlyList<ResourceOperation> Operations, IReadOnlyList<ArmDiagnostic> Diagnostics) ParsePages(IEnumerable<string> operationJsons)
    {
        var value = new JsonArray();
        var diagnostics = new List<ArmDiagnostic>();
        foreach (var json in operationJsons)
        {
            try
            {
                value.Add(JsonNode.Parse(json));
            }
            catch (JsonException)
            {
                diagnostics.Add(new(ArmDiagnosticSeverity.Error, "InvalidOperationsPayload", "An operations entry is not valid JSON."));
            }
        }

        var (operations, foldDiagnostics) = ParsePayload(new JsonObject { ["value"] = value });
        diagnostics.AddRange(foldDiagnostics);
        return (operations, diagnostics);
    }

    private static (IReadOnlyList<ResourceOperation> Operations, IReadOnlyList<ArmDiagnostic> Diagnostics) ParsePayload(JsonNode? root)
    {
        if (root?["value"] is not JsonArray value)
        {
            return ([], [new(ArmDiagnosticSeverity.Error, "InvalidOperationsPayload", "Operations payload has no value array.")]);
        }

        var latest = new Dictionary<string, ResourceOperation>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ArmDiagnostic>();
        foreach (var node in value)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var properties = entry["properties"] as JsonObject;
            if ((properties?["targetResource"] ?? entry["targetResource"]) is not JsonObject target)
            {
                continue; // deployment-level operations carry no resource to track
            }

            var type = Str(target, "resourceType");
            var name = Str(target, "resourceName");
            if (type.Length == 0 || name.Length == 0)
            {
                continue;
            }

            var key = $"{type}/{name}".ToLowerInvariant();

            var display = Str(properties, "provisioningState");
            if (display.Length == 0)
            {
                display = Str(entry["displayStatus"], "display");
            }

            var state = Map(display);
            if (display.Length > 0 && state == ResourceApplyState.Unknown)
            {
                diagnostics.Add(new(ArmDiagnosticSeverity.Warning, "UnrecognizedOperationState",
                    $"ARM reported status '{display}' for '{key}'; it stays Unknown."));
            }

            var statusCode = Str(properties, "statusCode");
            var operation = new ResourceOperation(key, state,
                statusCode.Length > 0 ? statusCode : null,
                ParseTimestamp(properties?["timestamp"] ?? entry["timestamp"]));

            if (!latest.TryGetValue(key, out var existing) || IsNewer(operation.TimestampUtc, existing.TimestampUtc))
            {
                latest[key] = operation;
            }
        }

        return (latest.Values.OrderBy(o => o.ResourceKey, StringComparer.Ordinal).ToList(),
                diagnostics.Distinct().ToList());
    }

    private static ResourceApplyState Map(string display) => display.ToLowerInvariant() switch
    {
        "created" or "accepted" => ResourceApplyState.Accepted,
        "started" or "running" => ResourceApplyState.Running,
        "succeeded" => ResourceApplyState.Succeeded,
        "failed" => ResourceApplyState.Failed,
        "canceled" => ResourceApplyState.Canceled,
        _ => ResourceApplyState.Unknown,
    };

    private static bool IsNewer(DateTimeOffset? candidate, DateTimeOffset? current) =>
        current is null || (candidate is not null && candidate >= current);

    private static DateTimeOffset? ParseTimestamp(JsonNode? node)
    {
        if (node?.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(node.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static string Str(JsonNode? node, string property) =>
        node is JsonObject o && o.TryGetPropertyValue(property, out var value) && value?.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : "";
}
