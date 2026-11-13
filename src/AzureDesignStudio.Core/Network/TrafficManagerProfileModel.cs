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
                // A profile needs a health probe; HTTPS on 443 at "/" is the portal's default too.
                MonitorConfig = new()
                {
                    Protocol = "HTTPS",
                    Port = 443,
                    Path = "/",
                },
            },
        };

        // Traffic Manager is a global service: the profile's location is the literal "global",
        // never the resource group's region.
        public override bool UseResourceGroupLocation { get => false; set { } }
        public override string Location { get => "global"; set { } }

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            // The profile's public name is {relativeName}.trafficmanager.net; without one the
            // profile cannot be created. The resource name is already DNS-safe (see IsValid).
            _trafficManagerProfile.Properties.DnsConfig.RelativeName = Name.ToLowerInvariant();
        }
        protected override ResourceBase ArmResource => _trafficManagerProfile;
        public override Type? DataFormType => typeof(TrafficManagerProfileForm);

        [MapToDto]
        [DisplayName("Routing method")]
        public string RoutingMethod
        {
            get => _trafficManagerProfile.Properties.TrafficRoutingMethod;
            set
            {
                if (!string.IsNullOrEmpty(value))
                    _trafficManagerProfile.Properties.TrafficRoutingMethod = value;
            }
        }

        [MapToDto]
        [DisplayName("DNS TTL (seconds)")]
        public int DnsTtl
        {
            get => _trafficManagerProfile.Properties.DnsConfig.Ttl;
            set
            {
                // 0 is what a design saved without the value maps to; keep the default then.
                if (value > 0)
                    _trafficManagerProfile.Properties.DnsConfig.Ttl = value;
            }
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
