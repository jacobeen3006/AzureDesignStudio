using AzureDesignStudio.Core.Common;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 11.3 gate [62] - a plan runs only if approved, approver-named, and the stored body still hashes to the approved hash.</summary>
public class PlanApprovalGateTests
{
    private static readonly DeployPlanArtifact Plan = DeployPlanArtifact.Create(
        "sub-1", "rg1", """{"resources":[{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"}]}""");

    [Fact]
    public void Approved_UneditedPlanWithApprover_IsCleared()
    {
        var result = PlanApprovalGate.Evaluate("Approved", Plan.CanonicalJson, Plan.PlanHash, "jakub");

        Assert.True(result.Cleared);
        Assert.Equal(PlanGateReason.Cleared, result.Reason);
    }

    [Fact]
    public void DraftOrPending_CannotRun()
    {
        foreach (var status in new[] { "Draft", "Pending", "approved" }) // exact match only; lowercase 'approved' is not the status
        {
            var result = PlanApprovalGate.Evaluate(status, Plan.CanonicalJson, Plan.PlanHash, "jakub");
            Assert.False(result.Cleared);
            Assert.Equal(PlanGateReason.NotApproved, result.Reason);
        }
    }

    [Fact]
    public void ApprovedWithoutApprover_CannotRun()
    {
        var result = PlanApprovalGate.Evaluate("Approved", Plan.CanonicalJson, Plan.PlanHash, "  ");

        Assert.False(result.Cleared);
        Assert.Equal(PlanGateReason.NoApprover, result.Reason);
    }

    [Fact]
    public void BodyEditedAfterApproval_IsDeniedNotSilentlyRerun()
    {
        var tampered = Plan.CanonicalJson.Replace("storageAccounts", "webSites");

        var result = PlanApprovalGate.Evaluate("Approved", tampered, Plan.PlanHash, "jakub");

        Assert.False(result.Cleared);
        Assert.Equal(PlanGateReason.BodyEditedAfterApproval, result.Reason);
        Assert.Contains("re-approve", result.Detail);
    }

    [Fact]
    public void MalformedOrMissingBody_IsDenied()
    {
        foreach (var body in new[] { "", "   ", "not json" })
        {
            var result = PlanApprovalGate.Evaluate("Approved", body, Plan.PlanHash, "jakub");
            Assert.False(result.Cleared);
            Assert.Equal(PlanGateReason.MalformedBody, result.Reason);
        }
    }
}
