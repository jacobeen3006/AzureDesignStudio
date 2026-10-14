using AzureDesignStudio.Discovery.Iac.WhatIf;

namespace AzureDesignStudio.Discovery.Iac;

/// <summary>
/// Folds the deployment family a staged apply creates (<c>ads-deployment</c>, or
/// <c>ads-deployment-b1..bn</c>) into one monitor row: worst state wins, so a plan that is
/// still running somewhere never reads as done and one failed batch never hides behind
/// siblings that succeeded. Name matching is anchored so a foreign deployment whose name
/// merely starts with the prefix is never folded in.
/// </summary>
public static class DeploymentStatusFolding
{
    /// <summary>True for the single-scope deployment and every staged batch deployment.</summary>
    public static bool IsAdsDeployment(string? name) =>
        !string.IsNullOrEmpty(name)
        && (name.Equals(WhatIfClient.DeploymentName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(WhatIfClient.DeploymentName + "-b", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ARM provisioning-state fold, worst state wins: Failed &gt; Running &gt; Canceled &gt; Succeeded.
    /// Any state ARM may still move (Accepted/Creating/Running/Updating or an unrecognized
    /// token) keeps the family in Running - an unproven state is never read as success.
    /// Empty input folds to empty, never to a state.
    /// </summary>
    public static string Fold(IEnumerable<string?> provisioningStates)
    {
        var seen = false;
        var failed = false;
        var running = false;
        var canceled = false;
        foreach (var raw in provisioningStates)
        {
            var state = raw?.Trim() ?? "";
            if (state.Length == 0)
            {
                continue;
            }

            seen = true;
            if (state.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            {
                failed = true;
            }
            else if (state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
            {
            }
            else if (state.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
            {
                canceled = true;
            }
            else
            {
                running = true;
            }
        }

        return !seen ? "" : failed ? "Failed" : running ? "Running" : canceled ? "Canceled" : "Succeeded";
    }
}
