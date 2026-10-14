using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>What ARM does to a resource that left the stack's deployment. <see cref="Delete"/> is gated until Phase 13.</summary>
public enum StackUnmanageAction
{
    Detach,
    Delete,
    Ignore,
}

/// <summary>ARM deny-settings lock levels on the stack's managed resources.</summary>
public enum StackDenyLock
{
    NotDeletion,
    NotDeletionAndChildUsage,
}

/// <summary>denySettings; <c>null</c> on a policy means ARM's default (no deny assignments).</summary>
public sealed record StackDenySettings(bool Enabled, IReadOnlyList<StackDenyLock> Locks);

/// <summary>The unmanage + deny policy a stack enforces (v2 plan [60]).</summary>
public sealed record StackPolicy(
    StackUnmanageAction Missing,
    StackUnmanageAction ResourceGroup = StackUnmanageAction.Detach,
    StackUnmanageAction Deployment = StackUnmanageAction.Detach,
    StackDenySettings? DenySettings = null);

/// <summary>
/// Result of building a deployment-stack PUT body. A plan with any Error diagnostic must never
/// be sent (fail closed); the hash binds the approval to the exact bytes that would be sent.
/// </summary>
public sealed record DeploymentStackBody(string Json, string PlanHash, IReadOnlyList<ArmDiagnostic> Diagnostics)
{
    public bool Success => !Diagnostics.Any(d => d.Severity == ArmDiagnosticSeverity.Error);
}

public enum StackDriftKind
{
    /// <summary>Managed by the stack and present live.</summary>
    Synced,
    /// <summary>Exists live but the stack does not manage it (the stack-out-of-sync case).</summary>
    Untracked,
    /// <summary>The stack expects it but it is gone from the live plane.</summary>
    Missing,
}

/// <summary>One live/expected resource's standing against the stack's managed list.</summary>
public sealed record StackDriftRow(string Id, StackDriftKind Kind);

/// <summary>Drift verdict for a stack against the current inventory ([61] stack-out-of-sync guard).</summary>
public sealed record StackDriftReport(IReadOnlyList<StackDriftRow> Rows)
{
    public int SyncedCount => Rows.Count(r => r.Kind == StackDriftKind.Synced);
    public int UntrackedCount => Rows.Count(r => r.Kind == StackDriftKind.Untracked);
    public int MissingCount => Rows.Count(r => r.Kind == StackDriftKind.Missing);

    /// <summary>Any divergence at all: the guard an apply must consult before touching a drifted stack.</summary>
    public bool IsOutOfSync => UntrackedCount > 0 || MissingCount > 0;
}

/// <summary>
/// Deployment-stack manager core (v2 plan 12.3/[60][61]): builds the ARM
/// <c>Microsoft.Resources/deploymentStacks</c> PUT body - inline template plus the
/// actionOnUnmanage + denySettings policy - with a canonical-JSON identity hash so the
/// approval gate binds to the exact payload, and answers the two read-only questions the
/// stack needs: which managed ids a stack reports, and how the live plane diverges from
/// them. Nothing here executes; the delete-flavored policy values are refused until the
/// Phase 13 decommissioning gates ([36]/[62]/[65]) are in force, so a stack can never
/// become a side door for unmanaged deletion.
/// </summary>
public static class DeploymentStack
{
    /// <summary>Stack API version the body is written against.</summary>
    public const string ApiVersion = "2021-04-01";

    private static readonly Regex StackNamePattern = new(@"^[-\w\._\(\)]{1,63}$", RegexOptions.Compiled);

    /// <summary>
    /// Builds the PUT body for one stack. The template must be a JSON object; parameters are
    /// optional. Policy members are emitted in a fixed order and the hash covers the canonical
    /// form, so the same inputs always produce byte-identical bodies and the same hash.
    /// </summary>
    public static DeploymentStackBody Build(
        string stackName,
        string location,
        string templateJson,
        StackPolicy policy,
        string? parametersJson = null)
    {
        var diagnostics = new List<ArmDiagnostic>();

        if (string.IsNullOrWhiteSpace(stackName) || !StackNamePattern.IsMatch(stackName))
            diagnostics.Add(Error("StackNameInvalid", "Stack name must match ^[-\\w._()]{1,63}$."));
        if (string.IsNullOrWhiteSpace(location))
            diagnostics.Add(Error("StackLocationMissing", "The stack resource needs a location."));
        if (policy is null)
            diagnostics.Add(Error("StackPolicyMissing", "A stack without an unmanage policy would delete nothing but manage nothing; state the policy."));
        else
        {
            // Phase 13 has not shipped: Delete actions here execute without impact previews,
            // protection windows or the approved-plan delete gate - refuse, not warn.
            foreach (var (slot, action) in new[]
            {
                ("missing", policy.Missing),
                ("resourceGroup", policy.ResourceGroup),
                ("deployment", policy.Deployment),
            })
                if (action == StackUnmanageAction.Delete)
                    diagnostics.Add(Error("StackDeleteActionLocked",
                        $"actionOnUnmanage.{slot} = Delete is disabled until the decommissioning gates ship (v2 plan 13); use Detach or Ignore."));

            if (policy.DenySettings is { Enabled: true, Locks.Count: 0 })
                diagnostics.Add(Error("StackDenyLocksRequired",
                    "denySettings.status Enabled requires at least one lock (NotDeletion, NotDeletionAndChildUsage)."));
        }

        JsonElement template = default;
        var templateOk = false;
        try
        {
            using var doc = JsonDocument.Parse(templateJson ?? string.Empty);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                template = doc.RootElement.Clone();
                templateOk = true;
            }
            else
                diagnostics.Add(Error("StackTemplateMalformed", "The stack template must be a JSON object."));
        }
        catch (JsonException)
        {
            diagnostics.Add(Error("StackTemplateMalformed", "The stack template is not valid JSON."));
        }

        JsonElement parameters = default;
        var parametersOk = false;
        if (parametersJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(parametersJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    parameters = doc.RootElement.Clone();
                    parametersOk = true;
                }
                else
                    diagnostics.Add(Error("StackParametersMalformed", "Stack parameters must be a JSON object."));
            }
            catch (JsonException)
            {
                diagnostics.Add(Error("StackParametersMalformed", "Stack parameters are not valid JSON."));
            }
        }

        var json = BuildBody(stackName, location, template, templateOk, parameters, parametersOk, policy);
        var hash = Hash(Canonicalize(JsonDocument.Parse(json).RootElement));
        return new DeploymentStackBody(json, hash, diagnostics);
    }

    /// <summary>
    /// Reads the managed resource ids a stack GET reports. A stack with no managed list is a
    /// malformed payload (Error), an empty list is legitimate (a stack that detached everything);
    /// entries without a usable resourceId are counted, never silently dropped.
    /// </summary>
    public static (IReadOnlyList<string> Ids, IReadOnlyList<ArmDiagnostic> Diagnostics) ReadManagedResources(string stackGetJson)
    {
        var diagnostics = new List<ArmDiagnostic>();
        var ids = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stackGetJson);
        }
        catch (JsonException)
        {
            return (ids, [Error("StackPayloadMalformed", "The stack payload is not valid JSON.")]);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !properties.TryGetProperty("managedResources", out var managed)
                || managed.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(Error("StackManagedResourcesMissing",
                    "The stack payload carries no properties.managedResources array."));
                return (ids, diagnostics);
            }

            var index = 0;
            foreach (var entry in managed.EnumerateArray())
            {
                var id = entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("resourceId", out var rid)
                    && rid.ValueKind == JsonValueKind.String
                    ? rid.GetString()?.Trim()
                    : null;
                if (string.IsNullOrEmpty(id))
                    diagnostics.Add(Error("StackManagedResourceInvalid",
                        $"managedResources[{index}] carries no resourceId."));
                else
                    ids.Add(id);
                index++;
            }
        }

        return (ids, diagnostics);
    }

    /// <summary>
    /// The stack-out-of-sync guard ([61]): compares a stack's managed ids against the live
    /// inventory. ARM ids are case-insensitive, so comparison trims and lowercases while rows
    /// keep the casing of first appearance (stack first, then live). Deterministic: rows are
    /// ordinal-sorted by the comparison key.
    /// </summary>
    public static StackDriftReport CheckDrift(IEnumerable<string> stackManagedIds, IEnumerable<string> liveIds)
    {
        var stackIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in stackManagedIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            stackIds.TryAdd(id.Trim().ToLowerInvariant(), id.Trim());
        }

        var liveSeen = new HashSet<string>(StringComparer.Ordinal);
        var liveIdsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in liveIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var key = id.Trim().ToLowerInvariant();
            if (liveSeen.Add(key))
                liveIdsByKey[key] = id.Trim();
        }

        var rows = new List<StackDriftRow>();
        foreach (var (key, display) in stackIds)
        {
            if (liveSeen.Contains(key))
                rows.Add(new StackDriftRow(display, StackDriftKind.Synced));
            else
                rows.Add(new StackDriftRow(display, StackDriftKind.Missing));
        }
        foreach (var (key, display) in liveIdsByKey)
        {
            if (!stackIds.ContainsKey(key))
                rows.Add(new StackDriftRow(display, StackDriftKind.Untracked));
        }

        rows.Sort((a, b) => string.Compare(a.Id.ToLowerInvariant(), b.Id.ToLowerInvariant(), StringComparison.Ordinal));
        return new StackDriftReport(rows);
    }

    private static string BuildBody(
        string stackName,
        string location,
        JsonElement template,
        bool templateOk,
        JsonElement parameters,
        bool parametersOk,
        StackPolicy? policy)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendProp(sb, "name", stackName ?? string.Empty, true);
        AppendProp(sb, "location", location ?? string.Empty, false);
        AppendProp(sb, "type", "Microsoft.Resources/deploymentStacks", false);
        AppendProp(sb, "apiVersion", ApiVersion, false);

        sb.Append(",\"properties\":{");
        var firstProperty = true;
        if (templateOk)
        {
            sb.Append("\"template\":").Append(template.GetRawText());
            firstProperty = false;
        }
        if (parametersOk)
        {
            if (!firstProperty) sb.Append(',');
            sb.Append("\"parameters\":").Append(parameters.GetRawText());
            firstProperty = false;
        }
        if (policy is not null)
        {
            if (!firstProperty) sb.Append(',');
            sb.Append("\"policy\":{\"actionOnUnmanage\":{");
            sb.Append("\"resourceGroup\":\"").Append(ActionName(policy.ResourceGroup)).Append("\",");
            sb.Append("\"deployment\":\"").Append(ActionName(policy.Deployment)).Append("\",");
            sb.Append("\"missing\":\"").Append(ActionName(policy.Missing)).Append("\"}");
            if (policy.DenySettings is { } deny)
            {
                sb.Append(",\"denySettings\":{\"status\":\"").Append(deny.Enabled ? "Enabled" : "Disabled").Append('"');
                sb.Append(",\"locks\":[");
                var locks = deny.Locks.OrderBy(l => l.ToString(), StringComparer.Ordinal);
                var first = true;
                foreach (var lockKind in locks)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(lockKind).Append('"');
                }
                sb.Append(']');
                sb.Append('}');
            }
            sb.Append('}');
        }
        sb.Append('}');
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendProp(StringBuilder sb, string name, string value, bool first)
    {
        if (!first) sb.Append(',');
        sb.Append(JsonSerializer.Serialize(name)).Append(':').Append(JsonSerializer.Serialize(value));
    }

    private static string ActionName(StackUnmanageAction action) => action switch
    {
        StackUnmanageAction.Delete => "Delete",
        StackUnmanageAction.Ignore => "Ignore",
        _ => "Detach",
    };

    // Same canonical form and identity algorithm as DeployPlanArtifact (keys sorted ordinal,
    // arrays ordered, literals verbatim, sha256: prefix) - the hash an approval binds to is one
    // algorithm, not a per-engine dialect; kept local because Discovery stays dependency-narrow.
    private static string Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();
        AppendCanonical(sb, element);
        return sb.ToString();
    }

    private static void AppendCanonical(StringBuilder sb, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) sb.Append(',');
                    firstProperty = false;
                    sb.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    AppendCanonical(sb, property.Value);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var child in element.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    AppendCanonical(sb, child);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(element.GetRawText());
                break;
        }
    }

    private static ArmDiagnostic Error(string code, string message) =>
        new(ArmDiagnosticSeverity.Error, code, message);

    private static string Hash(string canonicalJson) =>
        "sha256:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}
