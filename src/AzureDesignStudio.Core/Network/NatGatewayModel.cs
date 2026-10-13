using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Network
{
    [MapToDto(TypeKey = AdsConstants.NatGateway)]
    public class NatGatewayModel : AzureNodeBase
    {
        public NatGatewayModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "NAT Gateway";
        private readonly NatGateways _natGateway = new()
        {
            Sku = new()
            {
                Name = "Standard",
            },
            Properties = new()
            {
                IdleTimeoutInMinutes = 4,
                PublicIpAddresses = new List<object>(),
            },
        };
        protected override ResourceBase ArmResource => _natGateway;
        public override Type? DataFormType => typeof(NatGatewayForm);

        [MapToDto]
        [DisplayName("SKU")]
        public string SkuName => _natGateway.Sku.Name;

        [MapToDto]
        [DisplayName("Idle timeout (minutes)")]
        public int IdleTimeoutInMinutes
        {
            get => _natGateway.Properties.IdleTimeoutInMinutes;
            set => _natGateway.Properties.IdleTimeoutInMinutes = value;
        }

        public override bool IsValid => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-_.]{0,78}[a-zA-Z0-9_]$");

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"NAT Gateway cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<NatGatewayModelDto>(this);
        }
    }
}
