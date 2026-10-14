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

        if (Regex.IsMatch(scope, @"^/subscriptions/[^/]+/?$", RegexOptions.IgnoreCase))
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

    private static string Key(string type, string name) => $"{type}/{name}".ToLowerInvariant();

    private static string Str(JsonNode? node, string property) =>
        node is JsonObject o && o.TryGetPropertyValue(property, out var value) && value?.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : "";

    /// <summary>
    /// Pre-deploy gate for the single-scope pipeline until multi-scope staged apply is wired
    /// (v2 plan 11.2): staging errors block, and so does any batch beyond the deployment's
    /// ambient scope - this pipeline approves and deploys exactly one scope, so a plan that
    /// stages more must never reach ARM. Without a target identity, explicit resource-group
    /// scope paths cannot be proven to hit the approved target and block unbound. Warnings
    /// never block.
    /// </summary>
    public static bool TryFindBlockingErrors(string templateJson, DeployScopeKind ambientScope, out string message) =>
        TryFindBlockingErrorsCore(templateJson, ambientScope, null, null, out message);

    /// <summary>
    /// RG-ambient gate bound to the approved (subscription, resource group) target: an explicit
    /// literal <c>scope</c> path must name exactly that target (ARM ids are case-insensitive),
    /// or the plan is rejected <c>ScopeTargetMismatch</c> - the approval artifact binds one
    /// exact scope, so an off-target policy/authorization path must never ride the approved
    /// token into a deployment at a different scope.
    /// </summary>
    public static bool TryFindBlockingErrors(string templateJson, string subscriptionId, string resourceGroup, out string message) =>
        TryFindBlockingErrorsCore(templateJson, DeployScopeKind.ResourceGroup, subscriptionId, resourceGroup, out message);

    private static bool TryFindBlockingErrorsCore(string templateJson, DeployScopeKind ambientScope, string? subscriptionId, string? resourceGroup, out string message)
    {
        var plan = Create(templateJson, ambientScope);
        var errors = plan.Diagnostics.Where(d => d.Severity == ArmDiagnosticSeverity.Error).ToList();
        var beyond = plan.Batches.Where(b => b.Kind != ambientScope).Select(b => b.Kind.ToString()).Distinct().ToList();
        if (beyond.Count > 0)
        {
            errors.Add(new(ArmDiagnosticSeverity.Error, "MultiScopeNotDeployable",
                $"The staged plan requires batches beyond the {ambientScope} scope ({string.Join(", ", beyond)}); this pipeline deploys exactly one scope - multi-scope staged apply is not wired yet."));
        }

        if (ambientScope == DeployScopeKind.ResourceGroup)
        {
            var bound = !string.IsNullOrWhiteSpace(subscriptionId) && !string.IsNullOrWhiteSpace(resourceGroup);
            foreach (var (key, scope) in ExplicitResourceGroupScopes(templateJson))
            {
                if (!bound)
                {
                    errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeTargetUnbound",
                        $"'{key}' carries an explicit resource-group scope '{scope}'; without a bound (subscription, resource group) target the gate cannot prove it matches what was approved."));
                }
                else if (!ScopeMatchesTarget(scope, subscriptionId!, resourceGroup!))
                {
                    errors.Add(new(ArmDiagnosticSeverity.Error, "ScopeTargetMismatch",
                        $"'{key}' carries an explicit scope '{scope}' outside the approved target /subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}."));
                }
            }
        }

        message = string.Join(Environment.NewLine, errors.Select(e => $"{e.Code}: {e.Message}"));
        return errors.Count > 0;
    }

    /// <summary>Literal <c>scope</c> paths the classifier reads as resource-group scope. Expression scopes are
    /// skipped (Create already failed the plan closed); empty scope inherits the ambient deployment target.</summary>
    private static IEnumerable<(string Key, string Scope)> ExplicitResourceGroupScopes(string templateJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(templateJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (root is not JsonObject template || template["resources"] is not JsonArray resources)
        {
            yield break;
        }

        foreach (var item in resources)
        {
            if (item is not JsonObject resource)
            {
                continue;
            }

            var scope = Str(resource, "scope");
            if (scope.Length == 0 || scope.Contains('[') || ScopeKindFromPath(scope) != DeployScopeKind.ResourceGroup)
            {
                continue;
            }

            yield return (Key(Str(resource, "type"), Str(resource, "name")), scope);
        }
    }

    private static bool ScopeMatchesTarget(string scope, string subscriptionId, string resourceGroup)
    {
        var segments = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sub = -1;
        var rg = -1;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (sub < 0)
            {
                if (segments[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
                {
                    sub = i;
                }
            }
            else if (segments[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                rg = i;
                break;
            }
        }

        return sub >= 0 && rg > sub
            && segments[sub + 1].Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)
            && segments[rg + 1].Equals(resourceGroup, StringComparison.OrdinalIgnoreCase);
    }
}
