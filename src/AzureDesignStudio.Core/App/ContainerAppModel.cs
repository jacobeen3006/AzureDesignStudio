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
    [MapToDto(TypeKey = AdsConstants.ContainerApp)]
    public class ContainerAppModel : AzureNodeBase
    {
        public ContainerAppModel() : base()
        {
            AddPort(PortAlignment.Left);
            AddPort(PortAlignment.Top);
            AddPort(PortAlignment.Right);
            AddPort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Container App";
        public override Type? DataFormType => typeof(ContainerAppForm);
        private readonly ContainerApps _containerApp = new()
        {
            Properties = new()
            {
                Configuration = new()
                {
                    ActiveRevisionsMode = "Single",
                    Ingress = new()
                    {
                        External = true,
                        TargetPort = 80,
                    },
                },
                Template = new()
                {
                    Containers = new List<ContainerAppContainer>
                    {
                        new()
                        {
                            Image = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest",
                            Resources = new()
                            {
                                Cpu = "0.25",
                                Memory = "0.5Gi",
                            },
                        },
                    },
                },
            },
        };
        protected override ResourceBase ArmResource => _containerApp;
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is not ContainerAppsEnvironmentModel)
                return (false, $"{ServiceName} can only be deployed in a Container Apps Environment");

            return base.IsDrappable(overlappedGroup);
        }
        [MapToDto]
        [DisplayName("Image")]
        public string Image
        {
            get => _containerApp.Properties.Template.Containers[0].Image;
            set => _containerApp.Properties.Template.Containers[0].Image = value;
        }
        [MapToDto]
        [DisplayName("CPU")]
        public string Cpu
        {
            get => _containerApp.Properties.Template.Containers[0].Resources.Cpu;
            set => _containerApp.Properties.Template.Containers[0].Resources.Cpu = value;
        }
        [MapToDto]
        [DisplayName("Memory")]
        public string Memory
        {
            get => _containerApp.Properties.Template.Containers[0].Resources.Memory;
            set => _containerApp.Properties.Template.Containers[0].Resources.Memory = value;
        }
        [MapToDto]
        [DisplayName("Target port")]
        public int TargetPort
        {
            get => _containerApp.Properties.Configuration.Ingress.TargetPort;
            set => _containerApp.Properties.Configuration.Ingress.TargetPort = value;
        }
        [MapToDto]
        [DisplayName("External")]
        public bool External
        {
            get => _containerApp.Properties.Configuration.Ingress.External;
            set => _containerApp.Properties.Configuration.Ingress.External = value;
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ContainerAppModelDto>(this);
        }
        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            if (Group is not ContainerAppsEnvironmentModel environment)
                throw new Exception($"{ServiceName} must be in a Container Apps Environment.");

            _containerApp.Properties.ManagedEnvironmentId = environment.ResourceId;
            _containerApp.DependsOn = new List<string> { environment.ResourceId };
        }
    }
}
