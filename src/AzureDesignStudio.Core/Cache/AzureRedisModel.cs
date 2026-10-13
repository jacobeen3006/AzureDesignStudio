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
            AddPort(PortAlignment.Left);
            AddPort(PortAlignment.Top);
            AddPort(PortAlignment.Right);
            AddPort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Azure Redis";
        private readonly Redis _redis = new()
        {
            Sku = new()
            {
                Family = "C",
                Capacity = 1
            },
            Properties = new()
            {
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
            get => _redis.Sku.Family;
            set => _redis.Sku.Family = value;
        }
        [MapToDto]
        [DisplayName("SKU capacity")]
        public int Capacity
        {
            get => _redis.Sku.Capacity;
            set => _redis.Sku.Capacity = value;
        }
        [DisplayName("SKU")]
        public string Sku => $"{Family}_{Capacity}";
        public override bool IsValid
        {
            get => Family switch
            {
                "C" => Capacity >= 0 && Capacity <= 5,
                "P" => Capacity >= 0 && Capacity <= 5,
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

            _redis.Sku.Name = Sku;
        }
    }
}
