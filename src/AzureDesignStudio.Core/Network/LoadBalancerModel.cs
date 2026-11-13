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
    [MapToDto(TypeKey = AdsConstants.LoadBalancer)]
    public class LoadBalancerModel : AzureNodeBase
    {
        public LoadBalancerModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Load Balancer";
        private readonly LoadBalancers _loadBalancer = new()
        {
            Sku = new()
            {
                Name = "Standard",
                Tier = "Regional"
            },
            Properties = new()
            {
                FrontendIPConfigurations = new List<FrontendIPConfiguration>
                {
                    new FrontendIPConfiguration()
                    {
                        Name = "frontend",
                        Properties = new FrontendIPConfigurationPropertiesFormat()
                        {
                            PrivateIPAllocationMethod = "Dynamic",
                        }
                    }
                },
                BackendAddressPools = new List<BackendAddressPool>
                {
                    new BackendAddressPool()
                    {
                        Name = "backend"
                    }
                }
            },
        };
        protected override ResourceBase ArmResource => _loadBalancer;
        public override Type? DataFormType => typeof(LoadBalancerForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _loadBalancer.Sku.Name;
            set => _loadBalancer.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("Frontend name")]
        public string FrontendName
        {
            get => _loadBalancer.Properties.FrontendIPConfigurations[0].Name;
            set => _loadBalancer.Properties.FrontendIPConfigurations[0].Name = value;
        }
        [MapToDto]
        [DisplayName("Backend pool name")]
        public string BackendPoolName
        {
            get => _loadBalancer.Properties.BackendAddressPools[0].Name;
            set => _loadBalancer.Properties.BackendAddressPools[0].Name = value;
        }
        private PublicIpModel? LinkedPublicIp => LinkedNodes.OfType<PublicIpModel>().FirstOrDefault();

        /// <summary>A frontend has to be somewhere: a linked public IP makes a public load
        /// balancer, a subnet around it an internal one. With neither Azure refuses it
        /// (FrontendIPConfigurationHasNoSubnetOrPublicIPAddressOrPublicIPPrefix).</summary>
        public override bool IsValid => LinkedPublicIp is not null || Group is SubnetModel;

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            var frontend = _loadBalancer.Properties.FrontendIPConfigurations[0];
            if (LinkedPublicIp is { } address)
            {
                frontend.Properties = new FrontendIPConfigurationPropertiesFormat
                {
                    PublicIPAddress = new PublicIPAddress { Id = address.ResourceId },
                };
                _loadBalancer.DependsOn = [address.ResourceId];
            }
            else if (Group is SubnetModel subnet)
            {
                frontend.Properties = new FrontendIPConfigurationPropertiesFormat
                {
                    PrivateIPAllocationMethod = "Dynamic",
                    Subnet = new Subnet { Id = subnet.ResourceId },
                };
                _loadBalancer.DependsOn = [subnet.ResourceId];
            }
            else
            {
                throw new Exception($"{ServiceName} '{Name}' needs a frontend: link a public IP to it, or place it in a subnet.");
            }
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<LoadBalancerModelDto>(this);
        }
    }
}
