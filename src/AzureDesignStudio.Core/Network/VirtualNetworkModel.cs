using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;

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

        /// <summary>A subnet ready to add to this VNet: a name no sibling uses and, when one
        /// is free, its own /24. Every subnet used to start as "Subnet1" on 10.0.0.0/24, so
        /// the second one added was a duplicate name with an overlapping range.</summary>
        public SubnetModel NewSubnet()
        {
            var taken = Subnets.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var number = 1;
            while (taken.Contains($"Subnet{number}"))
                number++;

            var subnet = new SubnetModel { Name = $"Subnet{number}" };
            if (NextFreeSubnetPrefix() is { } prefix)
                subnet.AddressPrefix = prefix;
            return subnet;
        }

        /// <summary>Finds the first free aligned /24 across this VNet's IPv4 address
        /// spaces, in configured order. Returns <c>null</c> rather than a guess when no
        /// address space can hold a /24 (exhausted, absent, IPv6-only, narrower than
        /// /24, unparseable) or when an existing subnet's prefix cannot be parsed —
        /// an unknown range could overlap anything.</summary>
        public string? NextFreeSubnetPrefix()
        {
            const ulong blockSize = 256;

            var used = new List<Ipv4Cidr>();
            foreach (var subnet in Subnets)
            {
                var prefix = subnet.AddressPrefix;
                if (string.IsNullOrWhiteSpace(prefix) || Ipv4Cidr.IsIpv6(prefix))
                    continue;
                if (!Ipv4Cidr.TryParse(prefix, out var range))
                    return null;
                used.Add(range);
            }
            used.Sort((a, b) => a.Network.CompareTo(b.Network));

            foreach (var space in IpSpace)
            {
                if (!Ipv4Cidr.TryParse(space.AddressPrefix, out var block) || block.PrefixLength > 24)
                    continue;

                // One sweep over the start-sorted ranges: a range that overlaps the
                // candidate pushes it to the next /24 boundary past that range's end,
                // so cost is O(subnets) no matter how large the address space is.
                ulong candidate = block.Network;
                ulong spaceEnd = block.Last;
                foreach (var range in used)
                {
                    if (candidate + blockSize - 1 > spaceEnd)
                        break;
                    if (range.Last < candidate)
                        continue;
                    if (range.Network > candidate + blockSize - 1)
                        break;
                    candidate = ((ulong)range.Last / blockSize + 1) * blockSize;
                }

                if (candidate + blockSize - 1 <= spaceEnd)
                    return Ipv4Cidr.Format((uint)candidate, 24);
            }

            return null;
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

            foreach (var subnet in Subnets)
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
                        foreach (var subnet in Subnets)
                        {
                            peering.DependsOn.Add(subnet.ResourceId);
                        }
                        peering.DependsOn.Add(n.ResourceId);
                        foreach (var subnet in n.Subnets)
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
