using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Compute;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Compute
{
    [MapToDto(TypeKey = AdsConstants.VirtualMachineScaleSet)]
    public class VirtualMachineScaleSetModel : AzureNodeBase
    {
        public VirtualMachineScaleSetModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "VM Scale Set";
        private readonly VirtualMachineScaleSets _scaleSet = new()
        {
            Sku = new()
            {
                Name = "Standard_D2s_v3",
                Capacity = 2,
            },
            Properties = new()
            {
                Overprovision = true,
            },
        };
        protected override ResourceBase ArmResource => _scaleSet;
        public override Type? DataFormType => typeof(VirtualMachineScaleSetForm);
        [MapToDto]
        [DisplayName("SKU name")]
        public string SkuName
        {
            get => _scaleSet.Sku.Name;
            set => _scaleSet.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("Capacity")]
        public int Capacity
        {
            get => _scaleSet.Sku.Capacity;
            set => _scaleSet.Sku.Capacity = value;
        }
        [MapToDto]
        [DisplayName("Overprovision")]
        public bool Overprovision
        {
            get => _scaleSet.Properties.Overprovision;
            set => _scaleSet.Properties.Overprovision = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])?$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"VM Scale Set cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<VirtualMachineScaleSetModelDto>(this);
        }
    }
}
