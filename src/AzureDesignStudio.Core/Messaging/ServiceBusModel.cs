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
    [MapToDto(TypeKey = AdsConstants.ServiceBus)]
    public class ServiceBusModel : AzureNodeBase
    {
        public ServiceBusModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Service Bus";
        private readonly ServiceBus _serviceBus = new()
        {
            Sku = new()
            {
                Name = "Standard",
                Tier = "Regional"
            },
        };
        protected override ResourceBase ArmResource => _serviceBus;
        public override Type? DataFormType => typeof(ServiceBusForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _serviceBus.Sku.Name;
            set
            {
                _serviceBus.Sku.Name = value;
                _serviceBus.Sku.Capacity = value == "Premium" ? 1 : 0;
            }
        }
        [MapToDto]
        [DisplayName("Capacity")]
        public int Capacity
        {
            get => _serviceBus.Sku.Capacity;
            set => _serviceBus.Sku.Capacity = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-]{4,48}[a-zA-Z0-9]$")
                && (Sku != "Premium" || Capacity >= 1);
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Service Bus cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ServiceBusModelDto>(this);
        }
    }
}
