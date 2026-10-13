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
    [MapToDto(TypeKey = AdsConstants.EventGrid)]
    public class EventGridModel : AzureNodeBase
    {
        public EventGridModel() : base()
        {
            AddPort(PortAlignment.Left);
            AddPort(PortAlignment.Top);
            AddPort(PortAlignment.Right);
            AddPort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Event Grid";
        private readonly EventGrid _eventGrid = new()
        {
            Properties = new()
            {
                InputSchema = "EventGrid"
            },
        };
        protected override ResourceBase ArmResource => _eventGrid;
        public override Type? DataFormType => typeof(EventGridForm);
        [MapToDto]
        [DisplayName("Input schema")]
        public string InputSchema
        {
            get => _eventGrid.Properties.InputSchema;
            set => _eventGrid.Properties.InputSchema = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z0-9\-\.]{3,50}$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Event Grid cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<EventGridModelDto>(this);
        }
    }
}
