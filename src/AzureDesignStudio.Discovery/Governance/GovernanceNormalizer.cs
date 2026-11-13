using System.Text.Json;

namespace AzureDesignStudio.Discovery.Governance;

/// <summary>
/// One flat Resource Graph row (the columns <see cref="GovernanceQueries"/> projects) to a
/// governance finding or Defender plan. A row it cannot use - not an object, no resource id, a
/// recommendation that is dismissed, postponed, completed, suppressed or reported by Defender -
/// is null; it never throws. A column that is missing, empty or not a string reads as absent
/// (<c>tostring</c> of a missing property is the empty string). ARM ids compare
/// case-insensitively.
/// </summary>
public static class GovernanceNormalizer
{
    private const string AssessmentsSegment = "/providers/Microsoft.Security/assessments/";

    private static readonly HashSet<string> ClosedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dismissed", "Postponed", "Completed",
    };

    public static GovernanceFinding? FromAdvisorRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || Text(row, "resourceId") is not { } resourceId)
            return null;
        if (Text(row, "status") is { } status && ClosedStatuses.Contains(status.Trim()))
            return null;
        if (Suppressed(row))
            return null;
        // Defender for Cloud recommendations also surface in Advisor; Defender reports them.
        if (Text(row, "source") is { } source && source.Contains(AssessmentsSegment, StringComparison.OrdinalIgnoreCase))
            return null;

        var title = Text(row, "problem") ?? "Advisor recommendation";
        var detail = Text(row, "solution");
        return new GovernanceFinding(
            GovernanceSource.Advisor,
            Text(row, "id") ?? SyntheticId("advisor", resourceId, title, detail),
            resourceId,
            SubscriptionOf(row, resourceId),
            SeverityOf(Text(row, "impact")),
            Text(row, "category") ?? "",
            title,
            detail,
            PolicyAssignmentId: null,
            PolicyDefinitionId: null);
    }

    public static GovernanceFinding? FromPolicyRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || Text(row, "resourceId") is not { } resourceId)
            return null;

        var assignmentId = Text(row, "assignmentId");
        var definitionId = Text(row, "definitionId");
        var referenceId = Text(row, "referenceId");
        return new GovernanceFinding(
            GovernanceSource.Policy,
            Text(row, "id") ?? SyntheticId("policy", resourceId, assignmentId, definitionId, referenceId),
            resourceId,
            SubscriptionOf(row, resourceId),
            GovernanceSeverity.Medium,
            "Policy",
            Text(row, "assignmentName") ?? LastSegment(assignmentId) ?? "Policy non-compliance",
            referenceId,
            assignmentId,
            definitionId);
    }

    public static GovernanceFinding? FromDefenderRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return null;

        var id = Text(row, "id");
        // The assessment id is {resourceId}/providers/Microsoft.Security/assessments/{name}, so
        // the resource is recoverable from it when resourceDetails names no ARM resource.
        var resourceId = Text(row, "targetId") is { } target && target.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            ? target
            : id?.IndexOf(AssessmentsSegment, StringComparison.OrdinalIgnoreCase) is > 0 and var at ? id[..at] : null;
        if (resourceId is null)
            return null;

        var title = Text(row, "name") ?? "Defender for Cloud recommendation";
        return new GovernanceFinding(
            GovernanceSource.Defender,
            id ?? SyntheticId("defender", resourceId, title),
            resourceId,
            SubscriptionOf(row, resourceId),
            SeverityOf(Text(row, "severity")),
            "Security",
            title,
            Detail: null,
            PolicyAssignmentId: null,
            PolicyDefinitionId: null);
    }

    public static DefenderPlan? FromPlanRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object
            || Text(row, "plan") is not { } plan
            || Text(row, "tier") is not { } tier
            || (Text(row, "subscriptionId") ?? SubscriptionOfId(Text(row, "id"))) is not { } subscriptionId)
            return null;

        tier = tier.Trim();
        return new DefenderPlan(subscriptionId, plan,
            tier.Equals("Standard", StringComparison.OrdinalIgnoreCase) ? "Standard"
            : tier.Equals("Free", StringComparison.OrdinalIgnoreCase) ? "Free"
            : tier);
    }

    /// <summary>
    /// One finding per (Source, ResourceId, Title, PolicyAssignmentId, PolicyDefinitionId,
    /// Detail) - ids case-insensitive - ordered by ResourceId, Source, Severity descending and
    /// Title. Of duplicates the most severe is kept (then the lowest RecordId), so the result
    /// does not depend on the order rows arrived in.
    /// </summary>
    public static IReadOnlyList<GovernanceFinding> Dedupe(IEnumerable<GovernanceFinding> findings)
    {
        var kept = new Dictionary<GovernanceFinding, GovernanceFinding>(DedupeKey.Instance);
        foreach (var finding in findings)
        {
            if (finding is null)
                continue;
            if (!kept.TryGetValue(finding, out var existing)
                || finding.Severity > existing.Severity
                || (finding.Severity == existing.Severity && string.CompareOrdinal(finding.RecordId, existing.RecordId) < 0))
                kept[finding] = finding;
        }

        return kept.Values
            .OrderBy(f => f.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Source)
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            // Ties broken on every remaining field, so the order is total.
            .ThenBy(f => f.ResourceId, StringComparer.Ordinal)
            .ThenBy(f => f.PolicyAssignmentId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.PolicyDefinitionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Detail, StringComparer.Ordinal)
            .ThenBy(f => f.RecordId, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class DedupeKey : IEqualityComparer<GovernanceFinding>
    {
        public static readonly DedupeKey Instance = new();

        public bool Equals(GovernanceFinding? x, GovernanceFinding? y) =>
            ReferenceEquals(x, y)
            || (x is not null && y is not null
                && x.Source == y.Source
                && string.Equals(x.ResourceId, y.ResourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Title, y.Title, StringComparison.Ordinal)
                && string.Equals(x.PolicyAssignmentId, y.PolicyAssignmentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.PolicyDefinitionId, y.PolicyDefinitionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Detail, y.Detail, StringComparison.Ordinal));

        public int GetHashCode(GovernanceFinding f) => HashCode.Combine(
            f.Source,
            StringComparer.OrdinalIgnoreCase.GetHashCode(f.ResourceId),
            StringComparer.Ordinal.GetHashCode(f.Title),
            f.PolicyAssignmentId is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(f.PolicyAssignmentId),
            f.PolicyDefinitionId is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(f.PolicyDefinitionId),
            f.Detail is null ? 0 : StringComparer.Ordinal.GetHashCode(f.Detail));
    }

    // Matched by name, never Enum.TryParse: that would also accept "2" or "High, Low".
    private static GovernanceSeverity SeverityOf(string? value) => value?.Trim() switch
    {
        { } v when v.Equals("High", StringComparison.OrdinalIgnoreCase) => GovernanceSeverity.High,
        { } v when v.Equals("Low", StringComparison.OrdinalIgnoreCase) => GovernanceSeverity.Low,
        _ => GovernanceSeverity.Medium,
    };

    /// <summary><c>tostring(properties.suppressionIds)</c> is "" when there are none and the
    /// array's JSON text when there are; "[]" is none too.</summary>
    private static bool Suppressed(JsonElement row)
    {
        if (!row.TryGetProperty("suppressions", out var value))
            return false;
        if (value.ValueKind == JsonValueKind.Array)
            return value.GetArrayLength() > 0;
        if (StringOf(value)?.Trim() is not { Length: > 0 } text)
            return false;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            return parsed.RootElement.ValueKind switch
            {
                JsonValueKind.Array => parsed.RootElement.GetArrayLength() > 0,
                JsonValueKind.Null => false,
                _ => true,
            };
        }
        catch (JsonException)
        {
            return true; // some text that is not an empty list: a suppression
        }
    }

    /// <summary>The row's subscription, else the one its resource id names, else "".</summary>
    private static string SubscriptionOf(JsonElement row, string resourceId) =>
        Text(row, "subscriptionId") ?? SubscriptionOfId(resourceId) ?? "";

    private static string? SubscriptionOfId(string? id)
    {
        if (id is null)
            return null;
        var segments = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ? segments[1] : null;
    }

    private static string? LastSegment(string? id) =>
        id?.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segments ? segments[^1] : null;

    /// <summary>A stable record id for a row Resource Graph gave no id: the source and the
    /// fields that identify the finding, lower-cased.</summary>
    private static string SyntheticId(string source, params string?[] parts) =>
        source + ":" + string.Join('|', parts.Select(p => p ?? "")).ToLowerInvariant();

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && StringOf(value) is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>A string value, else null - also for one GetString cannot decode (a lone
    /// surrogate escape parses as JSON but throws when read).</summary>
    private static string? StringOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
