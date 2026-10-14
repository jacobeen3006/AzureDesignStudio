using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>Azure deployment scopes, ordered so earlier batches never depend on later ones.</summary>
public enum DeployScopeKind
{
    Tenant,
    ManagementGroup,
    Subscription,
    ResourceGroup,
}

/// <summary>One staged deployment: a self-contained sub-template for exactly one scope.</summary>
public sealed record DeployScopeBatch(DeployScopeKind Kind, string TemplateJson, IReadOnlyList<string> ResourceKeys);

/// <summary>
/// Result of staging a template into ordered scope batches. A plan with any Error diagnostic
/// must never execute (fail closed); warnings describe honest degradations.
/// </summary>
public sealed record DeployScopePlan(IReadOnlyList<DeployScopeBatch> Batches, IReadOnlyList<ArmDiagnostic> Diagnostics)
{
    public bool Success => !Diagnostics.Any(d => d.Severity == ArmDiagnosticSeverity.Error);
}

/// <summary>
/// Multi-scope staging for topological apply (v2 plan 11.2/[51]): splits one ARM template into
/// ordered sub-templates - Tenant, then ManagementGroup, then Subscription, then ResourceGroup -
/// so a staged apply creates an RG under its subscription batch before RG resources, and a role
/// assignment on a group before members of that group. Parameter/variable closure travels with
/// each batch (referenced-by-text, so expressions keep resolving); outputs are dropped per batch
/// and reported. A dependsOn edge that would need a later scope to deploy first is a plan error,
/// never a silent reorder: the batches that exist are the deployment, not a hint.
/// </summary>
public static partial class DeployScopePlanner
{
    private const string DefaultSchema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#";

    /// <summary>Template JSON keeps ARM expressions readable: default escaping turns ' into \u0027.</summary>
    private static readonly JsonSerializerOptions BatchJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"parameters\(\s*'([^']+)'\s*\)")]
    private static partial Regex ParameterRef();

    [GeneratedRegex(@"variables\(\s*'([^']+)'\s*\)")]
    private static partial Regex VariableRef();

    [GeneratedRegex(@"resourceId\(\s*'([^']+)'\s*,\s*'([^']+)'")]
    private static partial Regex ResourceIdRef();

    /// <summary>Parses a literal <c>resourceId('type','name',...)</c> expression, shared with
    /// targeted retry; null when the string is not a literal resourceId reference.</summary>
    public static (string Type, string Name)? ParseLiteralResourceId(string raw)
    {
        var match = ResourceIdRef().Match(raw);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    /// <summary>
    /// Stages a template; never throws - malformed input becomes an InvalidTemplate error.
    /// <paramref name="ambientScope"/> is the scope the caller will deploy at: resources that
    /// carry no explicit scope (ordinary RG-scoped role assignments, ambient policies) inherit
    /// it. Expression scopes cannot be resolved while staging, so they fail the plan closed.
    /// </summary>
    public static DeployScopePlan Create(string templateJson, DeployScopeKind ambientScope = DeployScopeKind.ResourceGroup)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(templateJson);
        }
        catch (JsonException)
        {
            return new DeployScopePlan([], [new(ArmDiagnosticSeverity.Error, "InvalidTemplate", "Template is not valid JSON.")]);
        }

        return Stage(root, ambientScope);
    }

    private static DeployScopePlan Stage(JsonNode? root, DeployScopeKind ambientScope)
    {
        var diagnostics = new List<ArmDiagnostic>();
        if (root is not JsonObject template || template["resources"] is not JsonArray resources)
        {
            return new DeployScopePlan([], [new(ArmDiagnosticSeverity.Error, "InvalidTemplate", "Template has no resources array.")]);
        }
        var members = new List<JsonObject>(resources.Count);
        foreach (var item in resources)
        {
            if (item is not JsonObject member)
            {
                return new DeployScopePlan([], [new(ArmDiagnosticSeverity.Error, "InvalidTemplate", "resources contains a null or non-object member.")]);
            }

            members.Add(member);
        }

        if (members.Count == 0)
        {
            return new DeployScopePlan([], []);
        }

        // Classify every resource; key = lowercase type/name.
        var entries = members.Select(resource => (Key: Key(Str(resource, "type"), Str(resource, "name")),
                                                  Kind: Classify(resource, Str(resource, "type"), ambientScope, diagnostics),
                                                  Element: resource)).ToList();

        // Validate: no dependsOn edge may require a later scope batch to deploy first.
        var kindByKey = entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, kind, element) in entries)
        {
            foreach (var dep in Dependencies(element))
            {
                if (!kindByKey.TryGetValue(dep.Key, out var depKind))
                {
                    diagnostics.Add(new(ArmDiagnosticSeverity.Warning, "UnplannableDependency",
                        $"'{key}' depends on '{dep.Raw}', which no resource in this template answers; what-if/deploy will judge it."));
                    continue;
                }

                if (depKind > kind)
                {
                    diagnostics.Add(new(ArmDiagnosticSeverity.Error, "ScopeOrderConflict",
                        $"'{key}' ({kind}) deploys before its dependency '{dep.Key}' ({depKind}); the plan cannot be staged without reordering."));
                }
            }
        }

        if (diagnostics.Any(d => d.Severity == ArmDiagnosticSeverity.Error))
        {
            return new DeployScopePlan([], diagnostics);
        }

        // Stage ordered batches; parameters and variables travel by textual reference.
        var schema = Str(template, "$schema");
        var contentVersion = Str(template, "contentVersion");
        var parameters = template["parameters"] as JsonObject;
        var variables = template["variables"] as JsonObject;
        var hasOutputs = template["outputs"] is JsonObject outputs && outputs.Count > 0;

        var batches = new List<DeployScopeBatch>();
        foreach (var kind in (DeployScopeKind[])Enum.GetValues(typeof(DeployScopeKind)))
        {
            var batch = entries.Where(e => e.Kind == kind).ToList();
            if (batch.Count == 0)
            {
                continue;
            }

            var resourcesNode = new JsonArray([.. batch.Select(m => JsonNode.Parse(m.Element.ToJsonString()))]);
            var resourcesText = resourcesNode.ToJsonString(BatchJson);
            var body = new JsonObject
            {
                ["$schema"] = schema.Length > 0 ? schema : DefaultSchema,
                ["contentVersion"] = contentVersion.Length > 0 ? contentVersion : "1.0.0.0",
            };

            var referencedParameters = ReferencedNames(parameters?.Select(p => p.Key), ParameterRef(), resourcesText);
            if (referencedParameters.Count > 0)
            {
                body["parameters"] = Subset(parameters!, referencedParameters);
            }

            var referencedVariables = ReferencedNames(variables?.Select(p => p.Key), VariableRef(), resourcesText);
            if (referencedVariables.Count > 0)
            {
                body["variables"] = Subset(variables!, referencedVariables);
            }

            body["resources"] = resourcesNode;
            batches.Add(new DeployScopeBatch(kind, body.ToJsonString(BatchJson), batch.Select(m => m.Key).ToList()));
        }

        if (hasOutputs)
        {
            diagnostics.Add(new(ArmDiagnosticSeverity.Warning, "OutputsDropped",
                "Template outputs cannot be served per scope batch and were dropped from the staged plan."));
        }

        return new DeployScopePlan(batches, diagnostics);
    }

    private static DeployScopeKind Classify(JsonNode? resource, string type, DeployScopeKind ambientScope, List<ArmDiagnostic> diagnostics)
    {
        var t = type.ToLowerInvariant();

        // Scope-polymorphic policy/authorization types: an explicit literal scope path decides;
        // no scope means the resource inherits the caller's deployment scope (never tenant by
        // default); an expression scope cannot be resolved while staging, so the plan fails closed.
        if (t.StartsWith("microsoft.authorization/") || t.StartsWith("microsoft.policy/") || t.StartsWith("microsoft.blueprint/"))
        {
            var scope = Str(resource, "scope");
            if (scope.Length == 0)
            {
                return ambientScope;
            }

            if (scope.Contains('['))
            {
                diagnostics.Add(new(ArmDiagnosticSeverity.Error, "ScopeExpressionNotPlanned",
                    $"'{type}' carries an expression scope that staging cannot resolve; refusing to stage the plan."));
                return ambientScope;
            }

            return ScopeKindFromPath(scope);
        }

        return t switch
        {
            "microsoft.management/managementgroups" or "microsoft.management/managementgroups/settings" => DeployScopeKind.ManagementGroup,
            "microsoft.subscription/aliases" => DeployScopeKind.Tenant,
            "microsoft.resources/resourcegroups" or "microsoft.resources/subscriptions/resourcegroups" => DeployScopeKind.Subscription,
            _ when t.StartsWith("microsoft.resources/subscriptions/", StringComparison.Ordinal) => DeployScopeKind.Subscription,
            _ when Str(resource, "subscriptionId").Length > 0 => DeployScopeKind.Subscription,
            _ => DeployScopeKind.ResourceGroup,
        };
    }

    private static DeployScopeKind ScopeKindFromPath(string scope)
    {
        if (scope == "/")
        {
            return DeployScopeKind.Tenant;
        }

        if (scope.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase))
        {
            return DeployScopeKind.ManagementGroup;
        }

        if (scope.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase))
        {
            return DeployScopeKind.ResourceGroup;
        }

        // A subscription-level scope - bare (/subscriptions/xxx) or a provider path under the
        // subscription (/subscriptions/xxx/providers/...) - is the subscription scope.
        if (Regex.IsMatch(scope, @"^/subscriptions/[^/]+(/|$)", RegexOptions.IgnoreCase))
        {
            return DeployScopeKind.Subscription;
        }

        return DeployScopeKind.ResourceGroup;
    }

    private static JsonObject Subset(JsonObject source, HashSet<string> names)
    {
        var node = new JsonObject();
        foreach (var property in source)
        {
            if (names.Contains(property.Key))
            {
                node[property.Key] = JsonNode.Parse(property.Value?.ToJsonString() ?? "null");
            }
        }

        return node;
    }

    private static HashSet<string> ReferencedNames(IEnumerable<string>? declaredNames, Regex pattern, string haystack)
    {
        var declared = new HashSet<string>(declaredNames ?? [], StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in pattern.Matches(haystack))
        {
            var name = match.Groups[1].Value;
            if (declared.Contains(name))
            {
                found.Add(name);
            }
        }

        return found;
    }

    private static IEnumerable<(string Key, string Raw)> Dependencies(JsonNode? resource)
    {
        if (resource?["dependsOn"] is not JsonArray dependsOn)
        {
            yield break;
        }

        foreach (var entry in dependsOn)
        {
            var raw = entry?.GetValueKind() == JsonValueKind.String ? entry!.GetValue<string>() : "";
            if (raw.Length == 0)
            {
                continue;
            }

            var match = ResourceIdRef().Match(raw);
            if (match.Success)
            {
                yield return (Key(match.Groups[1].Value, match.Groups[2].Value), raw);
            }
            else if (raw.Contains('['))
            {
                yield return ("", raw); // expression staging cannot resolve; the validator's unresolved checks stay authoritative
            }
        }
    }

    public static string Key(string type, string name) => $"{type}/{name}".ToLowerInvariant();

    private static string Str(JsonNode? node, string property) =>
        node is JsonObject o && o.TryGetPropertyValue(property, out var value) && value?.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : "";

    /// <summary>
    /// Pre-deploy gate for a pipeline that deploys exactly one scope (no target identity):
    /// staging errors block, and so does any batch beyond the ambient scope - without a bound
    /// target, explicit resource-group scope paths cannot be proven to hit what was approved
    /// and block unbound. Warnings never block.
    /// </summary>
    public static bool TryFindBlockingErrors(string templateJson, DeployScopeKind ambientScope, out string message) =>
        TryFindBlockingErrorsCore(templateJson, ambientScope, null, null, out _, out message);

    /// <summary>
    /// Staged-apply gate bound to the approved (subscription, resource group) target (v2 plan 11.2):
    /// the plan must stage cleanly and every batch must be a Subscription or ResourceGroup batch -
    /// those are exactly the two endpoints the approval identity binds, so the staged apply creates
    /// the subscription-scope batch under the approved subscription and then deploys the
    /// resource-group batch into the approved group. Tenant and management-group batches stay blocked:
    /// the approval contract carries no target identity for them. Every explicit literal target
    /// override must still name the approved target, or the plan is rejected <c>ScopeTargetMismatch</c>
    /// and never rides the approved token.
    /// </summary>
    public static bool TryStageForApply(string templateJson, string subscriptionId, string resourceGroup,
        out DeployScopePlan plan, out string message) =>
        TryFindBlockingErrorsCore(templateJson, DeployScopeKind.ResourceGroup, subscriptionId, resourceGroup, out plan, out message);


    private static bool TryFindBlockingErrorsCore(string templateJson, DeployScopeKind ambientScope, string? subscriptionId, string? resourceGroup,
        out DeployScopePlan plan, out string message)
    {
        plan = Create(templateJson, ambientScope);
        var bound = !string.IsNullOrWhiteSpace(subscriptionId) && !string.IsNullOrWhiteSpace(resourceGroup);
        var stagedApply = ambientScope == DeployScopeKind.ResourceGroup && bound;
        var errors = plan.Diagnostics.Where(d => d.Severity == ArmDiagnosticSeverity.Error).ToList();
        var beyond = (stagedApply
                ? plan.Batches.Where(b => b.Kind is not (DeployScopeKind.Subscription or DeployScopeKind.ResourceGroup))
                : plan.Batches.Where(b => b.Kind != ambientScope))
            .Select(b => b.Kind.ToString()).Distinct().ToList();
        if (beyond.Count > 0)
        {
            errors.Add(new(ArmDiagnosticSeverity.Error, "MultiScopeNotDeployable", stagedApply
                ? $"The staged plan requires batches beyond the approved Subscription/ResourceGroup targets ({string.Join(", ", beyond)}); the approval identity binds only those endpoints."
                : $"The staged plan requires batches beyond the {ambientScope} scope ({string.Join(", ", beyond)}); this pipeline deploys exactly one scope."));
        }

        if (ambientScope == DeployScopeKind.ResourceGroup)
        {
            foreach (var (key, property, value) in ExplicitTargetOverrides(templateJson))
            {
                if (property == "scope")
                {
                    if (value.Contains('['))
                    {
                        continue; // expression scope: Create already failed the plan closed
                    }

                    if (!TryParseScopePath(value, out var parsed, out var parsedSub, out var parsedRg))
                    {
                        // The classifier's fallback reads unrecognized strings as resource-group scope;
                        // the gate refuses to deploy a literal it cannot parse instead of treating it as ambient.
                        errors.Add(new(ArmDiagnosticSeverity.Error, "ScopePathMalformed",
                            $"'{key}' carries the literal scope '{value}', which is not a valid ARM scope path; refusing to treat it as the deployment target."));
                    }
                    else if (parsed == DeployScopeKind.ResourceGroup)
                    {
                        if (!bound)
                        {
                            errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeTargetUnbound",
                                $"'{key}' carries an explicit resource-group scope '{value}'; without a bound (subscription, resource group) target the gate cannot prove it matches what was approved."));
                        }
                        else if (!parsedSub.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)
                              || !parsedRg.Equals(resourceGroup, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeTargetMismatch",
                                $"'{key}' carries an explicit scope '{value}' outside the approved target /subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}."));
                        }
                    }
                    else if (parsed == DeployScopeKind.Subscription && stagedApply
                             && parsedSub.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                    {
                        // A subscription-scope path at the approved subscription stages with the
                        // subscription batch - exactly what the staged apply deploys there.
                    }
                    else if (parsed == DeployScopeKind.Subscription && stagedApply)
                    {
                        errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeTargetMismatch",
                            $"'{key}' carries an explicit subscription-scope path '{value}' outside the approved subscription '{subscriptionId}'."));
                    }
                    else
                    {
                        errors.Add(new(ArmDiagnosticSeverity.Error, "MultiScopeNotDeployable", stagedApply
                            ? $"'{key}' carries an explicit {parsed}-scope path '{value}'; the approval identity binds only the approved subscription and resource group."
                            : $"'{key}' carries an explicit {parsed}-scope path '{value}'; this pipeline deploys exactly one resource-group scope."));
                    }

                    continue;
                }

                // Nested-deployment retarget: a literal must name the approved target; an
                // expression target is unprovable and fails closed (ScopeExpressionNotPlanned precedent).
                if (value.Contains('['))
                {
                    errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeExpressionNotPlanned",
                        $"'{key}' overrides the deployment target with an expression {property} that staging cannot resolve; refusing to deploy an unprovable scope."));
                }
                else if (!bound)
                {
                    errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeTargetUnbound",
                        $"'{key}' overrides the deployment target with an explicit {property}; without a bound (subscription, resource group) target the gate cannot prove it matches what was approved."));
                }
                else
                {
                    var expected = property == "subscriptionId" ? subscriptionId! : resourceGroup!;
                    if (!value.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeTargetMismatch",
                            $"'{key}' overrides the deployment target with {property} '{value}' outside the approved target /subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}."));
                    }
                }
            }
        }

        message = string.Join(Environment.NewLine, errors.Select(e => $"{e.Code}: {e.Message}"));
        return errors.Count > 0;
    }

    /// <summary>
    /// Values for exactly the parameters a staged batch declares, in deployment-parameters shape:
    /// ARM refuses a value for a parameter the batch does not declare, so the staged apply sends
    /// the subset the batch carries. Unparseable input yields no values rather than an unapproved set.
    /// </summary>
    public static string ParametersForBatch(string batchTemplateJson, string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return "{}";
        }

        try
        {
            if (JsonNode.Parse(batchTemplateJson) is not JsonObject batch || batch["parameters"] is not JsonObject declared)
            {
                return "{}";
            }

            if (JsonNode.Parse(parametersJson) is not JsonObject parameters)
            {
                return "{}";
            }

            var names = declared.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subset = new JsonObject();
            foreach (var parameter in parameters)
            {
                if (names.Contains(parameter.Key))
                {
                    subset[parameter.Key] = JsonNode.Parse(parameter.Value?.ToJsonString() ?? "null");
                }
            }

            return subset.ToJsonString(BatchJson);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    /// <summary>Every non-empty literal target override on a resource declaration: <c>scope</c> plus the
    /// nested-deployment retarget properties <c>resourceGroup</c>/<c>subscriptionId</c>, including resources
    /// inside nested deployment templates (recursively). Empty values inherit the ambient target.</summary>
    private static List<(string Key, string Property, string Value)> ExplicitTargetOverrides(string templateJson)
    {
        var found = new List<(string, string, string)>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(templateJson);
        }
        catch (JsonException)
        {
            return found;
        }

        if (root is not JsonObject template)
        {
            return found;
        }

        CollectTargetOverrides(template["resources"] as JsonArray, found);
        return found;
    }

    private static void CollectTargetOverrides(JsonArray? resources, List<(string Key, string Property, string Value)> found)
    {
        if (resources is null)
        {
            return;
        }

        foreach (var item in resources)
        {
            if (item is not JsonObject resource)
            {
                continue;
            }

            var key = Key(Str(resource, "type"), Str(resource, "name"));
            foreach (var property in new[] { "scope", "resourceGroup", "subscriptionId" })
            {
                var value = Str(resource, property);
                if (value.Length > 0)
                {
                    found.Add((key, property, value));
                }
            }

            if (resource["properties"] is JsonObject properties && properties["template"] is JsonObject inner)
            {
                CollectTargetOverrides(inner["resources"] as JsonArray, found);
            }
        }
    }

    /// <summary>
    /// Strict ARM scope-path grammar: <c>/</c> (tenant),
    /// <c>/providers/Microsoft.Management/managementGroups/{name}</c>, or
    /// <c>/subscriptions/{id}...</c> where the subscription-scope form is bare or
    /// <c>/providers/...</c>, and the resource-group form is <c>/resourceGroups/{name}</c>
    /// with no second resource-group segment anywhere deeper. Anything else is unparseable -
    /// notably a relative <c>/resourceGroups/{name}</c> and a spoofed path that hides a
    /// second resource-group behind a provider segment.
    /// </summary>
    private static bool TryParseScopePath(string scope, out DeployScopeKind kind, out string subscriptionId, out string resourceGroup)
    {
        kind = DeployScopeKind.Tenant;
        subscriptionId = "";
        resourceGroup = "";
        var segments = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true; // "/"
        }

        if (segments.Length >= 4
            && segments[0].Equals("providers", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("microsoft.management", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("managementGroups", StringComparison.OrdinalIgnoreCase))
        {
            kind = DeployScopeKind.ManagementGroup;
            return true;
        }

        if (!segments[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments.Length >= 4 && segments[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 4; i < segments.Length; i++)
            {
                if (segments[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            kind = DeployScopeKind.ResourceGroup;
            subscriptionId = segments[1];
            resourceGroup = segments[3];
            return true;
        }

        if (segments.Length == 2 || (segments.Length > 2 && segments[2].Equals("providers", StringComparison.OrdinalIgnoreCase)))
        {
            kind = DeployScopeKind.Subscription;
            subscriptionId = segments[1];
            return true;
        }

        return false;
    }
}
