using System;

namespace AzureDesignStudio.Core.Common
{
    /// <summary>Why an approved-plan run is or is not cleared to execute.</summary>
    public enum PlanGateReason
    {
        Cleared,
        NotApproved,
        NoApprover,
        MalformedBody,
        BodyEditedAfterApproval,
    }

    /// <summary>Gate verdict; <see cref="Detail"/> says what an operator must fix.</summary>
    public sealed record PlanGateResult(bool Cleared, PlanGateReason Reason, string Detail)
    {
        internal static PlanGateResult Allow() => new(true, PlanGateReason.Cleared, "stored body matches the approved plan hash");
        internal static PlanGateResult Deny(PlanGateReason reason, string detail) => new(false, reason, detail);
    }

    /// <summary>
    /// The choke-point every execution path must pass (v2 plan 11.3, gate [62]): a plan runs only
    /// when it is Approved, names an approver, and the stored body still hashes to the plan hash
    /// the approval was bound to. Anything else denies - the gate never executes on a best guess,
    /// and a byte edited in the stored body after approval is found here, not in Azure.
    /// Pure over record fields so both the Desktop host and the Server can call it without the
    /// Core library dragging in the Workspace/EF dependency.
    /// </summary>
    public static class PlanApprovalGate
    {
        public const string ApprovedStatus = "Approved";

        public static PlanGateResult Evaluate(string status, string planJson, string planHash, string? approvedBy)
        {
            if (!string.Equals(status, ApprovedStatus, StringComparison.Ordinal))
                return PlanGateResult.Deny(PlanGateReason.NotApproved, $"plan status is '{status}', only '{ApprovedStatus}' plans may run");
            if (string.IsNullOrWhiteSpace(approvedBy))
                return PlanGateResult.Deny(PlanGateReason.NoApprover, "approved plan carries no approver");
            if (string.IsNullOrWhiteSpace(planJson))
                return PlanGateResult.Deny(PlanGateReason.MalformedBody, "approved plan carries no body");
            try
            {
                _ = DeployPlanArtifact.Canonicalize(planJson);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException)
            {
                return PlanGateResult.Deny(PlanGateReason.MalformedBody, "stored plan body is not valid JSON");
            }
            if (!DeployPlanArtifact.Verify(planJson, planHash))
                return PlanGateResult.Deny(PlanGateReason.BodyEditedAfterApproval, "stored plan body no longer matches the approved hash - re-approve the edited plan");
            return PlanGateResult.Allow();
        }
    }
}
