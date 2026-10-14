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
/// failure. Unrecognized display statuses stay Unknown rather than being coerced to failed.
/// Malformed payloads produce a diagnostic, never an exception.
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

        if (root?["value"] is not JsonArray value)
        {
            return ([], [new(ArmDiagnosticSeverity.Error, "InvalidOperationsPayload", "Operations payload has no value array.")]);
        }

        var latest = new Dictionary<string, ResourceOperation>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ArmDiagnostic>();
        foreach (var entry in value)
        {
            if (entry?["targetResource"] is not JsonNode target)
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

            var display = entry["displayStatus"]?["display"]?.GetValueKind() == JsonValueKind.String
                ? entry["displayStatus"]!["display"]!.GetValue<string>()
                : "";
            var state = Map(display);
            if (display.Length > 0 && state == ResourceApplyState.Unknown)
            {
                diagnostics.Add(new(ArmDiagnosticSeverity.Warning, "UnrecognizedOperationState",
                    $"ARM reported display status '{display}' for '{key}'; it stays Unknown."));
            }

            var operation = new ResourceOperation(key, state,
                entry["properties"]?["statusCode"]?.GetValueKind() == JsonValueKind.String
                    ? entry["properties"]!["statusCode"]!.GetValue<string>()
                    : null,
                ParseTimestamp(entry["timestamp"]));

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

        return DateTimeOffset.TryParse(node.GetValue<string>(), out var parsed) ? parsed : null;
    }

    private static string Str(JsonNode? node, string property) =>
        node is JsonObject o && o.TryGetPropertyValue(property, out var value) && value?.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : "";
}
