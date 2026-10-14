namespace AzureDesignStudio.Discovery.Iac;

/// <summary>Terminal state of one staged batch as observed from its own deployment.</summary>
public enum StagedBatchResult
{
    Completed,
    Failed,
    OutcomeUnknown,
}

/// <summary>Outcome of a single staged batch; <paramref name="Detail"/> carries ARM's own wording.</summary>
public sealed record StagedBatchOutcome(StagedBatchResult Result, string? Detail = null);

/// <summary>
/// Result of a staged run: which batch ended it (<see cref="FailedBatchIndex"/> is -1 when every
/// batch completed) and how many were run - so callers can say exactly where the plan stopped.
/// </summary>
public sealed record StagedRunResult(StagedBatchResult Result, int BatchesRun, int TotalBatches, int FailedBatchIndex, string? Detail);

/// <summary>
/// Ordered execution of a staged plan (v2 plan 11.2): the planner's batch order is the deployment -
/// batch i+1 starts only after batch i completed successfully. A failed batch stops the run, and so
/// does an unconfirmable outcome: continuing past ARM ambiguity would push resources into a scope
/// whose predecessor state is unknown. The batches that ran are the deployment; nothing reorders.
/// </summary>
public static class StagedBatchRunner
{
    public static async Task<StagedRunResult> RunAsync(
        IReadOnlyList<DeployScopeBatch> batches,
        Func<DeployScopeBatch, int, Task<StagedBatchOutcome>> deployBatch,
        Func<DeployScopeBatch, int, Task>? onBatchStarted = null)
    {
        for (var i = 0; i < batches.Count; i++)
        {
            if (onBatchStarted is not null)
            {
                await onBatchStarted(batches[i], i);
            }

            var outcome = await deployBatch(batches[i], i);
            if (outcome.Result != StagedBatchResult.Completed)
            {
                return new StagedRunResult(outcome.Result, i + 1, batches.Count, i, outcome.Detail);
            }
        }

        return new StagedRunResult(StagedBatchResult.Completed, batches.Count, batches.Count, -1, null);
    }
}
