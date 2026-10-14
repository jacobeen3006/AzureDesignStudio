namespace AzureDesignStudio.Discovery.Iac;

/// <summary>
/// What ARM can honestly preview for a staged plan, plus the caveat the approval must carry.
/// </summary>
public sealed record StagedPreview(string TemplateJson, string ParametersJson, string? Caveat, bool Unsupported = false);

/// <summary>
/// ARM what-if exists only at resource-group scope, so a staged plan is previewed through its
/// resource-group batch: the plan hash and token still bind the full approved payload, while the
/// review modal states plainly that the other batches deploy without a preview. A plan with no
/// resource-group batch cannot be previewed at all - and an unpreviewed plan can never earn the
/// token its deploy requires, so it stays undeployable through this pipeline.
/// </summary>
public static class StagedPreviewPlanner
{
    public const string UnsupportedCode = "STAGED_PREVIEW_UNSUPPORTED";

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
