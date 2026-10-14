using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using AzureDesignStudio.Core.Common;

namespace AzureDesignStudio.Server.Models
{
    public class DesignDbContext : DbContext
    {
        public DesignDbContext(DbContextOptions options) : base(options)
        {

        }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DesignModel>()
                .ToTable(t => t.HasCheckConstraint("ADS_CHECK_JSON", "ISJSON(DesignData)=1"));
            modelBuilder.Entity<PlanApplyEventModel>().HasIndex(e => e.ApplyId);
            modelBuilder.Entity<DecomWindowEventModel>().HasIndex(e => e.ApplyId);
        }

        public DbSet<DesignModel> AdsDesigns { get; set; } = null!;
        public DbSet<AzureSubscriptionModel> AzureSubscriptions { get; set; } = null!;
        public DbSet<PlanApprovalModel> AdsPlanApprovals { get; set; } = null!;
        public DbSet<PlanApplyEventModel> AdsPlanApplyEvents { get; set; } = null!;
        public DbSet<GuardrailPolicyModel> AdsGuardrailPolicies { get; set; } = null!;
        public DbSet<DecomApprovalModel> AdsDecomApprovals { get; set; } = null!;
        public DbSet<DecomWindowEventModel> AdsDecomWindowEvents { get; set; } = null!;
    }

    public record DesignModel
    {
        public int Id { get; set; }
        [StringLength(200, MinimumLength = 1)]
        public string Name { get; set; } = null!;
        public Guid UserId { get; set; }
        public string DesignData { get; set; } = null!;
        public DateTime LastModified { get; set; }
    }

    public record AzureSubscriptionModel
    {
        public int Id { get; set; }
        public Guid UserId { get; set; }
        public Guid SubscriptionId { get; set; }
        [StringLength(64, MinimumLength = 1)]
        public string SubscriptionName { get; set; } = null!;
        public Guid TenantId { get; set; }
        public Guid ClientId { get; set; }
        public string ClientSecret { get; set; } = null!;
    }

    public record PlanApprovalModel
    {
        /// <summary>DeployPlanArtifact hash ("sha256:" + 64 hex) the approval is bound to.</summary>
        [Key]
        [StringLength(71, MinimumLength = 71)]
        public string PlanHash { get; set; } = null!;

        /// <summary>Canonical plan body stored at approval time; re-verified against the hash before apply.</summary>
        [Required]
        public string PlanJson { get; set; } = null!;

        [StringLength(20, MinimumLength = 1)]
        public string Status { get; set; } = "Approved";

        /// <summary>Authenticated principal that approved (display name + oid) - never client-supplied.</summary>
        [StringLength(320, MinimumLength = 1)]
        public string ApprovedBy { get; set; } = null!;

        [StringLength(64, MinimumLength = 36)]
        public string ApprovedByUserId { get; set; } = null!;

        public DateTime ApprovedAtUtc { get; set; }

        /// <summary>Pre-deploy snapshot + undo entries derived from the live preview (v2 11.4);
        /// persisted with the approval so the record carries exactly what existed before apply.</summary>
        public string? RevertPlanJson { get; set; }
    }

    /// <summary>Server-side do-not-touch list (v2 13.1/[36-final]): one row (Id=1), the policy every
    /// review and apply is judged against. Written only through SetGuardrailPolicy by an
    /// authenticated principal; deploys never consult a client-supplied copy.</summary>
    public record GuardrailPolicyModel
    {
        [Key]
        public int Id { get; set; }

        /// <summary>GuardrailPolicy.FromJson shape (scopes/tags/types). Validated on write.</summary>
        [Required]
        public string Json { get; set; } = "";

        [StringLength(320)]
        public string UpdatedBy { get; set; } = "";

        [StringLength(64)]
        public string UpdatedByUserId { get; set; } = "";

        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>Server-side approval of a decommission-window execution (v2 13.5). Bound to the
    /// execution hash (the exact lock set), not a what-if token - a window lock is not a
    /// deployment. Consumed by ExecuteDecomWindow; one execute per approval.</summary>
    public record DecomApprovalModel
    {
        /// <summary>DecomExecution.ExecutionHash ("sha256:" + 64 hex) the approval is bound to.</summary>
        [Key]
        [StringLength(71, MinimumLength = 71)]
        public string ExecutionHash { get; set; } = null!;

        [StringLength(64, MinimumLength = 1)]
        public string WindowId { get; set; } = null!;

        [StringLength(71, MinimumLength = 71)]
        public string PlanHash { get; set; } = null!;

        [StringLength(64, MinimumLength = 1)]
        public string SubscriptionId { get; set; } = null!;

        /// <summary>Authenticated principal that approved (display name + oid) - never client-supplied.</summary>
        [StringLength(320, MinimumLength = 1)]
        public string ApprovedBy { get; set; } = null!;

        [StringLength(64, MinimumLength = 36)]
        public string ApprovedByUserId { get; set; } = null!;

        public DateTime ApprovedAtUtc { get; set; }
    }

    /// <summary>Append-only decommission-window audit event (v2 13.5). One run correlates by
    /// ApplyId: Started (before any ARM lock mutation) is followed by at most one terminal -
    /// Completed (every scope locked or already-locked), Failed (some scope refused), or
    /// OutcomeUnknown (the lock run became unconfirmable). Release records Aborted. Events are
    /// never updated or deleted; a failed audit write is logged critical, never swallowed.</summary>
    public record DecomWindowEventModel
    {
        [Key]
        public Guid Id { get; set; }

        /// <summary>Correlates all events belonging to one lock run or release.</summary>
        public Guid ApplyId { get; set; }

        /// <summary>Event type: Started / Completed / Failed / OutcomeUnknown / Aborted.</summary>
        [StringLength(16, MinimumLength = 1)]
        public string Event { get; set; } = null!;

        [StringLength(64, MinimumLength = 1)]
        public string WindowId { get; set; } = null!;

        /// <summary>Runbook plan hash; empty for a release (the release request carries none).</summary>
        [StringLength(71)]
        public string PlanHash { get; set; } = "";

        /// <summary>Execution hash (the bound lock set); empty for a release.</summary>
        [StringLength(71)]
        public string ExecutionHash { get; set; } = "";

        [StringLength(64, MinimumLength = 1)]
        public string SubscriptionId { get; set; } = null!;

        [StringLength(320)]
        public string Principal { get; set; } = "";

        /// <summary>Per-scope outcome summary or failure context, truncated to 1024.</summary>
        [StringLength(1024)]
        public string? Detail { get; set; }

        public DateTime AtUtc { get; set; }
    }
}
