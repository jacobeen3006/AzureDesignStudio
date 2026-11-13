using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Cache;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.Cache
{
    [MapToDto(TypeKey = AdsConstants.AzureRedis)]
    public class AzureRedisModel : AzureNodeBase
    {
        public AzureRedisModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Azure Redis";
        private readonly Redis _redis = new()
        {
            Properties = new()
            {
                Sku = new()
                {
                    Family = "C",
                    Capacity = 1
                },
                MinimumTlsVersion = "1.2",
                EnableNonSslPort = false,
            },
        };
        protected override ResourceBase ArmResource => _redis;
        public override Type? DataFormType => typeof(AzureRedisForm);
        [MapToDto]
        [DisplayName("SKU family")]
        public string Family
        {
            get => _redis.Properties.Sku.Family;
            set
            {
                // An absent value (a design saved without it) keeps the current family.
                if (!string.IsNullOrEmpty(value))
                    _redis.Properties.Sku.Family = value;
            }
        }
        [MapToDto]
        [DisplayName("SKU capacity")]
        public int Capacity
        {
            get => _redis.Properties.Sku.Capacity ?? 0;
            set => _redis.Properties.Sku.Capacity = value;
        }
        [DisplayName("SKU")]
        public string Sku => $"{Family}_{Capacity}";
        /// <summary>The ARM sku name. C is the Basic/Standard family and P is Premium; the model
        /// has no Basic/Standard switch, so a C cache is exported as Standard (replicated, with
        /// an SLA). "C_1" - family and size glued together - is not a sku name ARM accepts.</summary>
        public string SkuName => Family == "P" ? "Premium" : "Standard";

        public override bool IsValid
        {
            get => Family switch
            {
                "C" => Capacity >= 0 && Capacity <= 6,
                "P" => Capacity >= 1 && Capacity <= 5, // there is no P0
                _ => false,
            };
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Azure Redis cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<AzureRedisModelDto>(this);
        }
        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            _redis.Properties.Sku.Name = SkuName;
        }
    }
}
