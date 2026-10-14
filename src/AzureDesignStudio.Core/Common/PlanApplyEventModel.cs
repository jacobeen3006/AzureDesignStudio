using System.ComponentModel.DataAnnotations;

namespace AzureDesignStudio.Core.Common;

/// <summary>
/// Append-only apply audit event (v2 plan [62]/11.3). One apply correlates by ApplyId:
/// Started (canonical plan body + consumed what-if preview evidence, written before any
/// ARM mutation) is followed by exactly one true terminal event - Completed (ARM reported
/// provisioningState=Succeeded) or Failed (ARM refused the submission, or provisioning
/// finished failed/partial). Transport failure is not a provisioning outcome: when the
/// client stream is lost mid-deployment a non-terminal TransportLost marker lands and ARM
/// polling continues for the real outcome; OutcomeUnknown is recorded only when terminal
/// truth was genuinely unreachable. A failed start never claims "applied"; events are
/// never updated or deleted. Lives in Core so the persisted-column contract is testable
/// offline.
/// </summary>
public record PlanApplyEventModel
{
    /// <summary>Approval consumed; ARM mutation about to begin.</summary>
    public const string Started = "Started";

    /// <summary>True terminal: deployment finished with provisioningState=Succeeded.</summary>
    public const string Completed = "Completed";

    /// <summary>True terminal: ARM refused the submission, or provisioning finished failed/partial.</summary>
    public const string Failed = "Failed";

    /// <summary>Non-terminal: client stream lost mid-deployment; ARM polling continues for the real outcome.</summary>
    public const string TransportLost = "TransportLost";

    /// <summary>Terminal without truth: submission was accepted but the real outcome became unconfirmable.</summary>
    public const string OutcomeUnknown = "OutcomeUnknown";

    /// <summary>Max length of a persisted event type; every constant must fit the column contract.</summary>
    public const int EventTypeCap = 16;

    /// <summary>Detail cap; longer values are truncated with a marker.</summary>
    public const int DetailCap = 1024;

    private const string TruncationMarker = "…[trunc]";

    public static string? Truncate(string? detail) =>
        detail is { Length: <= DetailCap } ? detail : detail is not null ? detail[..(DetailCap - TruncationMarker.Length)] + TruncationMarker : null;

    public int Id { get; set; }

    /// <summary>Correlates all events belonging to one apply.</summary>
    public Guid ApplyId { get; set; }

    [StringLength(71, MinimumLength = 71)]
    public string PlanHash { get; set; } = null!;

    /// <summary>Canonical approved plan body; every event row can reconstruct the applied plan.</summary>
    [Required]
    public string PlanJson { get; set; } = null!;

    [StringLength(64, MinimumLength = 1)]
    public string SubscriptionId { get; set; } = null!;

    [StringLength(90, MinimumLength = 1)]
    public string ResourceGroupName { get; set; } = null!;

    [StringLength(64, MinimumLength = 1)]
    public string DeploymentName { get; set; } = null!;

    [StringLength(320, MinimumLength = 1)]
    public string ApprovedBy { get; set; } = null!;

    [StringLength(64, MinimumLength = 36)]
    public string ApprovedByUserId { get; set; } = null!;

    public DateTime ApprovedAtUtc { get; set; }

    /// <summary>SHA-256 hex of the consumed what-if token: proof the apply rode on a live preview (Started rows).</summary>
    [StringLength(64)]
    public string? PreviewEvidence { get; set; }

    [StringLength(EventTypeCap, MinimumLength = 6)]
    public string EventType { get; set; } = null!;

    /// <summary>Terminal context (provisioningState / error excerpt), truncated to DetailCap.</summary>
    [StringLength(DetailCap)]
    public string? Detail { get; set; }

    public DateTime TimestampUtc { get; set; }
}
