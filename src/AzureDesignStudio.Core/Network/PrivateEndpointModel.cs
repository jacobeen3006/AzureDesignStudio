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
        public const string DefaultGroupId = "sqlServer";

        /// <summary>The private-link sub-resource ("group id") for the resource types on the
        /// palette that support one. A linked target of one of these types names its own
        /// group id, so the usual case needs nothing typed.</summary>
        private static readonly Dictionary<string, string> GroupIdByType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.Sql/servers"] = "sqlServer",
            ["Microsoft.Storage/storageAccounts"] = "blob",
            ["Microsoft.KeyVault/vaults"] = "vault",
            ["Microsoft.ContainerRegistry/registries"] = "registry",
            ["Microsoft.Cache/redis"] = "redisCache",
            ["Microsoft.Web/sites"] = "sites",
            ["Microsoft.ServiceBus/namespaces"] = "namespace",
            ["Microsoft.EventHub/namespaces"] = "namespace",
            ["Microsoft.DocumentDB/databaseAccounts"] = "Sql",
            ["Microsoft.AppConfiguration/configurationStores"] = "configurationStores",
            ["Microsoft.CognitiveServices/accounts"] = "account",
            ["Microsoft.Search/searchServices"] = "searchService",
            ["Microsoft.SignalRService/signalR"] = "signalr",
            ["Microsoft.DataFactory/factories"] = "dataFactory",
        };

        /// <summary>True when the ARM resource type offers a private link on the palette.</summary>
        public static bool SupportsPrivateLink(string armResourceType) =>
            !string.IsNullOrEmpty(armResourceType) && GroupIdByType.ContainsKey(armResourceType);

        public PrivateEndpointModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Private Endpoint";

        private readonly PrivateEndpoints _privateEndpoint = new();
        protected override ResourceBase ArmResource => _privateEndpoint;
        public override Type? DataFormType => typeof(PrivateEndpointForm);

        /// <summary>Resource id typed by hand, for a target that is not on the canvas. A linked
        /// target takes precedence.</summary>
        [MapToDto]
        [DisplayName("Target resource")]
        public string TargetResourceId { get; set; } = string.Empty;

        [MapToDto]
        [DisplayName("Group ID")]
        public string PrivateLinkGroupId { get; set; } = DefaultGroupId;

        /// <summary>The subnet the endpoint's network interface is created in.</summary>
        public SubnetModel? Subnet => Group as SubnetModel;

        /// <summary>The resource on the canvas this endpoint is linked to, if it is of a type
        /// that offers a private link.</summary>
        public AzureNodeBase? LinkedTarget =>
            LinkedNodes.OfType<AzureNodeBase>().FirstOrDefault(n => GroupIdByType.ContainsKey(n.ArmResourceType));

        public string EffectiveTargetId => LinkedTarget?.ResourceId ?? (TargetResourceId ?? string.Empty).Trim();

        /// <summary>The typed group id, unless it was left at its default and the linked
        /// target is of a type whose group id is known.</summary>
        public string EffectiveGroupId
        {
            get
            {
                var typed = (PrivateLinkGroupId ?? string.Empty).Trim();
                if (LinkedTarget is { } target && (typed.Length == 0 || typed == DefaultGroupId))
                    return GroupIdByType[target.ArmResourceType];
                return typed.Length == 0 ? DefaultGroupId : typed;
            }
        }

        /// <summary>An endpoint is a network interface in a subnet, connected to one resource.
        /// Without either it cannot be created; it used to export with neither.</summary>
        public override bool IsValid => Subnet is not null && EffectiveTargetId.Length > 0;

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is SubnetModel)
                return (true, string.Empty);
            if (overlappedGroup != null)
                return (false, $"Private Endpoint cannot be in {((IAzureNode)overlappedGroup).ServiceName}; place it in a subnet.");

            return (true, string.Empty);
        }

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            var subnet = Subnet ?? throw new Exception($"{ServiceName} '{Name}' must be placed in a subnet.");
            if (EffectiveTargetId.Length == 0)
                throw new Exception($"{ServiceName} '{Name}' needs a target: link it to a resource, or enter a resource id.");

            _privateEndpoint.Properties = new PrivateEndpointProperties
            {
                Subnet = new Subnet { Id = subnet.ResourceId },
                PrivateLinkServiceConnections =
                [
                    new PrivateLinkServiceConnection
                    {
                        Name = Name + "-connection",
                        Properties = new PrivateLinkServiceConnectionProperties
                        {
                            PrivateLinkServiceId = EffectiveTargetId,
                            GroupIds = [EffectiveGroupId],
                        },
                    },
                ],
            };

            _privateEndpoint.DependsOn = [subnet.ResourceId];
            if (LinkedTarget is { } target)
                _privateEndpoint.DependsOn.Add(target.ResourceId);
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<PrivateEndpointModelDto>(this);
        }
    }
}
