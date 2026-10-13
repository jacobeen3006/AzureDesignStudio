using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.Network
{
    [MapToDto(TypeKey = AdsConstants.PrivateEndpoint)]
    public class PrivateEndpointModel : AzureNodeBase
    {
        public PrivateEndpointModel() : base()
        {
            AddPort(PortAlignment.Left);
            AddPort(PortAlignment.Top);
            AddPort(PortAlignment.Right);
            AddPort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Private Endpoint";
        private readonly PrivateEndpoints _privateEndpoint = new()
        {
            Properties = new()
            {
                PrivateLinkServiceConnections = new List<PrivateLinkServiceConnection>
                {
                    new PrivateLinkServiceConnection()
                    {
                        Name = "connection",
                        Properties = new PrivateLinkServiceConnectionProperties()
                        {
                            PrivateLinkServiceId = string.Empty,
                            GroupIds = new List<string> { "sqlServer" },
                        }
                    }
                }
            },
        };
        protected override ResourceBase ArmResource => _privateEndpoint;
        public override Type? DataFormType => typeof(PrivateEndpointForm);
        [MapToDto]
        [DisplayName("Target resource")]
        public string TargetResourceId
        {
            get => _privateEndpoint.Properties.PrivateLinkServiceConnections[0].Properties.PrivateLinkServiceId;
            set => _privateEndpoint.Properties.PrivateLinkServiceConnections[0].Properties.PrivateLinkServiceId = value;
        }
        [MapToDto]
        [DisplayName("Group ID")]
        public string PrivateLinkGroupId
        {
            get => _privateEndpoint.Properties.PrivateLinkServiceConnections[0].Properties.GroupIds[0];
            set => _privateEndpoint.Properties.PrivateLinkServiceConnections[0].Properties.GroupIds[0] = value;
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Private Endpoint cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<PrivateEndpointModelDto>(this);
        }
    }
}
