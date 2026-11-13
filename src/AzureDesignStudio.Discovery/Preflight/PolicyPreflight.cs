using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using static AzureDesignStudio.Discovery.Preflight.PreflightSupport;

namespace AzureDesignStudio.Discovery.Preflight;

public enum PolicyVerdict { Deny, Audit, Other }

/// <summary>One <c>policyEvaluations</c> entry for one previewed resource.</summary>
public sealed record PolicyFinding(string ResourceId, PolicyVerdict Verdict, string Effect,
    string? PolicyAssignmentId, string? PolicyDefinitionId, string? PolicyDefinitionReferenceId, string Reason);

/// <summary><see cref="Checked"/> + <see cref="NotChecked"/> is every candidate (Create/Modify
/// rows with an after payload under a subscription); every resource not checked is explained
/// by a note.</summary>
public sealed record PolicyPreflightResult(IReadOnlyList<PolicyFinding> Findings, int Checked, int NotChecked, IReadOnlyList<string> Notes);

/// <summary>
/// Wave B2: asks Azure Policy (<c>checkPolicyRestrictions</c>) about each resource a successful
/// what-if will create or modify, using the after payloads ARM has already resolved. Advisory
/// only: ARM still enforces policy at deployment. Secret values are scrubbed from every request
/// (<see cref="Scrub"/>) and deployment parameters are never sent. Verdicts come only from
/// <c>contentEvaluationResult.policyEvaluations</c>; <c>fieldRestrictions</c> describe pending
/// fields and are ignored. Never throws except an <see cref="OperationCanceledException"/> from
/// the caller's token: no permission, throttling, a malformed answer and the time limit all
/// become notes, with the findings gathered so far.
/// </summary>
public sealed class PolicyPreflight(ArmTransport transport)
{
    public const string ApiVersion = "2024-10-01";
    public const int MaxResources = 50;
    public const int MaxConcurrency = 3;

    public static IReadOnlyList<string> Caveats { get; } =
    [
        "Azure Policy is enforced by ARM at deployment.",
        "This check evaluates each resource on its own with the values what-if predicts.",
        "Assignments inherited from management groups and effects such as Modify, Append and DeployIfNotExists may not be reported.",
        "Secret values are removed before checking, so a policy on them is not evaluated.",
        "Only the previewed batch is checked.",
    ];

    private const string ForbiddenNote =
        "No permission to check Azure Policy (needs Microsoft.PolicyInsights/checkPolicyRestrictions/read).";

    private const string UnreadableSecretsNote =
        "Azure Policy was not checked: the template's secure parameters could not be read, so their values could not be removed from the requests.";

    private const string NoReason = "Policy evaluation reported a violation";

    /// <summary>The after-payload members sent as <c>resourceContent</c>; everything else
    /// (id, etag, systemData, dependsOn, ...) is not resource content.</summary>
    private static readonly string[] ContentMembers =
        ["type", "name", "location", "tags", "sku", "kind", "identity", "zones", "plan", "extendedLocation", "properties"];

    /// <summary>Types whose content is the secret itself: never sent at all.</summary>
    private static readonly HashSet<string> SecretTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.KeyVault/vaults/secrets",
        "Microsoft.KeyVault/vaults/keys",
        "Microsoft.Web/sites/config",
        "Microsoft.Web/sites/slots/config",
        "Microsoft.ApiManagement/service/namedValues",
    };

    /// <summary>Members dropped whatever their value (rule 2).</summary>
    private static readonly HashSet<string> SecretContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "protectedSettings", "customData", "sharedKey", "secureValue", "connectionString", "connectionStrings", "adminPassword",
    };

    /// <summary>A string leaf whose key, or any key above it, contains one of these is dropped (rule 3).</summary>
    private static readonly string[] SecretNameParts = ["password", "secret", "connectionstring", "accountkey", "accesskey", "token", "sas"];

    /// <summary>A string leaf containing one of these is dropped (rule 4).</summary>
    private static readonly string[] SecretPatterns = ["AccountKey=", "SharedAccessKey=", "SharedAccessSignature=", "sig=", "-----BEGIN"];

    public async Task<PolicyPreflightResult> CheckAsync(IReadOnlyList<WhatIfChange> changes, string templateJson, string? parametersJson,
        TimeSpan budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var candidates = (changes ?? [])
            .Where(IsCandidate)
            .OrderBy(c => c.ResourceId, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return new PolicyPreflightResult([], 0, 0, []);

        IReadOnlySet<string> secureValues;
        try
        {
            secureValues = SecureValues(templateJson, parametersJson);
        }
        catch (Exception)
        {
            // Without the secure parameter values a secret could reach Azure Policy: check nothing.
            return new PolicyPreflightResult([], 0, candidates.Count, [UnreadableSecretsNote]);
        }

        var selected = candidates.Take(MaxResources).ToList();
        var outcomes = new Outcome?[selected.Count];
        using var deadline = new CancellationTokenSource(DeadlineOf(budget));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var next = -1;
        var forbidden = 0;

        async Task WorkAsync()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // After a 403 or the time limit no new request starts.
                if (Volatile.Read(ref forbidden) == 1 || deadline.IsCancellationRequested)
                    return;
                var i = Interlocked.Increment(ref next);
                if (i >= selected.Count)
                    return;
                var outcome = await CheckOneAsync(selected[i], templateJson, secureValues, linked.Token, deadline.Token, ct)
                    .ConfigureAwait(false);
                outcomes[i] = outcome;
                if (outcome.Kind == OutcomeKind.Forbidden)
                    Volatile.Write(ref forbidden, 1);
            }
        }

        var workers = Enumerable.Range(0, Math.Min(MaxConcurrency, selected.Count)).Select(_ => WorkAsync()).ToArray();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Rethrown below as the caller's own cancellation.
        }
        ct.ThrowIfCancellationRequested();

        var stoppedByPermission = Volatile.Read(ref forbidden) == 1;
        var findings = new List<PolicyFinding>();
        var resourceNotes = new List<string>();
        int checkedCount = 0, timedOut = 0;
        var forbiddenSeen = false;
        foreach (var outcome in outcomes)
        {
            // A resource never started was stopped by the 403 or by the time limit.
            var kind = outcome?.Kind ?? (stoppedByPermission ? OutcomeKind.Forbidden : OutcomeKind.TimedOut);
            switch (kind)
            {
                case OutcomeKind.Checked:
                    checkedCount++;
                    findings.AddRange(outcome!.Findings);
                    break;
                case OutcomeKind.Skipped or OutcomeKind.Failed:
                    resourceNotes.Add(outcome!.Note!);
                    break;
                case OutcomeKind.Forbidden:
                    forbiddenSeen = true;
                    break;
                default:
                    timedOut++;
                    break;
            }
        }

        var notes = new List<string>();
        if (forbiddenSeen)
            notes.Add(ForbiddenNote);
        if (timedOut > 0)
            notes.Add($"Policy check stopped at the time limit: {timedOut} of {selected.Count} resources not checked.");
        notes.AddRange(resourceNotes);
        if (candidates.Count > selected.Count)
            notes.Add($"Only the first {MaxResources} resources were checked against Azure Policy; {candidates.Count - selected.Count} more were not checked.");

        return new PolicyPreflightResult(Order(findings), checkedCount, candidates.Count - checkedCount, notes);
    }

    private enum OutcomeKind { Checked, Skipped, Failed, Forbidden, TimedOut }

    private sealed record Outcome(OutcomeKind Kind, IReadOnlyList<PolicyFinding> Findings, string? Note);

    private async Task<Outcome> CheckOneAsync(WhatIfChange change, string? templateJson, IReadOnlySet<string> secureValues,
        CancellationToken requestToken, CancellationToken deadlineToken, CancellationToken ct)
    {
        var resourceId = change.ResourceId!;
        try
        {
            var body = BuildRequest(resourceId, change.AfterJson!, templateJson, secureValues, out var skipReason);
            if (body is null)
                return new Outcome(OutcomeKind.Skipped, [], $"{resourceId} was not checked against Azure Policy: {skipReason}.");

            var json = await transport.PostAsync(EndpointOf(resourceId), body, requestToken).ConfigureAwait(false);
            return new Outcome(OutcomeKind.Checked, ParseResult(resourceId, json), null);
        }
        catch (Exception e)
        {
            return Classify(e, deadlineToken, ct) switch
            {
                RequestFailure.TimedOut => new Outcome(OutcomeKind.TimedOut, [], null),
                RequestFailure.Forbidden => new Outcome(OutcomeKind.Forbidden, [], null),
                _ => new Outcome(OutcomeKind.Failed, [], $"{resourceId} was not checked against Azure Policy: the check failed ({CodeOf(e)})."),
            };
        }
    }

    private static bool IsCandidate(WhatIfChange? change)
    {
        if (change is null || change.Kind is not (WhatIfChangeKind.Create or WhatIfChangeKind.Modify)
            || string.IsNullOrWhiteSpace(change.AfterJson) || change.ResourceId is not { } id || !id.StartsWith('/'))
            return false;
        var segments = Segments(id);
        return segments.Length >= 3 && segments[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The resource-group form when the resource sits in a group (the group itself is
    /// created at subscription scope), else the subscription form.</summary>
    private string EndpointOf(string resourceId)
    {
        var segments = Segments(resourceId);
        var scope = segments.Length > 4 && segments[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)
            ? $"/subscriptions/{Uri.EscapeDataString(segments[1])}/resourceGroups/{Uri.EscapeDataString(segments[3])}"
            : $"/subscriptions/{Uri.EscapeDataString(segments[1])}";
        return $"{transport.Endpoints.ManagementEndpoint}{scope}/providers/Microsoft.PolicyInsights/checkPolicyRestrictions?api-version={ApiVersion}";
    }

    // ---------- secure parameter values ----------

    /// <summary>
    /// The values that must never reach Azure Policy: the value of every <c>secureString</c>
    /// parameter and every string leaf of every <c>secureObject</c> parameter, taken from the
    /// request parameters (<c>{ name: { value } }</c>, names case-insensitive), else from the
    /// template's <c>defaultValue</c>. Throws <see cref="JsonException"/> when either document
    /// cannot be read: a caller that cannot know the secrets must not send anything.
    /// </summary>
    public static IReadOnlySet<string> SecureValues(string templateJson, string? parametersJson)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        using var template = ParseObjectDocument(templateJson, "template");
        using var parameters = string.IsNullOrWhiteSpace(parametersJson) ? null : ParseObjectDocument(parametersJson, "parameters");

        if (!template.RootElement.TryGetProperty("parameters", out var declared) || declared.ValueKind != JsonValueKind.Object)
            return values;
        var supplied = parameters is null ? (JsonElement?)null : ParameterMap(parameters.RootElement);

        foreach (var parameter in declared.EnumerateObject())
        {
            if (parameter.Value.ValueKind != JsonValueKind.Object
                || !parameter.Value.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                continue;
            var type = typeElement.GetString();
            var isString = string.Equals(type, "secureString", StringComparison.OrdinalIgnoreCase);
            var isObject = string.Equals(type, "secureObject", StringComparison.OrdinalIgnoreCase);
            if (!isString && !isObject)
                continue;

            var value = SuppliedValue(supplied, parameter.Name)
                ?? (parameter.Value.TryGetProperty("defaultValue", out var defaultValue) ? defaultValue : (JsonElement?)null);
            if (value is { } v)
                CollectStrings(v, values);
        }
        return values;
    }

    private static JsonDocument ParseObjectDocument(string json, string what)
    {
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            return doc;
        doc.Dispose();
        throw new JsonException($"The {what} is not a JSON object.");
    }

    /// <summary>The <c>{ name: { value } }</c> map: the request's parameters object, or the
    /// <c>parameters</c> member of a whole parameter file.</summary>
    private static JsonElement ParameterMap(JsonElement root) =>
        (root.TryGetProperty("$schema", out _) || root.TryGetProperty("contentVersion", out _))
        && root.TryGetProperty("parameters", out var inner) && inner.ValueKind == JsonValueKind.Object
            ? inner
            : root;

    private static JsonElement? SuppliedValue(JsonElement? supplied, string name)
    {
        if (supplied is not { } map)
            return null;
        foreach (var entry in map.EnumerateObject())
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
                && entry.Value.ValueKind == JsonValueKind.Object
                && entry.Value.TryGetProperty("value", out var value))
                return value;
        }
        return null;
    }

    private static void CollectStrings(JsonElement element, HashSet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString() is { Length: > 0 } s:
                values.Add(s);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectStrings(property.Value, values);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectStrings(item, values);
                break;
        }
    }

    // ---------- request ----------

    /// <summary>
    /// The <c>checkPolicyRestrictions</c> body for one resource, or null with
    /// <paramref name="skipReason"/>: <c>resourceContent</c> is the after payload's resource
    /// members, scrubbed; <c>apiVersion</c> is the payload's, else the first template resource of
    /// the same type's; a child resource names its parent as <c>scope</c>. Audit effects are
    /// included and no <c>pendingFields</c> are sent.
    /// </summary>
    public static string? BuildRequest(string resourceId, string afterJson, string? templateJson,
        IReadOnlySet<string> secureValues, out string? skipReason)
    {
        skipReason = null;
        JsonObject after;
        try
        {
            if (JsonNode.Parse(afterJson) is not JsonObject parsed)
            {
                skipReason = "the what-if result for it is not a JSON object";
                return null;
            }
            after = parsed;
        }
        catch (JsonException)
        {
            skipReason = "the what-if result for it is not valid JSON";
            return null;
        }

        var idType = TypeOf(resourceId, resourceGroups: true);
        var type = StringMember(after, "type") ?? idType;
        if ((type is not null && SecretTypes.Contains(type)) || (idType is not null && SecretTypes.Contains(idType)))
        {
            skipReason = $"the content of a {type ?? idType} resource is a secret and is never sent";
            return null;
        }
        if (type is null)
        {
            skipReason = "its resource type is unknown";
            return null;
        }

        var apiVersion = StringMember(after, "apiVersion") ?? TemplateApiVersion(templateJson, type);
        if (apiVersion is null)
        {
            skipReason = $"no API version for {type} in the what-if result or the template";
            return null;
        }

        var content = new JsonObject();
        foreach (var member in ContentMembers)
        {
            if (Member(after, member) is { } property)
                content[property.Key] = property.Value?.DeepClone();
        }
        if (Member(content, "type") is null)
            content["type"] = type;

        var details = new JsonObject
        {
            ["resourceContent"] = Scrub(content, secureValues),
            ["apiVersion"] = apiVersion,
        };
        // A child resource (two or more type/name pairs after the namespace) is checked in its parent's scope.
        if (ParentOf(resourceId) is { } parent)
            details["scope"] = parent;

        var request = new JsonObject
        {
            ["resourceDetails"] = details,
            ["includeAuditEffect"] = true,
        };
        return request.ToJsonString();
    }

    private static KeyValuePair<string, JsonNode?>? Member(JsonObject obj, string name)
    {
        foreach (var property in obj)
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property;
        return null;
    }

    private static string? StringMember(JsonObject obj, string name) =>
        Member(obj, name)?.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string? TemplateApiVersion(string? templateJson, string type)
    {
        if (string.IsNullOrWhiteSpace(templateJson))
            return null;
        try
        {
            using var template = JsonDocument.Parse(templateJson);
            return template.RootElement.ValueKind == JsonValueKind.Object
                && template.RootElement.TryGetProperty("resources", out var resources)
                ? FindApiVersion(resources, parentType: null, type)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Depth-first, in template order: a <c>resources</c> array or a symbolic-name
    /// object (languageVersion 2.0); a nested child's short type is joined to its parent's.</summary>
    private static string? FindApiVersion(JsonElement resources, string? parentType, string type)
    {
        var items = resources.ValueKind switch
        {
            JsonValueKind.Array => resources.EnumerateArray().ToList(),
            JsonValueKind.Object => resources.EnumerateObject().Select(p => p.Value).ToList(),
            _ => [],
        };
        foreach (var resource in items)
        {
            if (resource.ValueKind != JsonValueKind.Object
                || !resource.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
                || typeElement.GetString() is not { Length: > 0 } declared)
                continue;
            var fullType = parentType is not null && !declared.Contains('/') ? $"{parentType}/{declared}" : declared;
            if (string.Equals(fullType, type, StringComparison.OrdinalIgnoreCase)
                && resource.TryGetProperty("apiVersion", out var version) && version.ValueKind == JsonValueKind.String
                && version.GetString() is { Length: > 0 } apiVersion && !apiVersion.StartsWith('['))
                return apiVersion;
            if (resource.TryGetProperty("resources", out var nested) && FindApiVersion(nested, fullType, type) is { } found)
                return found;
        }
        return null;
    }

    // ---------- scrub ----------

    /// <summary>
    /// A copy of <paramref name="resourceContent"/> with every secret removed (Global
    /// Constraints, rules 1-4):
    /// <list type="bullet">
    /// <item>string leaves holding a secure parameter value, and members whose name holds one;</item>
    /// <item>the members <c>protectedSettings</c>, <c>customData</c>, <c>sharedKey</c>,
    /// <c>secureValue</c>, <c>connectionString(s)</c> and <c>adminPassword</c>, whatever they hold;</item>
    /// <item>the <c>value</c> of every <c>{ name, value }</c> array item (app settings, environment
    /// variables), and the <c>value</c> and <c>defaultValue</c> of every object whose <c>type</c> is
    /// SecureString or SecureObject (Data Factory linked services), at any depth and under any key;</item>
    /// <item>every string leaf held by, or nested at any depth under, a key containing password,
    /// secret, connectionstring, accountkey, accesskey, token or sas (an array item takes its
    /// array's key);</item>
    /// <item>string leaves carrying a key, SAS signature or PEM block.</item>
    /// </list>
    /// The value, name and pattern rules take string leaves only: booleans and numbers stay.
    /// Secret-bearing types (rule 5) are refused by <see cref="BuildRequest"/> before this.
    /// </summary>
    public static JsonObject Scrub(JsonObject resourceContent, IReadOnlySet<string> secureValues)
    {
        var copy = (JsonObject)resourceContent.DeepClone();
        ScrubObject(copy, secureValues, underSecretName: false);
        return copy;
    }

    private static void ScrubObject(JsonObject obj, IReadOnlySet<string> secureValues, bool underSecretName)
    {
        if (IsSecureDeclaration(obj))
            RemoveMembers(obj, "value", "defaultValue");
        foreach (var (key, value) in obj.ToList())
        {
            if (SecretContainers.Contains(key) || HoldsSecureValue(key, secureValues)
                || !Keep(value, underSecretName || HasSecretName(key), secureValues))
                obj.Remove(key);
        }
    }

    private static void ScrubArray(JsonArray array, IReadOnlySet<string> secureValues, bool underSecretName)
    {
        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is JsonObject item && Member(item, "name") is not null)
                RemoveMembers(item, "value");
            // An array item is judged under the array's key.
            if (!Keep(array[i], underSecretName, secureValues))
                array.RemoveAt(i);
        }
    }

    /// <summary>Scrubs containers in place; false when a string leaf must go.
    /// <paramref name="underSecretName"/>: the leaf's key, or a key above it, names a secret.</summary>
    private static bool Keep(JsonNode? node, bool underSecretName, IReadOnlySet<string> secureValues)
    {
        switch (node)
        {
            case JsonObject obj:
                ScrubObject(obj, secureValues, underSecretName);
                return true;
            case JsonArray array:
                ScrubArray(array, secureValues, underSecretName);
                return true;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>();
                return !underSecretName && !HoldsSecureValue(text, secureValues) && !HasSecretPattern(text);
            default:
                return true; // numbers, booleans, null
        }
    }

    /// <summary><c>{ "type": "SecureString" | "SecureObject", "value": ... }</c>: a secret
    /// whatever key holds it (<c>servicePrincipalKey</c> matches no name rule).</summary>
    private static bool IsSecureDeclaration(JsonObject obj) =>
        StringMember(obj, "type") is { } type
        && (type.Equals("SecureString", StringComparison.OrdinalIgnoreCase) || type.Equals("SecureObject", StringComparison.OrdinalIgnoreCase));

    private static void RemoveMembers(JsonObject obj, params string[] names)
    {
        foreach (var key in obj.Select(p => p.Key).Where(k => names.Any(n => k.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList())
            obj.Remove(key);
    }

    private static bool HasSecretName(string key) =>
        SecretNameParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static bool HasSecretPattern(string text) =>
        SecretPatterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static bool HoldsSecureValue(string text, IReadOnlySet<string> secureValues) =>
        secureValues.Any(secret => secret.Length > 0 && text.Contains(secret, StringComparison.OrdinalIgnoreCase));

    // ---------- response ----------

    /// <summary>
    /// One finding per <c>contentEvaluationResult.policyEvaluations[]</c> entry, classified by
    /// <c>effectDetails.policyEffect</c> (Deny, Audit, else Other); an <c>evaluationResult</c>
    /// other than NonCompliant is Other. <c>fieldRestrictions</c> are ignored. Duplicates are
    /// removed. Throws <see cref="JsonException"/> on a response that is not a JSON object or
    /// whose evaluation list is malformed - never an empty "no findings".
    /// </summary>
    public static IReadOnlyList<PolicyFinding> ParseResult(string resourceId, string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new JsonException("The policy check response is empty.");
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The policy check response is not a JSON object.");
        if (!root.TryGetProperty("contentEvaluationResult", out var content) || content.ValueKind == JsonValueKind.Null)
            return [];
        if (content.ValueKind != JsonValueKind.Object)
            throw new JsonException("contentEvaluationResult is not a JSON object.");
        if (!content.TryGetProperty("policyEvaluations", out var evaluations) || evaluations.ValueKind == JsonValueKind.Null)
            return [];
        if (evaluations.ValueKind != JsonValueKind.Array)
            throw new JsonException("policyEvaluations is not a JSON array.");

        var findings = new List<PolicyFinding>();
        foreach (var evaluation in evaluations.EnumerateArray())
        {
            if (evaluation.ValueKind != JsonValueKind.Object)
                continue;
            var info = ObjectMember(evaluation, "policyInfo");
            var effect = StringMember(ObjectMember(evaluation, "effectDetails"), "policyEffect") ?? "Unknown";
            var result = StringMember(evaluation, "evaluationResult");

            PolicyVerdict verdict;
            string reason;
            if (!string.Equals(result, "NonCompliant", StringComparison.OrdinalIgnoreCase))
            {
                verdict = PolicyVerdict.Other;
                reason = $"evaluation result '{result ?? "(none)"}'";
            }
            else
            {
                verdict = effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? PolicyVerdict.Deny
                    : effect.Equals("Audit", StringComparison.OrdinalIgnoreCase) ? PolicyVerdict.Audit
                    : PolicyVerdict.Other;
                reason = ReasonOf(ObjectMember(evaluation, "evaluationDetails"));
            }

            findings.Add(new PolicyFinding(resourceId, verdict, effect,
                StringMember(info, "policyAssignmentId"), StringMember(info, "policyDefinitionId"),
                StringMember(info, "policyDefinitionReferenceId"), reason));
        }
        return Order(findings);
    }

    private static string ReasonOf(JsonElement? details)
    {
        if (StringMember(details, "reason") is { } reason)
            return reason;
        if (details is { } d && d.TryGetProperty("evaluatedExpressions", out var expressions)
            && expressions.ValueKind == JsonValueKind.Array
            && expressions.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object) is { ValueKind: JsonValueKind.Object } first)
        {
            var target = first.TryGetProperty("targetValue", out var t)
                ? t.ValueKind == JsonValueKind.String ? t.GetString() : t.ValueKind == JsonValueKind.Null ? null : t.GetRawText()
                : null;
            var parts = new[] { StringMember(first, "path") ?? StringMember(first, "expression"), StringMember(first, "operator"), target }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (parts.Count > 0)
                return string.Join(' ', parts);
        }
        return NoReason;
    }

    private static IReadOnlyList<PolicyFinding> Order(IEnumerable<PolicyFinding> findings) =>
        findings
            .Distinct()
            .OrderBy(f => f.ResourceId, StringComparer.Ordinal)
            .ThenBy(f => f.Verdict)
            .ThenBy(f => f.PolicyAssignmentId, StringComparer.Ordinal)
            .ThenBy(f => f.PolicyDefinitionReferenceId, StringComparer.Ordinal)
            .ToList();

    private static JsonElement? ObjectMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? StringMember(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;
}
