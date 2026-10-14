using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

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
        }

        public DbSet<DesignModel> AdsDesigns { get; set; } = null!;
        public DbSet<AzureSubscriptionModel> AzureSubscriptions { get; set; } = null!;
        public DbSet<PlanApprovalModel> AdsPlanApprovals { get; set; } = null!;
        public DbSet<PlanApplyEventModel> AdsPlanApplyEvents { get; set; } = null!;
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
    }

    /// <summary>
    /// Append-only apply audit events (v2 plan [62]/11.3). One apply correlates by ApplyId:
    /// a Started event (canonical plan body + consumed what-if preview evidence, written before
    /// any ARM mutation) followed by exactly one terminal Completed or Failed event. A rejected
    /// or interrupted start is recorded as Failed - it never claims "applied". Approval rows are
    /// consumed at apply; these events are never updated or deleted.
    /// </summary>
    public record PlanApplyEventModel
    {
        public const string Started = "Started";
        public const string Completed = "Completed";
        public const string Failed = "Failed";

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

        [StringLength(16, MinimumLength = 7)]
        public string EventType { get; set; } = null!;

        /// <summary>Terminal context (provisioningState / error excerpt), truncated to 1024.</summary>
        [StringLength(1024)]
        public string? Detail { get; set; }

        public DateTime TimestampUtc { get; set; }
    }
}
