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
}
