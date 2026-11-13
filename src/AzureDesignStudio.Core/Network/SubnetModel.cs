using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Web;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.SQL;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;

namespace AzureDesignStudio.Core.Network
{
    [MapToDto(TypeKey = AdsConstants.Subnet)]
    public class SubnetModel : AzureGroupBase
    {
        public override string ServiceName => "Subnet";
        public override Type? DataFormType => typeof(SubnetForm);
        public override GroupStyle GroupStyle
        {
            get => new()
            {
                OutlineColor = "#cccccc"
            };
        }
        private readonly Subnets _subnet = new()
        {
            Properties = new()
            {
                AddressPrefix = "10.0.0.0/24",
                PrivateEndpointNetworkPolicies = "Enabled",
                PrivateLinkServiceNetworkPolicies = "Enabled"
            }
        };
        protected override ResourceBase ArmResource => _subnet;
        public override string ResourceId
        {
            get
            {
                if (Group is not VirtualNetworkModel vnet)
                    throw new Exception($"Subnet needs to be in a vnet.");

                return $"[resourceId('{_subnet.Type}', '{vnet.Name}','{ArmName}')]";
            }
        }

        /// <summary>The name written to the template: a reserved subnet name always in its
        /// documented spelling (AzureFirewallSubnet), anything else as typed.</summary>
        public string ArmName => DedicatedSubnets.Canonical(Name);

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is not VirtualNetworkModel)
                return (false, "A subnet must be placed inside a virtual network.");

            return (true, string.Empty);
        }

        [MapToDto]
        [Required, DisplayName("Address space")]
        public string AddressPrefix
        {
            get => _subnet.Properties.AddressPrefix;
            set => _subnet.Properties.AddressPrefix = value;
        }

        [MapToDto]
        [DisplayName("Delegation")]
        public string Delegation
        {
            get => _delegation;
            set => _delegation = value;
        }
        private string _delegation = string.Empty;

        /// <summary>True when the subnet has no default outbound access (no implicit public IP).
        /// Off by default so a design saved before this option behaves as it always did.</summary>
        [MapToDto]
        [DisplayName("Private subnet (no default outbound access)")]
        public bool PrivateSubnet { get; set; }

        /// <summary>Azure accepts <c>defaultOutboundAccess</c> only on a subnet with no
        /// delegation (including the App Service one added for a plan placed here) and not on
        /// a reserved-name subnet.</summary>
        public bool SupportsPrivateSubnet =>
            string.IsNullOrEmpty(Delegation)
            && !Children.OfType<AppServicePlanModel>().Any()
            && !DedicatedSubnets.IsReserved(Name);

        private static void PopulateServiceEndpoints(NodeModel node, IList<ServiceEndpointPropertiesFormat> serviceEndpoints)
        {
            foreach (var port in node.Ports)
            {
                foreach (var link in port.Links)
                {
                    if (link.Target.Model is PortModel tp && tp.Parent == node)
                        continue;

                    if (link.Target.Model is PortModel sqlTp && sqlTp.Parent is SqlDatabaseModel sqlDb
                        && serviceEndpoints.FirstOrDefault(d => d.Service == "Microsoft.Sql") is null)
                    {
                        serviceEndpoints.Add(new()
                        {
                            Service = "Microsoft.Sql",
                            Locations = new List<string> { sqlDb.Location }
                        });
                    }
                    if (link.Target.Model is PortModel webTp && webTp.Parent is WebAppModel webapp
                        && serviceEndpoints.FirstOrDefault(d => d.Service == "Microsoft.Web") is null)
                    {
                        serviceEndpoints.Add(new()
                        {
                            Service = "Microsoft.Web",
                            Locations = new List<string> { "*" }
                        });
                    }
                }
            }
        }

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            if (Group is not VirtualNetworkModel vnet)
                throw new Exception($"Subnet must be in a vnet.");

            ArmResource.Name = $"{vnet.Name}/{ArmName}";
            _subnet.Location = null;
            _subnet.DependsOn = new List<string>()
            {
                vnet.ResourceId,
            };
            // Subnets of one network are created one after another: Azure refuses concurrent
            // changes to a network's subnets (AnotherOperationInProgress) - something a
            // template validation does not catch, only a real deployment.
            var siblings = vnet.Subnets;
            var index = siblings.IndexOf(this);
            if (index > 0)
                _subnet.DependsOn.Add(siblings[index - 1].ResourceId);
            _subnet.Properties.ServiceEndpoints = new List<ServiceEndpointPropertiesFormat>();
            _subnet.Properties.Delegations = new List<Delegation>();

            foreach (var child in Children)
            {
                if (child is AppServicePlanModel
                    && _subnet.Properties.Delegations.FirstOrDefault(d => d.Properties?.ServiceName == "Microsoft.Web/serverfarms") is null)
                {
                    _subnet.Properties.Delegations.Add(new()
                    {
                        Name = "appsvc-delegation",
                        Properties = new()
                        {
                            ServiceName = "Microsoft.Web/serverfarms"
                        }
                    });
                }

                if (child is GroupModel group)
                {
                    foreach (var node in group.Children)
                    {
                        PopulateServiceEndpoints(node, _subnet.Properties.ServiceEndpoints);
                    }
                }
                else
                {
                    PopulateServiceEndpoints(child, _subnet.Properties.ServiceEndpoints);
                }
            }

            if (!string.IsNullOrEmpty(Delegation)
                && _subnet.Properties.Delegations.FirstOrDefault(d => d.Properties?.ServiceName == Delegation) is null)
            {
                _subnet.Properties.Delegations.Add(new()
                {
                    Name = $"{Delegation.Split('/').Last()}-delegation",
                    Properties = new()
                    {
                        ServiceName = Delegation
                    }
                });
            }

            // API Management in a network refuses a subnet without a network security group
            // (NetworkSecurityGroupNotFound) - at create time, not in validate or what-if. The
            // subnet brings its own, with the rules the service documents as required.
            _apimNsg = BuildApiManagementNsg(vnet);
            if (_apimNsg is not null)
            {
                var nsgId = $"[resourceId('{_apimNsg.Type}', '{_apimNsg.Name}')]";
                _subnet.Properties.NetworkSecurityGroup = new() { Id = nsgId };
                _subnet.DependsOn.Add(nsgId);
            }
            else
            {
                _subnet.Properties.NetworkSecurityGroup = null;
            }

            if (NatGateway is { } nat)
            {
                _subnet.Properties.NatGateway = new() { Id = nat.ResourceId };
                _subnet.DependsOn.Add(nat.ResourceId);
            }
            else
            {
                _subnet.Properties.NatGateway = null;
            }

            _subnet.Properties.DefaultOutboundAccess = SupportsPrivateSubnet ? !PrivateSubnet : null;

            // Don't include them in the serialization if they are empty. 
            if (_subnet.Properties.ServiceEndpoints.Count == 0)
                _subnet.Properties.ServiceEndpoints = null;
            if (_subnet.Properties.Delegations.Count == 0)
                _subnet.Properties.Delegations = null;
        }

        private NetworkSecurityGroups? _apimNsg;

        /// <summary>The NAT gateway serving this subnet: the first one linked to a node placed in
        /// the subnet, or in a group inside it (a VM in an availability set). Subnets have no
        /// ports of their own, so the link is drawn to a machine.</summary>
        public NatGatewayModel? NatGateway =>
            Children.SelectMany(c => c is GroupModel g ? g.Children : [c])
                .OfType<AzureNodeBase>()
                .SelectMany(n => n.LinkedNodes)
                .OfType<NatGatewayModel>()
                .FirstOrDefault();

        public override IList<ResourceBase> GetArmResources()
        {
            var subnet = base.GetArmResources();
            return _apimNsg is null ? subnet : [_apimNsg, .. subnet];
        }

        /// <summary>
        /// The NSG an API Management subnet needs (learn.microsoft.com, "Virtual network
        /// configuration reference: API Management"): management on 3443 from the ApiManagement
        /// tag and the load balancer probe on 6390 in both modes, client traffic on 80/443 in
        /// External mode, and the service's own dependencies outbound. Null when the subnet
        /// hosts no API Management.
        /// </summary>
        private NetworkSecurityGroups? BuildApiManagementNsg(VirtualNetworkModel vnet)
        {
            var apim = Children.OfType<APIMModel>().FirstOrDefault();
            if (apim is null)
                return null;

            var rules = new List<object>();
            var priority = 100;
            void Rule(string name, string direction, string source, string destination, string ports)
            {
                rules.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["protocol"] = "Tcp",
                        ["sourcePortRange"] = "*",
                        ["destinationPortRanges"] = ports.Split(','),
                        ["sourceAddressPrefix"] = source,
                        ["destinationAddressPrefix"] = destination,
                        ["access"] = "Allow",
                        ["priority"] = priority,
                        ["direction"] = direction,
                    },
                });
                priority += 10;
            }

            if (string.Equals(apim.VnetType, "External", StringComparison.OrdinalIgnoreCase))
                Rule("AllowClientHttps", "Inbound", "Internet", "VirtualNetwork", "80,443");
            Rule("AllowApiManagementManagement", "Inbound", "ApiManagement", "VirtualNetwork", "3443");
            Rule("AllowAzureLoadBalancerProbe", "Inbound", "AzureLoadBalancer", "VirtualNetwork", "6390");
            priority = 100;
            Rule("AllowStorage", "Outbound", "VirtualNetwork", "Storage", "443");
            Rule("AllowSql", "Outbound", "VirtualNetwork", "Sql", "1433");
            Rule("AllowKeyVault", "Outbound", "VirtualNetwork", "AzureKeyVault", "443");
            Rule("AllowAzureMonitor", "Outbound", "VirtualNetwork", "AzureMonitor", "1886,443");

            return new NetworkSecurityGroups
            {
                Name = $"nsg-{vnet.Name}-{ArmName}",
                Location = vnet.Location,
                Properties = new() { SecurityRules = rules },
            };
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<SubnetModelDto>(this);
        }
    }
}
