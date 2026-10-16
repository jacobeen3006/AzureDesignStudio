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
/// Managed-vs-planned review ([61]): what the stack manages today compared with the ids the
/// incoming template will claim. Distinct from <see cref="CheckDrift"/> (managed-vs-live):
/// this table is shown before approval so the user sees exactly what this update adopts
/// (+) and what it leaves behind (-) under the detach policy.
/// </summary>
public sealed record StackManagementDiff(int AlreadyManagedCount, IReadOnlyList<string> TakingOver, IReadOnlyList<string> Releasing);

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
    /// <paramref name="bypassOutOfSyncError"/> lands ARM's explicit out-of-sync override - the
    /// member rides inside the hashed body, so a sha256: approval binds the bypass exactly as
    /// it binds the template. Default <c>false</c> emits nothing new (byte-identical bodies).
    /// </summary>
    public static DeploymentStackBody Build(
        string stackName,
        string location,
        string templateJson,
        StackPolicy policy,
        string? parametersJson = null,
        bool bypassOutOfSyncError = false)
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

        var json = BuildBody(stackName, location, template, templateOk, parameters, parametersOk, policy, bypassOutOfSyncError);
        var hash = Hash(Canonicalize(JsonDocument.Parse(json).RootElement));
        return new DeploymentStackBody(json, hash, diagnostics);
    }

    /// <summary>Wire form of an unmanage action, case-insensitive; blank, unknown or
    /// out-of-range values are refused so callers answer 400 instead of guessing. A parsed
    /// <see cref="StackUnmanageAction.Delete"/> still meets <c>StackDeleteActionLocked</c> in Build.</summary>
    public static bool TryParseAction(string? value, out StackUnmanageAction action)
    {
        action = default;
        if (!IsNamedWire(value) || !Enum.TryParse<StackUnmanageAction>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return false;
        action = parsed;
        return true;
    }

    // Enum names never start with a digit: a numeric wire value is someone's guess at the
    // protocol, not a protocol statement - refuse it rather than let "1" smuggle in Delete.
    private static bool IsNamedWire(string? value) => !string.IsNullOrWhiteSpace(value) && !char.IsDigit(value[0]);

    /// <summary>Wire form of a deny lock level, case-insensitive; blank, numeric or unknown values are refused.</summary>
    public static bool TryParseDenyLock(string? value, out StackDenyLock denyLock)
    {
        denyLock = default;
        if (!IsNamedWire(value) || !Enum.TryParse<StackDenyLock>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return false;
        denyLock = parsed;
        return true;
    }

    /// <summary>The name rule ARM enforces on stacks; the name interpolates the ARM URL, so
    /// every caller validates through this gate before any HTTP call or URL build.</summary>
    public static bool IsValidStackName(string? name) =>
        !string.IsNullOrEmpty(name) && StackNamePattern.IsMatch(name);

    /// <summary>
    /// Reads the managed resource ids a stack GET/list payload reports. Two verified shapes
    /// are accepted: the get-era <c>properties.managedResources[].resourceId</c> and the
    /// list/get-era <c>properties.resources[].id</c> (<c>{id, status, denyStatus}</c> entries);
    /// the union is deduped on the same trim+lowercase key the drift guard uses. A payload
    /// carrying neither key is malformed (Error); an empty array under either key is a
    /// legitimate reported-empty. Entries without a usable id are counted, never dropped.
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
                || properties.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Error("StackManagedResourcesMissing",
                    "The stack payload carries no properties.managedResources or properties.resources array."));
                return (ids, diagnostics);
            }

            var reported = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, idMember) in new[] { ("managedResources", "resourceId"), ("resources", "id") })
            {
                if (!properties.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
                    continue;
                reported = true;
                var index = 0;
                foreach (var entry in list.EnumerateArray())
                {
                    var id = entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty(idMember, out var rid)
                        && rid.ValueKind == JsonValueKind.String
                        ? rid.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrEmpty(id))
                        diagnostics.Add(Error("StackManagedResourceInvalid",
                            $"{key}[{index}] carries no {idMember}."));
                    else if (seen.Add(id.ToLowerInvariant()))
                        ids.Add(id);
                    index++;
                }
            }
            if (!reported)
            {
                diagnostics.Add(Error("StackManagedResourcesMissing",
                    "The stack payload carries no properties.managedResources or properties.resources array."));
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

    /// <summary>
    /// Builds the ARM ids the template's <c>resources[]</c> will manage if deployed to this
    /// scope: <c>/subscriptions/{sub}/resourceGroups/{rg}/providers/{type}/{name}</c>, with
    /// child types spliced as <c>.../virtualNetworks/vnet1/subnets/subnet1</c> (standard ARM
    /// id form; name segments fill type segments in order). Fail-closed, never guessed: a
    /// missing or expression (<c>[...]</c>) name/type reports <c>PlannedIdUnresolved</c>, a
    /// literal absolute <c>scope</c> outside the target resource group reports
    /// <c>PlannedIdOffScope</c>. Ids keep template casing; output is ordinal-sorted.
    /// </summary>
    public static (IReadOnlyList<string> Ids, IReadOnlyList<ArmDiagnostic> Diagnostics) PlannedManagedResourceIds(
        string templateJson, string subscriptionId, string resourceGroup)
    {
        var diagnostics = new List<ArmDiagnostic>();
        var ids = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(templateJson ?? string.Empty);
        }
        catch (JsonException)
        {
            return (ids, [Error("PlannedIdUnresolved", "The stack template is not valid JSON; nothing can be planned.")]);
        }

        using (doc)
        {
            // A template without a resources array manages nothing - an honest empty, not an error.
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("resources", out var resources)
                || resources.ValueKind != JsonValueKind.Array)
                return (ids, diagnostics);

            foreach (var entry in resources.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(Error("PlannedIdUnresolved", "A resources[] entry is not an object."));
                    continue;
                }
                var type = GetString(entry, "type");
                var name = GetString(entry, "name");
                var label = $"{(type is { Length: > 0 } t ? t : "?")}/{(name is { Length: > 0 } n ? n : "?")}";
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name) || name![0] == '[')
                {
                    diagnostics.Add(Error("PlannedIdUnresolved",
                        $"{label} cannot be resolved to an ARM id before deployment."));
                    continue;
                }

                // A literal absolute scope is the template's own statement of where the resource
                // lands; if it names anything outside this stack's resource group the id below
                // would be a lie - report instead of guessing. Relative scopes ("parent/child")
                // stay in the target group and need no check.
                var scope = GetString(entry, "scope");
                if (scope is { Length: > 0 } && scope[0] == '/' && !ScopeMatchesTarget(scope, subscriptionId, resourceGroup))
                {
                    diagnostics.Add(Error("PlannedIdOffScope",
                        $"{label} carries a literal scope ({scope}) outside the stack's resource group."));
                    continue;
                }

                ids.Add(BuildArmId(subscriptionId, resourceGroup, type!, name!));
            }
        }

        ids.Sort(StringComparer.Ordinal);
        return (ids, diagnostics);
    }

    /// <summary>
    /// Compares a stack's managed ids against the ids an incoming template plans to manage
    /// ([61] managed-vs-planned review). Case-insensitive like <see cref="CheckDrift"/>:
    /// trim + lowercase comparison, first-appearance casing in output, ordinal-sorted lists.
    /// </summary>
    public static StackManagementDiff DiffManagedAgainstPlan(IEnumerable<string> stackManagedIds, IEnumerable<string> plannedIds)
    {
        var managed = FirstByKey(stackManagedIds);
        var planned = FirstByKey(plannedIds);

        var already = 0;
        var takingOver = new List<string>();
        foreach (var (key, display) in planned)
        {
            if (managed.ContainsKey(key)) already++;
            else takingOver.Add(display);
        }
        var releasing = new List<string>();
        foreach (var (key, display) in managed)
        {
            if (!planned.ContainsKey(key)) releasing.Add(display);
        }

        takingOver.Sort((a, b) => string.Compare(a.ToLowerInvariant(), b.ToLowerInvariant(), StringComparison.Ordinal));
        releasing.Sort((a, b) => string.Compare(a.ToLowerInvariant(), b.ToLowerInvariant(), StringComparison.Ordinal));
        return new StackManagementDiff(already, takingOver, releasing);
    }

    private static Dictionary<string, string> FirstByKey(IEnumerable<string>? ids)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids ?? [])
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            map.TryAdd(id.Trim().ToLowerInvariant(), id.Trim());
        }
        return map;
    }

    private static bool ScopeMatchesTarget(string absoluteScope, string subscriptionId, string resourceGroup)
    {
        var segments = absoluteScope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /subscriptions/{sub}/resourceGroups/{rg}[/...] - ARM ids are case-insensitive.
        return segments.Length >= 4
            && string.Equals(segments[0], "subscriptions", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], subscriptionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "resourceGroups", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[3], resourceGroup, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string BuildArmId(string subscriptionId, string resourceGroup, string type, string name)
    {
        var typeParts = type.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var nameParts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        sb.Append("/subscriptions/").Append(subscriptionId)
            .Append("/resourceGroups/").Append(resourceGroup)
            .Append("/providers/").Append(typeParts[0]);
        for (var i = 1; i < typeParts.Length; i++)
        {
            sb.Append('/').Append(typeParts[i]);
            if (i - 1 < nameParts.Length) sb.Append('/').Append(nameParts[i - 1]);
        }
        // More name than type segments: the extra segments ride at the end rather than vanish.
        for (var i = typeParts.Length - 1; i < nameParts.Length; i++)
            sb.Append('/').Append(nameParts[i]);
        return sb.ToString();
    }

    private static string BuildBody(
        string stackName,
        string location,
        JsonElement template,
        bool templateOk,
        JsonElement parameters,
        bool parametersOk,
        StackPolicy? policy,
        bool bypassOutOfSyncError)
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
        if (bypassOutOfSyncError)
        {
            // ARM guidance: redeploying the same template is the fix for an out-of-sync stack;
            // this explicit override is the reviewed last resort, and the hash binds it.
            if (!firstProperty) sb.Append(',');
            sb.Append("\"bypassStackOutOfSyncError\":true");
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
