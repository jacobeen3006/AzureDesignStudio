using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Web;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AzureDesignStudio.Core.Web
{
    [MapToDto(TypeKey = AdsConstants.AppServicePlan)]
    public class AppServicePlanModel : AzureGroupBase
    {
        private GroupStyle groupStyle = null!;
        public override GroupStyle GroupStyle
        {
            get => groupStyle ??= new()
            {
                OutlineWidth = 0,
                BackgroundColor = "#e6f7ff",
            };
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<AppServicePlanModelDto>(this);
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is SubnetModel)
                return (true, string.Empty);

            return base.IsDrappable(overlappedGroup);
        }
        public override string ServiceName => "App Service Plan";
        public override Type? DataFormType => typeof(AppServicePlanForm);
        private readonly Serverfarms _servicePlan = new()
        {
            Kind = "linux",
            Sku = new()
            {
                Name = "P1v2",
                Tier = "PremiumV2",
            },
            Properties = new()
            {
                TargetWorkerCount = 1,
            }
        };
        protected override ResourceBase ArmResource => _servicePlan;
        [MapToDto]
        [Required, DisplayName("OS")]
        public string Kind
        {
            get => _servicePlan.Kind;
            set => _servicePlan.Kind = value;
        }
        [MapToDto]
        [Required, DisplayName("SKU Name")]
        public string SkuName
        {
            get => _servicePlan.Sku.Name;
            set => _servicePlan.Sku.Name = value;
        }
        [MapToDto]
        [Required, DisplayName("Tier")]
        public string SkuTier
        {
            get => _servicePlan.Sku.Tier;
            set
            {
                if (_servicePlan.Sku.Tier != value)
                {
                    _servicePlan.Sku.Tier = value;
                    GroupStyle.BackgroundColor = _servicePlan.Sku.Tier switch
                    {
                        "Dynamic" => "#fff2e8",
                        "ElasticPremium" => "#f6ffed",
                        _ => "#e6f7ff",
                    };
                    BuildCSSStyle();
                }
            }
        }
        /// <summary>The dedicated (always-on) sizes the plan form offers. Free and Shared are
        /// left out: the exported sites set AlwaysOn, which those tiers reject.</summary>
        public static readonly IReadOnlyList<(string Name, string Tier)> DedicatedSkus =
        [
            ("B1", "Basic"), ("B2", "Basic"), ("B3", "Basic"),
            ("S1", "Standard"), ("S2", "Standard"), ("S3", "Standard"),
            ("P1v2", "PremiumV2"), ("P2v2", "PremiumV2"), ("P3v2", "PremiumV2"),
            ("P0v3", "PremiumV3"), ("P1v3", "PremiumV3"), ("P2v3", "PremiumV3"), ("P3v3", "PremiumV3"),
        ];

        public const string DefaultDedicatedSku = "P1v2";

        /// <summary>Consumption (Y1) or Elastic Premium (EP*): a plan only Function Apps can use.</summary>
        public bool IsServerless => SkuTier is "Dynamic" or "ElasticPremium";

        /// <summary>True while a Function App on this plan has chosen Consumption or Premium
        /// hosting: that choice, not the plan form, decides the SKU.</summary>
        public bool SkuFollowsFunctionApp =>
            Children.OfType<FunctionAppModel>().Any(f => f.HostingPlan is "consumption" or "premium");

        /// <summary>Sets a dedicated size and the tier that belongs to it. Unknown names are ignored.</summary>
        public bool UseDedicatedSku(string? skuName)
        {
            var match = DedicatedSkus.FirstOrDefault(s => string.Equals(s.Name, skuName, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
                return false;

            SkuName = match.Name;
            SkuTier = match.Tier;
            return true;
        }

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();
            _servicePlan.Properties.Reserved = Kind == "linux";
        }
    }
}
