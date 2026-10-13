using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Messaging;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Messaging
{
    [MapToDto(TypeKey = AdsConstants.EventHubs)]
    public class EventHubsModel : AzureNodeBase
    {
        public EventHubsModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Event Hubs";
        private readonly EventHubs _eventHubs = new()
        {
            Sku = new()
            {
                Name = "Standard",
                Tier = "Regional"
            },
            Properties = new(),
        };
        protected override ResourceBase ArmResource => _eventHubs;
        public override Type? DataFormType => typeof(EventHubsForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _eventHubs.Sku.Name;
            set => _eventHubs.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("Disable auto ingestion")]
        public bool DisableAutoIngestion
        {
            get => _eventHubs.Properties.DisableAutoIngestion;
            set => _eventHubs.Properties.DisableAutoIngestion = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-]{4,48}[a-zA-Z0-9]$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Event Hubs cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<EventHubsModelDto>(this);
        }
    }
}
