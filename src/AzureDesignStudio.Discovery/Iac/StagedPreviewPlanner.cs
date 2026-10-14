namespace AzureDesignStudio.Discovery.Iac;

/// <summary>
/// What ARM can honestly preview for a staged plan, plus the caveat the approval must carry.
/// </summary>
public sealed record StagedPreview(string TemplateJson, string ParametersJson, string? Caveat, bool Unsupported = false);

/// <summary>
/// A staged plan is previewed through the batch that lands at the approved target scope: ARM
/// what-if runs the template as a deployment at that scope, so the target-scope batch is the one
/// the preview really covers. The plan hash and token still bind the full approved payload, while
/// the review modal states plainly that the other batches deploy without a preview. A plan whose
/// batches none rides the target endpoint cannot be previewed at all - and an unpreviewed plan can
/// never earn the token its deploy requires, so it stays undeployable through this pipeline.
/// </summary>
public static class StagedPreviewPlanner
{
    public const string UnsupportedCode = "STAGED_PREVIEW_UNSUPPORTED";

    /// <summary>Target-bound selection (v2 plan 11.2): the previewed batch is the one whose kind
    /// the approved target names; a single-batch plan previews the whole payload as-is.</summary>
    public static StagedPreview Select(DeployScopePlan plan, DeployTargetScope target, string fullTemplateJson, string parametersJson)
    {
        var targetBatch = plan.Batches.FirstOrDefault(b => b.Kind == target.Kind);
        if (targetBatch is null && plan.Batches.Count > 0)
        {
            return new(fullTemplateJson, parametersJson,
                $"{UnsupportedCode}: this plan deploys only {string.Join(", ", plan.Batches.Select(b => b.Kind))}-scope batches; none lands at the approved target {target.ScopePath}, so the plan cannot be previewed - and no preview means no approval and no deploy.",
                Unsupported: true);
        }

        if (targetBatch is null || plan.Batches.Count == 1)
        {
            return new(fullTemplateJson, parametersJson, null);
        }

        var others = string.Join(", ", plan.Batches.Where(b => b != targetBatch).Select(b => b.Kind));
        return new(targetBatch.TemplateJson, DeployScopePlanner.ParametersForBatch(targetBatch.TemplateJson, parametersJson),
            $"Staged apply: this plan deploys {plan.Batches.Count} scope batches ({string.Join(", ", plan.Batches.Select(b => b.Kind))}); ARM what-if previews the {target.Kind}-scope batch at the approved target - the remaining batches ({others}) deploy under the same approval without a preview.");
    }

    /// <summary>Legacy resource-group-target selection; behavior and wording unchanged.</summary>
    public static StagedPreview Select(DeployScopePlan plan, string fullTemplateJson, string parametersJson)
    {
        var rgBatch = plan.Batches.FirstOrDefault(b => b.Kind == DeployScopeKind.ResourceGroup);
        if (rgBatch is null && plan.Batches.Count > 0)
        {
            return new(fullTemplateJson, parametersJson,
                $"{UnsupportedCode}: this plan deploys only {plan.Batches[0].Kind}-scope batches; ARM what-if exists at resource-group scope only, so the plan cannot be previewed - and no preview means no approval and no deploy.",
                Unsupported: true);
        }

        if (rgBatch is null || plan.Batches.Count == 1)
        {
            return new(fullTemplateJson, parametersJson, null);
        }

        var kinds = string.Join(", ", plan.Batches.Select(b => b.Kind.ToString()));
        return new(rgBatch.TemplateJson, DeployScopePlanner.ParametersForBatch(rgBatch.TemplateJson, parametersJson),
            $"Staged apply: this plan deploys {plan.Batches.Count} scope batches ({kinds}); ARM what-if previews only the ResourceGroup batch - the remaining batches deploy under the same approval without a preview.");
    }
}
