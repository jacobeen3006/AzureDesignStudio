using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>Outcome of narrowing a deployment template down to failed resources.</summary>
/// <param name="TemplateJson">Filtered template (only matched resources); null when <see cref="Error"/> is set.</param>
/// <param name="MatchedKeys">Normalized <c>type/name</c> keys actually kept from the template.</param>
/// <param name="Warnings">Honest disclosures: unmatched keys, pruned dependsOn entries, copy loops.</param>
/// <param name="Error">Why no subset could be proven; retry stays blocked until resolved.</param>
public sealed record RetryFilterResult(
    string? TemplateJson,
    IReadOnlyList<string> MatchedKeys,
    IReadOnlyList<string> Warnings,
    string? Error);

/// <summary>
/// Targeted retry (v2 plan 11.2/[56]): narrows an exported template to the resources the monitor
/// reported as failed/canceled, so a retry re-applies exactly the failed subset. Matching is by
/// the monitor's <c>type/name</c> key (ARM's deployment-operations resourceType/resourceName,
/// case-insensitive), which is 1:1 with template members. The subset still runs through the full
/// what-if → approval → apply gate like any other deployment: this filter bypasses nothing, it
/// only builds the candidate body. Fail-closed: an unparseable template or a key set matching
/// nothing yields an error, never a widened fallback.
/// </summary>
public static class RetryTemplateFilter
{
    /// <summary>Normalizes a monitor row key (<c>type/name</c>) for template matching.</summary>
    public static string NormalizeKey(string type, string name) => $"{type}/{name}".ToLowerInvariant();

    /// <summary>Builds the retry subset template from failed monitor keys. Pure: touches nothing else.</summary>
    public static RetryFilterResult Filter(string templateJson, IEnumerable<string> failedKeys)
    {
        var keys = new HashSet<string>(
            failedKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return new(null, [], [], "No failed resources were selected for retry.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(templateJson);
        }
        catch (JsonException)
        {
            return new(null, [], [], "InvalidTemplate: the deployment template is not valid JSON.");
        }

        if (root is not JsonObject template)
        {
            return new(null, [], [], "InvalidTemplate: the deployment template is not a JSON object.");
        }

        if (template["resources"] is not JsonArray resources)
        {
            return new(null, [], [], "InvalidTemplate: the deployment template has no resources array.");
        }

        // Validate every member first (same discipline as DeployScopePlanner): a template we
        // cannot fully classify must not produce a partial subset.
        var members = new List<(JsonObject Resource, string Key, string Name)>();
        foreach (var member in resources)
        {
            if (member is not JsonObject resource
                || resource["type"]?.GetValueKind() != JsonValueKind.String
                || resource["name"]?.GetValueKind() != JsonValueKind.String)
            {
                return new(null, [], [], "InvalidTemplate: a resource member has no string type and name.");
            }

            var type = resource["type"]!.GetValue<string>();
            var name = resource["name"]!.GetValue<string>();
            members.Add((resource, NormalizeKey(type, name), name));
        }

        var matched = members.Where(m => keys.Contains(m.Key)).ToList();
        if (matched.Count == 0)
        {
            return new(null, [], [],
                "None of the failed resources appear in this template; the retry subset is empty.");
        }

        var warnings = new List<string>();
        foreach (var missing in keys.Where(k => !members.Any(m => m.Key == k)))
        {
            warnings.Add($"'{missing}' was not found in the template (it may belong to another deployment).");
        }

        var retained = new JsonArray();
        var retainedKeys = new HashSet<string>(matched.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
        var retainedNames = new HashSet<string>(matched.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var prunedAny = matched.Count != members.Count;
        foreach (var (resource, key, _) in matched)
        {
            // Detached copy: a retained node cannot stay a child of the original resources array.
            var subset = resource.DeepClone().AsObject();
            if (prunedAny && subset["dependsOn"] is JsonArray dependsOn)
            {
                var kept = new JsonArray();
                foreach (var entry in dependsOn)
                {
                    var raw = entry?.GetValueKind() == JsonValueKind.String ? entry!.GetValue<string>() : "";
                    var literal = DeployScopePlanner.ParseLiteralResourceId(raw);
                    if (raw.Length == 0)
                    {
                        warnings.Add($"'{key}' dependsOn entry is not a resource reference; dropped from the subset.");
                    }
                    else if (literal is not null)
                    {
                        // Same literal resourceId('type','name') parse as DeployScopePlanner: it
                        // resolves to the canonical key the monitor rows use. A retained edge
                        // keeps the raw expression verbatim so ARM evaluates it identically.
                        if (retainedKeys.Contains(DeployScopePlanner.Key(literal.Value.Type, literal.Value.Name)))
                        {
                            kept.Add(raw);
                        }
                        else
                        {
                            warnings.Add($"'{key}' dependsOn '{raw}' was pruned (outside the retry subset).");
                        }
                    }
                    else if (!raw.Contains('['))
                    {
                        if (retainedNames.Contains(raw))
                        {
                            kept.Add(raw);
                        }
                        else
                        {
                            // A dependsOn naming a pruned resource would fail ARM validation
                            // outright; the dependency already exists in Azure from the first apply.
                            warnings.Add($"'{key}' dependsOn '{raw}' was pruned (outside the retry subset).");
                        }
                    }
                    else
                    {
                        // Unresolvable expression: keep it - dropping it would silently remove
                        // deployment ordering - and disclose that ARM stays the judge.
                        kept.Add(raw);
                        warnings.Add($"'{key}' dependsOn '{raw}' cannot be statically verified; ARM will reject the retry if it points outside the subset.");
                    }
                }

                subset["dependsOn"] = kept;
            }

            if (subset["copy"] is not null)
            {
                warnings.Add($"'{key}' carries a copy loop; the retry runs its full iteration count.");
            }

            retained.Add(subset);
        }

        template["resources"] = retained;
        return new(template.ToJsonString(), matched.Select(m => m.Key).ToList(), warnings, null);
    }
}
