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
    [MapToDto(TypeKey = AdsConstants.ContainerAppsJob)]
    public class ContainerAppsJobModel : AzureNodeBase
    {
        public ContainerAppsJobModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Container Apps Job";
        public override Type? DataFormType => typeof(ContainerAppsJobForm);
        private readonly Jobs _job = new()
        {
            Properties = new()
            {
                Configuration = new()
                {
                    TriggerType = "Manual",
                },
                Template = new()
                {
                    Containers = new List<JobContainer>
                    {
                        new()
                        {
                            Image = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest",
                        },
                    },
                },
            },
        };
        protected override ResourceBase ArmResource => _job;
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is not ContainerAppsEnvironmentModel)
                return (false, $"{ServiceName} can only be deployed in a Container Apps Environment");

            return base.IsDrappable(overlappedGroup);
        }
        public override bool IsValid => TriggerType != "Schedule" || !string.IsNullOrWhiteSpace(Schedule);
        [MapToDto]
        [DisplayName("Trigger type")]
        public string TriggerType
        {
            get => _job.Properties.Configuration.TriggerType;
            set => _job.Properties.Configuration.TriggerType = value;
        }
        [MapToDto]
        [DisplayName("Schedule")]
        public string Schedule
        {
            get => _job.Properties.Configuration.Schedule;
            set => _job.Properties.Configuration.Schedule = value;
        }
        [MapToDto]
        [DisplayName("Image")]
        public string Image
        {
            get => _job.Properties.Template.Containers[0].Image;
            set => _job.Properties.Template.Containers[0].Image = value;
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ContainerAppsJobModelDto>(this);
        }
        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            if (Group is not ContainerAppsEnvironmentModel environment)
                throw new Exception($"{ServiceName} must be in a Container Apps Environment.");

            _job.Properties.EnvironmentId = environment.ResourceId;
            _job.DependsOn = new List<string> { environment.ResourceId };

            // The schedule is only meaningful for recurring jobs.
            if (TriggerType != "Schedule")
                _job.Properties.Configuration.Schedule = null;
        }
    }
}
