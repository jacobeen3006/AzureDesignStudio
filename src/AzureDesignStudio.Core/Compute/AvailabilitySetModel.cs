using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Compute;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.Compute
{
    [MapToDto(TypeKey = AdsConstants.AvailabilitySet)]
    public class AvailabilitySetModel : AzureGroupBase
    {
        public AvailabilitySetModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Availability Set";
        public override Type? DataFormType => typeof(AvailabilitySetForm);
        public override GroupStyle GroupStyle
        {
            get => new()
            {
                OutlineWidth = 1,
                OutlineStyle = "solid",
                OutlineColor = "#8c8c8c"
            };
        }

        private readonly AvailabilitySets _availabilitySet = new()
        {
            Sku = new()
            {
                Name = "Aligned"
            },
            Properties = new()
            {
                PlatformFaultDomainCount = 2,
                PlatformUpdateDomainCount = 5,
            },
        };
        protected override ResourceBase ArmResource => _availabilitySet;

        [MapToDto]
        [DisplayName("Fault domains")]
        public int FaultDomains
        {
            get => _availabilitySet.Properties.PlatformFaultDomainCount;
            set => _availabilitySet.Properties.PlatformFaultDomainCount = value;
        }
        [MapToDto]
        [DisplayName("Update domains")]
        public int UpdateDomains
        {
            get => _availabilitySet.Properties.PlatformUpdateDomainCount;
            set => _availabilitySet.Properties.PlatformUpdateDomainCount = value;
        }

        // Aligned SKU supports 2-3 fault domains and up to 20 update domains.
        public override bool IsValid =>
            FaultDomains >= 2 && FaultDomains <= 3
            && UpdateDomains >= 1 && UpdateDomains <= 20;

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            // Availability sets are top-level resources; VMs are placed into them, not the reverse.
            if (overlappedGroup is not null)
                return (false, $"{ServiceName} is a top-level resource and cannot be placed inside another group.");

            return base.IsDrappable(overlappedGroup);
        }

        public override IList<ResourceBase> GetArmResources()
        {
            if (!IsValid)
                throw new Exception($"{Name} is not valid.");

            return base.GetArmResources();
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<AvailabilitySetModelDto>(this);
        }
    }
}
