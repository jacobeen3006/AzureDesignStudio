using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 11.2 - staged apply execution: the planner's batch order is the deployment;
/// a failed or unconfirmable batch stops the pipeline before anything later is submitted.
/// </summary>
public class StagedBatchRunnerTests
{
    private static DeployScopeBatch Batch(DeployScopeKind kind) => new(kind, "{}", ["key"]);

    private static Task<StagedBatchOutcome> Completed() =>
        Task.FromResult(new StagedBatchOutcome(StagedBatchResult.Completed));

    [Fact]
    public async Task RunsEveryBatchInPlannerOrder()
    {
        var batches = new[] { Batch(DeployScopeKind.Subscription), Batch(DeployScopeKind.ResourceGroup) };
        var order = new List<DeployScopeKind>();
        var started = new List<int>();
        var result = await StagedBatchRunner.RunAsync(batches,
            (batch, _) => { order.Add(batch.Kind); return Completed(); },
            (_, index) => { started.Add(index); return Task.CompletedTask; });

        Assert.Equal([DeployScopeKind.Subscription, DeployScopeKind.ResourceGroup], order);
        Assert.Equal([0, 1], started);
        Assert.Equal(StagedBatchResult.Completed, result.Result);
        Assert.Equal(2, result.BatchesRun);
        Assert.Equal(-1, result.FailedBatchIndex);
    }

    [Fact]
    public async Task FailedBatch_StopsBeforeTheNextBatchIsSubmitted()
    {
        var batches = new[] { Batch(DeployScopeKind.Subscription), Batch(DeployScopeKind.ResourceGroup) };
        var executed = 0;
        var result = await StagedBatchRunner.RunAsync(batches, (_, index) =>
        {
            executed++;
            return Task.FromResult(index == 0
                ? new StagedBatchOutcome(StagedBatchResult.Failed, "ARM said no")
                : new StagedBatchOutcome(StagedBatchResult.Completed));
        });

        Assert.Equal(StagedBatchResult.Failed, result.Result);
        Assert.Equal(0, result.FailedBatchIndex);
        Assert.Equal("ARM said no", result.Detail);
        Assert.Equal(1, executed);
        Assert.Equal(1, result.BatchesRun);
    }

    [Fact]
    public async Task UnconfirmableOutcome_NeverContinuesThePipeline()
    {
        // A lost connection during polling means the deployment may still be running;
        // continuing to the next scope batch could mutate under an unknown parent state.
        var batches = new[] { Batch(DeployScopeKind.Subscription), Batch(DeployScopeKind.ResourceGroup) };
        var executed = 0;
        var result = await StagedBatchRunner.RunAsync(batches, (_, _) =>
        {
            executed++;
            return Task.FromResult(new StagedBatchOutcome(StagedBatchResult.OutcomeUnknown, "transport lost"));
        });

        Assert.Equal(StagedBatchResult.OutcomeUnknown, result.Result);
        Assert.Equal(0, result.FailedBatchIndex);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task SingleBatchRun_PassesItsOutcomeThrough()
    {
        var result = await StagedBatchRunner.RunAsync([Batch(DeployScopeKind.ResourceGroup)],
            (_, _) => Completed());

        Assert.Equal(StagedBatchResult.Completed, result.Result);
        Assert.Equal(-1, result.FailedBatchIndex);
        Assert.Equal(1, result.BatchesRun);
    }
}
