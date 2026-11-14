using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AzureDesignStudio.Services.Models
{
    /// <summary>How a tenant's own guardrail policy relates to the inherited default (the NULL-tenant
    /// row). <c>replace</c> is the default (D-05): the tenant's policy is the whole list. <c>merge</c>
    /// judges a plan against the union of both; it is selected per tenant by an administrator.</summary>
    public static class GuardrailPolicyModes
    {
        public const string Replace = "replace";
        public const string Merge = "merge";

        /// <summary>Normalizes a mode text (trimmed, case-insensitive). False for anything else - an
        /// unknown mode is rejected, never guessed.</summary>
        public static bool TryNormalize(string? value, out string mode)
        {
            var text = value?.Trim();
            if (string.Equals(text, Replace, StringComparison.OrdinalIgnoreCase)) { mode = Replace; return true; }
            if (string.Equals(text, Merge, StringComparison.OrdinalIgnoreCase)) { mode = Merge; return true; }
            mode = Replace;
            return false;
        }
    }

    /// <summary>Event types written to the append-only security event table.</summary>
    public static class SecurityEventTypes
    {
        public const string GuardrailPolicyChanged = "GuardrailPolicyChanged";
        public const string SelfApprovalChanged = "SelfApprovalChanged";
        public const string PolicyModeChanged = "PolicyModeChanged";
    }

    /// <summary>Per-tenant security settings (one row per scope). A scope without a row runs on the
    /// defaults: self-approval off, guardrail policy mode <c>replace</c>.</summary>
    public record TenantSettingsModel
    {
        /// <summary>The scope key (the tenant id of an organizational token, the user's object id for a
        /// personal account). Supplied by the service, never generated.</summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Guid TenantId { get; set; }

        public bool AllowSelfApproval { get; set; }

        /// <summary>See <see cref="GuardrailPolicyModes"/>.</summary>
        [StringLength(16)]
        public string GuardrailPolicyMode { get; set; } = GuardrailPolicyModes.Replace;

        [StringLength(320)]
        public string UpdatedBy { get; set; } = "";

        [StringLength(64)]
        public string UpdatedByUserId { get; set; } = "";

        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>Append-only record of a security-relevant change (guardrail policy, self-approval,
    /// policy mode). Events are never updated or deleted, like the decommission event stream; a failed
    /// audit write is logged critical, never swallowed.</summary>
    public record SecurityEventModel
    {
        [Key]
        public Guid Id { get; set; }

        /// <summary>The scope the change applies to.</summary>
        public Guid TenantId { get; set; }

        /// <summary>See <see cref="SecurityEventTypes"/>.</summary>
        [StringLength(32, MinimumLength = 1)]
        public string EventType { get; set; } = null!;

        [StringLength(320)]
        public string Principal { get; set; } = "";

        [StringLength(64)]
        public string PrincipalUserId { get; set; } = "";

        /// <summary>What changed (old and new values, never a secret), truncated to 1024.</summary>
        [StringLength(1024)]
        public string? Detail { get; set; }

        public DateTime AtUtc { get; set; }
    }
}
