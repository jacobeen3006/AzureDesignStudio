using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AzureDesignStudio.Core.Network
{
    public class AddressSpace
    {
        // For AntList binding in UI.
        public string AddressPrefix { get; set; } = string.Empty;
    }
    [MapToDto(TypeKey = AdsConstants.VirtualNetwork)]
    public class VirtualNetworkModel : AzureGroupBase
    {
        public VirtualNetworkModel() : base()
        {
            AddPort(new VirtualNetworkPort(this, PortAlignment.Left));
            AddPort(new VirtualNetworkPort(this, PortAlignment.Top));
            AddPort(new VirtualNetworkPort(this, PortAlignment.Right));
            AddPort(new VirtualNetworkPort(this, PortAlignment.Bottom));
        }
        public override string ServiceName => "Virtual Network";
        public override Type? DataFormType => typeof(VirtualNetworkForm);
        private readonly VirtualNetworks _vnet = new();
        protected override ResourceBase ArmResource => _vnet;
        [MapToDto]
        public List<AddressSpace> IpSpace { get; set; } = new List<AddressSpace>()
        {
            new AddressSpace()
            {
                AddressPrefix = "10.0.0.0/16"
            }
        };
        public List<SubnetModel> Subnets
        {
            get
            {
                if (Children?.Count > 0)
                    return Children.Where(c => c is SubnetModel).Select(c => (SubnetModel)c).ToList();

                return new List<SubnetModel>();
            }
        }
        public SubnetModel AddSubnet(SubnetModel subnet)
        {
            var count = Subnets.Count;
            subnet.SetPosition(Position.X + Padding + count * 5, Position.Y + Padding + count * 5);

            AddChild(subnet);
            return subnet;
        }

        /// <summary>First unused /24 inside the first IPv4 address space, so auto-created
        /// subnets never overlap one the user already defined.</summary>
        public string NextFreeSubnetPrefix()
        {
            const int prefixLength = 24;
            var space = IpSpace.FirstOrDefault()?.AddressPrefix;
            if (space is null || !TryParseCidr(space, out var baseAddress, out var spacePrefix))
                return "10.0.0.0/24";

            var used = Subnets
                .Select(s => s.AddressPrefix.Trim())
                .Where(p => p.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (spacePrefix >= prefixLength)
                return FormatPrefix(baseAddress, prefixLength);

            var blockSize = 1u << (32 - prefixLength);
            var blocks = 1u << (prefixLength - spacePrefix);
            for (uint i = 0; i < blocks; i++)
            {
                var candidate = FormatPrefix(baseAddress + i * blockSize, prefixLength);
                if (!used.Contains(candidate))
                    return candidate;
            }

            return FormatPrefix(baseAddress, prefixLength);

            static bool TryParseCidr(string cidr, out uint address, out int prefix)
            {
                address = 0;
                prefix = 0;
                var parts = cidr.Split('/');
                if (parts.Length != 2
                    || !IPAddress.TryParse(parts[0], out var ip)
                    || ip.AddressFamily != AddressFamily.InterNetwork
                    || !int.TryParse(parts[1], out prefix)
                    || prefix is < 0 or > 32)
                    return false;

                address = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
                return true;
            }

            static string FormatPrefix(uint value, int prefix) =>
                $"{value >> 24}.{(value >> 16) & 0xff}.{(value >> 8) & 0xff}.{value & 0xff}/{prefix}";
        }

        public override IList<ResourceBase> GetArmResources()
        {
            _vnet.Properties = new()
            {
                AddressSpace = new()
                {
                    AddressPrefixes = IpSpace.Select(p => p.AddressPrefix).ToList(),
                }
            };

            var result = base.GetArmResources().ToList();

            foreach(var subnet in Subnets)
            {
                // Deploy subnets as separate resources.
                // It is necessary when there are other resources which depend on subnets.
                result.AddRange(subnet.GetArmResources());
            }

            // Vnet peering needs to be placed out of Vnet as it requires `dependsOn` to work. 
            // Child resource naming: https://docs.microsoft.com/en-us/azure/azure-resource-manager/templates/child-resource-name-type
            var ports = Ports.Where(p => p.Links.Count > 0).ToList();
            foreach (var port in ports)
            {
                foreach (var link in port.Links)
                {
                    var targetParent = link.Target.Model is PortModel tp ? tp.Parent : null;
                    var node = targetParent != this ? targetParent : (link.Source.Model is PortModel sp ? sp.Parent : null);
                    if (node is VirtualNetworkModel n)
                    {
                        var peering = new VirtualNetworkPeeringModel()
                        {
                            Name = this.Name + "/" + n.Name + "-peering",
                            RemoteVirtualNetworkId = n.ResourceId,
                        };
                        peering.DependsOn.Add(ResourceId);
                        foreach(var subnet in Subnets)
                        {
                            peering.DependsOn.Add(subnet.ResourceId);
                        }
                        peering.DependsOn.Add(n.ResourceId);
                        foreach(var subnet in n.Subnets)
                        {
                            peering.DependsOn.Add(subnet.ResourceId);
                        }
                        result.Add(peering.ArmResource);
                    }
                }
            }

            return result;
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            var vnetDto = mapper.Map<VirtualNetworkModelDto>(this);

            return vnetDto;
        }
    }
}
