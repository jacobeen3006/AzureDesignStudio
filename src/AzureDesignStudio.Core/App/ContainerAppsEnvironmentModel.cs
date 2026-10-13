using AutoMapper;
using AzureDesignStudio.AzureResources.App;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.App
{
    [MapToDto(TypeKey = AdsConstants.ContainerAppsEnvironment)]
    public class ContainerAppsEnvironmentModel : AzureGroupBase
    {
        public ContainerAppsEnvironmentModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Container Apps Environment";
        public override Type? DataFormType => typeof(ContainerAppsEnvironmentForm);
        private readonly ManagedEnvironments _environment = new()
        {
            Properties = new()
            {
                ZoneRedundant = false,
            },
        };
        protected override ResourceBase ArmResource => _environment;
        [MapToDto]
        [DisplayName("Zone redundant")]
        public bool ZoneRedundant
        {
            get => _environment.Properties.ZoneRedundant;
            set => _environment.Properties.ZoneRedundant = value;
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is IAzureNode n)
                return (false, $"Container Apps Environment cannot be deployed in the {n.ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ContainerAppsEnvironmentModelDto>(this);
        }
    }
}
