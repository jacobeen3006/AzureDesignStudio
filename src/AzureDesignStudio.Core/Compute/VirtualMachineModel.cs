using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Compute;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Compute
{
    [MapToDto(TypeKey = AdsConstants.VirtualMachine)]
    public class VirtualMachineModel : AzureNodeBase
    {
        public VirtualMachineModel()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Virtual Machine";
        public override Type? DataFormType => typeof(VirtualMachineForm);
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<VirtualMachineModelDto>(this);
        }
        private readonly VirtualMachines _vm = new();
        protected override ResourceBase ArmResource => _vm;

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            if (Group is AvailabilitySetModel availabilitySet)
            {
                _vm.Properties ??= new VirtualMachineProperties();
                _vm.Properties.AvailabilitySet = new SubResource { Id = availabilitySet.ResourceId };
            }
        }
    }
}
