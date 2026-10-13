using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Insights;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Insights
{
    [MapToDto(TypeKey = AdsConstants.ApplicationInsights)]
    public class ApplicationInsightsModel : AzureNodeBase
    {
        public ApplicationInsightsModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Application Insights";
        private readonly AzureResources.Insights.Components _component = new()
        {
            Kind = "web",
            Properties = new()
            {
                IngestionMode = "ApplicationInsights",
            },
        };
        protected override ResourceBase ArmResource => _component;
        public override Type? DataFormType => typeof(ApplicationInsightsForm);
        [MapToDto]
        [DisplayName("Application type")]
        public string ApplicationType
        {
            get => _component.Kind;
            set => _component.Kind = value;
        }
        [MapToDto]
        [DisplayName("Ingestion mode")]
        public string IngestionMode
        {
            get => _component.Properties.IngestionMode;
            set => _component.Properties.IngestionMode = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z][a-zA-Z0-9._-]{0,89}$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Application Insights cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ApplicationInsightsModelDto>(this);
        }
    }
}
