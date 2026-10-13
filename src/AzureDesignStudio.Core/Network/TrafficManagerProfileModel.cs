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
    [MapToDto(TypeKey = AdsConstants.TrafficManagerProfile)]
    public class TrafficManagerProfileModel : AzureNodeBase
    {
        public TrafficManagerProfileModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Traffic Manager Profile";
        private readonly TrafficManagerProfiles _trafficManagerProfile = new()
        {
            Sku = new()
            {
                Name = "Standard",
            },
            Properties = new()
            {
                TrafficRoutingMethod = "Performance",
                DnsConfig = new()
                {
                    Ttl = 300,
                },
            },
        };
        protected override ResourceBase ArmResource => _trafficManagerProfile;
        public override Type? DataFormType => typeof(TrafficManagerProfileForm);

        [MapToDto]
        [DisplayName("Routing method")]
        public string RoutingMethod
        {
            get => _trafficManagerProfile.Properties.TrafficRoutingMethod;
            set => _trafficManagerProfile.Properties.TrafficRoutingMethod = value;
        }

        [MapToDto]
        [DisplayName("DNS TTL (seconds)")]
        public int DnsTtl
        {
            get => _trafficManagerProfile.Properties.DnsConfig.Ttl;
            set => _trafficManagerProfile.Properties.DnsConfig.Ttl = value;
        }

        public override bool IsValid => Regex.IsMatch(Name, @"^[a-zA-Z][a-zA-Z0-9-]{1,48}[a-zA-Z0-9]$");

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Traffic Manager Profile cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<TrafficManagerProfileModelDto>(this);
        }
    }
}
