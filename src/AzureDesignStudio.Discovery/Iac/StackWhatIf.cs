using System.Text;
using System.Text.Json;
using AzureDesignStudio.Discovery.Iac.WhatIf;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>What a deployment-stack what-if predicts for one resource (<c>changeType</c>).</summary>
public enum StackChangeKind
{
    Create,
    Modify,
    NoChange,
    Detach,
    Delete,
    /// <summary>ARM could not evaluate the resource, or answered a value this build does not know:
    /// never treated as "no change".</summary>
    Unsupported,
}

/// <summary>One resource-level change. <c>Potential</c> is <c>changeCertainty = potential</c>; the
/// management and deny members are the raw ARM values (<c>managed</c>, <c>denyDelete</c>, ...).</summary>
public sealed record StackResourceChange(
    string Id, string Type, StackChangeKind Kind, bool Potential,
    string? ManagementBefore, string? ManagementAfter,
    string? DenyBefore, string? DenyAfter, string? UnsupportedReason);

/// <summary>Parsed deployment-stacks what-if result. Property-level changes are not read.</summary>
public sealed record StackWhatIfSummary(
    string ProvisioningState,
    IReadOnlyList<StackResourceChange> Changes,
    string? DenyModeBefore, string? DenyModeAfter,
    IReadOnlyList<ArmDiagnostic> Diagnostics)
{
    public IReadOnlyList<string> DeleteResourceIds => IdsOf(StackChangeKind.Delete);

    public IReadOnlyList<string> DetachResourceIds => IdsOf(StackChangeKind.Detach);

    public bool HasDelete => Changes.Any(c => c.Kind == StackChangeKind.Delete);

    /// <summary>Non-null when the result cannot be trusted as complete: a state other than
    /// "succeeded", an error-level diagnostic, any Unsupported change or any Potential change.
    /// A result with no <c>changes</c> member carries an error diagnostic, so it lands here too.</summary>
    public string? IndeterminacyReason
    {
        get
        {
            if (!string.Equals(ProvisioningState, "succeeded", StringComparison.OrdinalIgnoreCase))
                return $"stack what-if state is '{ProvisioningState}', not 'succeeded'";
            if (Diagnostics.FirstOrDefault(d => d.Severity == ArmDiagnosticSeverity.Error) is { } error)
                return $"stack what-if reported an error ({error.Code}): {error.Message}";
            if (Changes.FirstOrDefault(c => c.Kind == StackChangeKind.Unsupported) is { } unsupported)
                return $"stack what-if cannot evaluate {unsupported.Id}"
                    + (string.IsNullOrEmpty(unsupported.UnsupportedReason) ? "" : $": {unsupported.UnsupportedReason}");
            if (Changes.FirstOrDefault(c => c.Potential) is { } potential)
                return $"stack what-if is only potentially sure about {potential.Id}";
            return null;
        }
    }

    private IReadOnlyList<string> IdsOf(StackChangeKind kind) =>
        Changes.Where(c => c.Kind == kind && c.Id.Length > 0)
            .Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
}

/// <summary>Request builder and strict parser for <c>Microsoft.Resources/deploymentStacksWhatIfResults</c>.</summary>
public static class StackWhatIf
{
    public const string ApiVersion = "2025-07-01";
    public const string RetentionInterval = "PT1H";

    /// <summary>Builds the PUT body from a stack body produced by <see cref="DeploymentStack.Build"/>.
    /// <c>template</c>, <c>parameters</c>, <c>actionOnUnmanage</c> and <c>denySettings</c> are copied
    /// verbatim, never rebuilt, so what is previewed is exactly what was hashed. No location member.</summary>
    public static string BuildRequest(string stackResourceId, string stackBodyJson)
    {
        if (string.IsNullOrWhiteSpace(stackResourceId))
            throw new ArgumentException("A stack resource id is required.", nameof(stackResourceId));

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stackBodyJson ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The stack body is not valid JSON: {ex.Message}", nameof(stackBodyJson));
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The stack body has no properties object.", nameof(stackBodyJson));

            var sb = new StringBuilder();
            sb.Append("{\"properties\":{\"deploymentStackResourceId\":").Append(JsonSerializer.Serialize(stackResourceId));
            sb.Append(",\"retentionInterval\":").Append(JsonSerializer.Serialize(RetentionInterval));
            foreach (var member in new[] { "template", "parameters", "actionOnUnmanage", "denySettings" })
            {
                if (props.TryGetProperty(member, out var value))
                    sb.Append(',').Append(JsonSerializer.Serialize(member)).Append(':').Append(value.GetRawText());
                else if (member != "parameters")
                    throw new ArgumentException($"The stack body has no properties.{member}.", nameof(stackBodyJson));
            }
            return sb.Append("}}").ToString();
        }
    }

    public static StackWhatIfSummary Parse(string resultJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(resultJson ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw new WhatIfException("MalformedStackWhatIf", $"Stack what-if response is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
                throw new WhatIfException("MalformedStackWhatIf", "Stack what-if response has no properties object.");

            if (props.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                throw new WhatIfException(
                    ReadString(error, "code") ?? "StackWhatIfFailed",
                    ReadString(error, "message") ?? "The stack what-if failed.");

            var state = ReadString(props, "provisioningState") ?? "unknown";
            var diagnostics = new List<ArmDiagnostic>();
            var changes = new List<StackResourceChange>();
            string? denyBefore = null, denyAfter = null;

            if (props.TryGetProperty("changes", out var changesEl) && changesEl.ValueKind == JsonValueKind.Object
                && changesEl.TryGetProperty("resourceChanges", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "StackChangeMalformed",
                            "A resourceChanges entry is not an object."));
                        continue;
                    }
                    changes.Add(new StackResourceChange(
                        ReadString(item, "id") ?? string.Empty,
                        ReadString(item, "type") ?? string.Empty,
                        MapKind(ReadString(item, "changeType")),
                        string.Equals(ReadString(item, "changeCertainty"), "potential", StringComparison.OrdinalIgnoreCase),
                        ReadNested(item, "managementStatusChange", "before"),
                        ReadNested(item, "managementStatusChange", "after"),
                        ReadNested(item, "denyStatusChange", "before"),
                        ReadNested(item, "denyStatusChange", "after"),
                        ReadString(item, "unsupportedReason")));
                }

                if (changesEl.TryGetProperty("denySettingsChange", out var deny) && deny.ValueKind == JsonValueKind.Object)
                {
                    denyBefore = ReadNested(deny, "before", "mode");
                    denyAfter = ReadNested(deny, "after", "mode");
                }
            }
            else
            {
                // A result that lists nothing is not "no changes": say it could not be read.
                diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "StackChangesMissing",
                    "The stack what-if result carries no properties.changes.resourceChanges array."));
            }

            if (props.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in diags.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object))
                {
                    // ArmDiagnosticSeverity has no info level: info and warning both stay below Error.
                    var severity = string.Equals(ReadString(d, "level"), "error", StringComparison.OrdinalIgnoreCase)
                        ? ArmDiagnosticSeverity.Error
                        : ArmDiagnosticSeverity.Warning;
                    var message = ReadString(d, "message") ?? string.Empty;
                    if (ReadString(d, "target") is { Length: > 0 } target)
                        message = $"{message} ({target})";
                    diagnostics.Add(new ArmDiagnostic(severity, ReadString(d, "code") ?? "StackWhatIfDiagnostic", message));
                }
            }

            return new StackWhatIfSummary(state, changes, denyBefore, denyAfter, diagnostics);
        }
    }

    /// <summary>Name of the temporary what-if result for one approved plan: <c>ads-</c> plus the first
    /// 16 hex digits of the plan hash, so the same plan always maps to the same result.</summary>
    public static string ResultName(string stackName, string planHash)
    {
        if (!DeploymentStack.IsValidStackName(stackName))
            throw new ArgumentException("Stack name must match ^[-\\w._()]{1,90}$.", nameof(stackName));
        var hex = (planHash ?? string.Empty).StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? planHash![7..]
            : planHash ?? string.Empty;
        if (hex.Length < 16 || !hex[..16].All(Uri.IsHexDigit))
            throw new ArgumentException("The plan hash must start with 16 hex digits.", nameof(planHash));
        return "ads-" + hex[..16].ToLowerInvariant();
    }

    private static StackChangeKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "create" => StackChangeKind.Create,
        "modify" => StackChangeKind.Modify,
        "nochange" => StackChangeKind.NoChange,
        "detach" => StackChangeKind.Detach,
        "delete" => StackChangeKind.Delete,
        _ => StackChangeKind.Unsupported,
    };

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ReadNested(JsonElement el, string outer, string inner) =>
        el.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object ? ReadString(o, inner) : null;
}
