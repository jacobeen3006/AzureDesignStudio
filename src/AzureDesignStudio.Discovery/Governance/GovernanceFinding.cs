namespace AzureDesignStudio.Discovery.Governance;

/// <summary>The Azure service a governance finding came from (Wave B3).</summary>
public enum GovernanceSource { Advisor, Policy, Defender }

public enum GovernanceSeverity { Low, Medium, High }

/// <summary>
/// One governance finding about one resource, normalized from a Resource Graph row by
/// <see cref="GovernanceNormalizer"/>: an open Advisor recommendation, a non-compliant policy
/// state or an unhealthy Defender for Cloud assessment. <see cref="RecordId"/> is the row's id
/// (the recommendation, policy state or assessment); <see cref="ResourceId"/> is the ARM id of
/// the resource the finding is about, matched case-insensitively.
/// </summary>
public sealed record GovernanceFinding(GovernanceSource Source, string RecordId, string ResourceId, string SubscriptionId,
    GovernanceSeverity Severity, string Category, string Title, string? Detail, string? PolicyAssignmentId, string? PolicyDefinitionId);

/// <summary>One Defender for Cloud plan of a subscription (<c>microsoft.security/pricings</c>):
/// <see cref="Plan"/> is the pricing name (VirtualMachines, CloudPosture, ...), <see cref="Tier"/>
/// Free or Standard.</summary>
public sealed record DefenderPlan(string SubscriptionId, string Plan, string Tier);

/// <summary>The governance sources a scan can collect, by their stored names.</summary>
public static class GovernanceSources
{
    public const string Advisor = "advisor", Policy = "policy", Defender = "defender";

    public static IReadOnlyList<string> All { get; } = [Advisor, Policy, Defender];
}

/// <summary>What governance findings cannot tell, shown wherever they are.</summary>
public static class GovernanceCaveats
{
    public static IReadOnlyList<string> All { get; } =
    [
        "Resource Graph hides subscriptions and resources the scanning identity cannot read, so zero findings is not proof of none.",
        "Policy compliance lags changes by about 15 minutes and full evaluation runs every 24 hours.",
        "Defender recommendations depend on which plans are on.",
        "Advisor recommendations refresh on Advisor's schedule, and dismissed or postponed recommendations may still be listed when Resource Graph does not carry their status.",
    ];
}
