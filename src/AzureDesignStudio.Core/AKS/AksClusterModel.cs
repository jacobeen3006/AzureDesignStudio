using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.ContainerService;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System;
using System.ComponentModel;
using System.Text;

namespace AzureDesignStudio.Core.AKS
{
    [MapToDto(TypeKey = AdsConstants.AKSCluster)]
    public class AksClusterModel : AzureNodeBase
    {
        public AksClusterModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "AKS Cluster";
        public override Type? DataFormType => typeof(AksClusterForm);

        // ARM defaults where the form leaves a field blank: AKS deploys its current default
        // Kubernetes version when the property is omitted (WhenWritingDefault drops nulls),
        // and no service principal profile means managed identity - never a fake SP.
        private readonly ManagedClusters _cluster = new()
        {
            Properties = new ManagedClustersClusterProperties
            {
                DefaultAgentPoolProfile = new ManagedClustersAgentPoolProfile
                {
                    Name = "nodepool1",
                    Count = 3,
                    VmSize = "Standard_D2s_v3",
                    Mode = "System",
                    OsType = "Linux",
                },
            },
        };
        protected override ResourceBase ArmResource => _cluster;

        [DisplayName("DNS prefix"), MapToDto]
        public string DnsPrefix
        {
            get => _dnsPrefix;
            set => _dnsPrefix = value ?? string.Empty;
        }

        [DisplayName("Kubernetes version"), MapToDto]
        public string KubernetesVersion
        {
            get => _cluster.Properties.KubernetesVersion;
            set => _cluster.Properties.KubernetesVersion = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        [DisplayName("Node count"), MapToDto]
        public int NodeCount
        {
            get => _cluster.Properties.DefaultAgentPoolProfile.Count;
            set => _cluster.Properties.DefaultAgentPoolProfile.Count = value;
        }

        [DisplayName("Agent VM size"), MapToDto]
        public string AgentVmSize
        {
            get => _cluster.Properties.DefaultAgentPoolProfile.VmSize;
            set => _cluster.Properties.DefaultAgentPoolProfile.VmSize = value;
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<AksClusterModelDto>(this);
        }

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            // The API demands a DNS prefix; derive a valid label from the cluster name when
            // the designer left it blank, so an untended tile still exports deployable bytes.
            // Derived at every export so it follows a rename, and never stored as the user's value.
            _cluster.Properties.DnsPrefix = string.IsNullOrWhiteSpace(_dnsPrefix) ? DeriveDnsPrefix(Name) : _dnsPrefix;

            // A cluster with neither a service principal nor an identity is refused
            // ("servicePrincipalProfile is missing"); a system-assigned identity needs no
            // secret. The pools travel as the API's agentPoolProfiles array - the single
            // "defaultAgentPoolProfile" member the export used to write does not exist.
            _cluster.Identity = new ManagedClustersIdentity { Type = "SystemAssigned" };
            var pool = _cluster.Properties.DefaultAgentPoolProfile;
            _cluster.Properties.AgentPoolProfiles = [pool];

            // Placed in a subnet, the nodes join it (Azure CNI). The service range must not
            // overlap the network, so it is taken from outside the 10.0.0.0/8 most designs use.
            if (Group is Network.SubnetModel subnet)
            {
                pool.VnetSubnetID = subnet.ResourceId;
                _cluster.Properties.NetworkProfile = new ManagedClustersNetworkProfile
                {
                    NetworkPlugin = "azure",
                    ServiceCidr = "172.16.0.0/16",
                    DnsServiceIP = "172.16.0.10",
                };
                _cluster.DependsOn = [subnet.ResourceId];
            }
            else
            {
                pool.VnetSubnetID = null;
                _cluster.Properties.NetworkProfile = null;
                _cluster.DependsOn = null;
            }
        }

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is null or Network.SubnetModel)
                return (true, string.Empty);
            return (false, $"{ServiceName} can only be placed in a subnet or directly on the canvas.");
        }

        private string _dnsPrefix = string.Empty;

        // ARM dnsPrefix grammar: lowercase alphanumeric or '-', no leading/trailing '-'.
        internal static string DeriveDnsPrefix(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in (name ?? string.Empty).ToLowerInvariant())
                sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-');
            var prefix = sb.ToString().Trim('-');
            while (prefix.Contains("--"))
                prefix = prefix.Replace("--", "-");
            prefix = prefix.Trim('-');
            if (prefix.Length == 0)
                prefix = "ads-aks";
            return prefix.Length > 44 ? prefix[..44].Trim('-') : prefix;
        }
    }
}
