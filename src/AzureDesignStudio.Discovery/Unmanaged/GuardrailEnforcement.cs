using System.Text.Json;
using AzureDesignStudio.Discovery.Iac.WhatIf;

namespace AzureDesignStudio.Discovery.Unmanaged;

/// <summary>One resource a deploy plan would touch, in guardrail terms (resolved ARM id + type + tags).</summary>
public sealed record GuardrailTarget(string ResourceId, string Type, IReadOnlyDictionary<string, string> Tags);

/// <summary>A planned-touch that a guardrail protects: the resource and the matched pattern.</summary>
public sealed record GuardrailFinding(string ResourceId, string Reason);

/// <summary>
/// Guardrails on the deploy path (v2 plan 13.1/[36-final]): every resource a what-if plan touches
/// is judged against the do-not-touch list before anything can be approved or applied. The what-if
/// rows carry ARM-resolved ids and the full before/after payloads, so the match runs on truth -
/// the same rules the scan audit uses, never a client-side claim.
/// </summary>
public static class GuardrailEnforcement
{
    /// <summary>Guardrail view of a what-if row: ARM-resolved id, type derived from the id, tags from
    /// the pre-change payload (falls back to post-change for Creates). Rows without a resolved id
    /// carry nothing to match; the indeterminacy gate already refuses those plans.</summary>
    public static GuardrailTarget? FromWhatIfChange(WhatIfChange change)
    {
        if (string.IsNullOrWhiteSpace(change.ResourceId)) return null;
        var tags = ReadTags(change.BeforeJson) ?? ReadTags(change.AfterJson) ?? EmptyTags;
        return new GuardrailTarget(change.ResourceId, TypeFromArmId(change.ResourceId), tags);
    }

    /// <summary>Matchable targets for a whole summary, in input order (token binding).</summary>
    public static IReadOnlyList<GuardrailTarget> Targets(IEnumerable<WhatIfChange> changes)
    {
        var list = new List<GuardrailTarget>();
        foreach (var change in changes)
            if (FromWhatIfChange(change) is { } target)
                list.Add(target);
        return list;
    }

    /// <summary>Findings for a plan summary: protected resources, ordinal-sorted by id, one per id.</summary>
    public static IReadOnlyList<GuardrailFinding> Evaluate(GuardrailPolicy policy, IEnumerable<WhatIfChange> changes) =>
        EvaluateTargets(policy, Targets(changes));

    /// <summary>Findings for already-bound targets (the apply path re-evaluates the stored set).</summary>
    public static IReadOnlyList<GuardrailFinding> EvaluateTargets(GuardrailPolicy policy, IEnumerable<GuardrailTarget> targets)
    {
        var findings = new Dictionary<string, GuardrailFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (findings.ContainsKey(target.ResourceId)) continue;
            if (policy.ProtectionReasonFor(target.ResourceId, target.Type, target.Tags) is { } reason)
                findings[target.ResourceId] = new GuardrailFinding(target.ResourceId, reason);
        }

        return findings.Values.OrderBy(f => f.ResourceId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// ARM type from a resource id: after the LAST /providers/ (nested-scope ids chain providers),
    /// the segments are <c>namespace / type / name / childType / childName…</c>, so the type is the
    /// first two segments plus every child type at odd depth (e.g.
    /// <c>Microsoft.Compute/virtualMachines/extensions</c>). Unrecognized shapes yield "" - which
    /// matches no non-empty pattern and so can never falsely protect.
    /// </summary>
    public static string TypeFromArmId(string id)
    {
        var marker = id.LastIndexOf("/providers/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return "";
        var segments = id[(marker + "/providers/".Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return "";
        var type = segments[0] + "/" + segments[1];
        for (var i = 3; i < segments.Length; i += 2)
            type += "/" + segments[i];
        return type;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resource tags from a what-if payload: the ARM resource sits at the root, or wrapped
    /// under <c>content</c> in some api shapes. Malformed payloads read as no tags (id and type
    /// still judge scope and type rules); non-string tag values are not tag semantics and skip.</summary>
    private static IReadOnlyDictionary<string, string>? ReadTags(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var source = root;
            if (!root.TryGetProperty("tags", out _)
                && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
                source = content;
            if (!source.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return null;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags.EnumerateObject())
                if (tag.Value.ValueKind == JsonValueKind.String && tag.Value.GetString() is { } value)
                    dict[tag.Name] = value;
            return dict;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
