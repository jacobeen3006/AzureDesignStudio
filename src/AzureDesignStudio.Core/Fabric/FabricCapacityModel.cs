using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Fabric;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.Fabric
{
    [MapToDto(TypeKey = AdsConstants.FabricCapacity)]
    public class FabricCapacityModel : AzureNodeBase
    {
        public FabricCapacityModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Fabric Capacity";
        private readonly FabricCapacities _capacity = new()
        {
            Sku = new()
            {
                Name = "F4",
                Tier = "Fabric"
            },
        };
        protected override ResourceBase ArmResource => _capacity;
        public override Type? DataFormType => typeof(FabricCapacityForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _capacity.Sku.Name;
            set => _capacity.Sku.Name = value;
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Fabric Capacity cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<FabricCapacityModelDto>(this);
        }
    }
}
