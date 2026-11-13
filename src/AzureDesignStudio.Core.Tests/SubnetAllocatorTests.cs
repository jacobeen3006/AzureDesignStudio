using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Core.Network;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// <see cref="VirtualNetworkModel.NextFreeSubnetPrefix"/> hands out a whole aligned /24
    /// inside one of the VNet's IPv4 address spaces, never one that overlaps an existing
    /// subnet of any size, and returns null instead of guessing when it cannot be sure.
    /// </summary>
    public class SubnetAllocatorTests
    {
        private static VirtualNetworkModel Vnet(params string[] spaces) => new()
        {
            IpSpace = spaces.Select(s => new AddressSpace { AddressPrefix = s }).ToList(),
        };

        private static VirtualNetworkModel WithSubnets(VirtualNetworkModel vnet, params string[] prefixes)
        {
            for (var i = 0; i < prefixes.Length; i++)
                vnet.AddSubnet(new SubnetModel { Name = $"s{i}", AddressPrefix = prefixes[i] });
            return vnet;
        }

        [Fact]
        public void DefaultVnetStartsAtItsFirstBlock()
        {
            Assert.Equal("10.0.0.0/24", new VirtualNetworkModel().NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("10.0.2.0/24", "10.0.0.0/24", "10.0.1.0/24")]
        [InlineData("10.0.2.0/24", "10.0.1.0/24", "10.0.0.0/24")] // order of existing subnets is irrelevant
        [InlineData("10.0.1.0/24", "10.0.0.0/24", "10.0.2.0/24")] // first gap, not one past the highest
        [InlineData("10.0.0.0/24", "10.0.1.0/24")]
        public void SkipsOccupiedBlocks(string expected, params string[] existing)
        {
            Assert.Equal(expected, WithSubnets(new VirtualNetworkModel(), existing).NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("10.0.0.0/26")]    // first quarter of the block
        [InlineData("10.0.0.128/25")]  // upper half
        [InlineData("10.0.0.252/30")]  // last four addresses
        [InlineData("10.0.0.7/32")]    // a single host
        public void SubnetSmallerThanSlash24StillOccupiesItsBlock(string existing)
        {
            Assert.Equal("10.0.1.0/24", WithSubnets(new VirtualNetworkModel(), existing).NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("10.0.0.0/23", "10.0.2.0/24")]
        [InlineData("10.0.1.0/23", "10.0.2.0/24")]    // host bits set: still the 10.0.0.0/23 range
        [InlineData("10.0.0.0/17", "10.0.128.0/24")]  // lower half of the VNet
        [InlineData("10.0.0.99/24", "10.0.1.0/24")]   // host bits set on a /24
        public void SubnetLargerThanSlash24OccupiesEveryBlockItCovers(string existing, string expected)
        {
            Assert.Equal(expected, WithSubnets(new VirtualNetworkModel(), existing).NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("10.0.0.0/16")]
        [InlineData("10.0.0.0/12")]
        [InlineData("10.0.0.0/8")]
        [InlineData("0.0.0.0/0")]
        public void SubnetCoveringTheWholeAddressSpaceLeavesNothing(string existing)
        {
            Assert.Null(WithSubnets(new VirtualNetworkModel(), existing).NextFreeSubnetPrefix());
        }

        [Fact]
        public void SubnetOutsideTheAddressSpaceDoesNotConsumeIt()
        {
            Assert.Equal("10.0.0.0/24", WithSubnets(new VirtualNetworkModel(), "192.168.0.0/24").NextFreeSubnetPrefix());
        }

        [Fact]
        public void ExhaustedAddressSpaceReturnsNull()
        {
            Assert.Null(WithSubnets(Vnet("192.168.1.0/24"), "192.168.1.0/24").NextFreeSubnetPrefix());
            Assert.Null(WithSubnets(Vnet("192.168.0.0/23"), "192.168.0.0/25", "192.168.1.64/26").NextFreeSubnetPrefix());

            var full = Vnet("10.0.0.0/16");
            WithSubnets(full, Enumerable.Range(0, 256).Select(i => $"10.0.{i}.0/24").ToArray());
            Assert.Null(full.NextFreeSubnetPrefix());
        }

        [Fact]
        public void LastRemainingBlockIsFound()
        {
            var vnet = Vnet("10.0.0.0/16");
            WithSubnets(vnet, Enumerable.Range(0, 256).Where(i => i != 200).Select(i => $"10.0.{i}.0/24").ToArray());

            Assert.Equal("10.0.200.0/24", vnet.NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("10.0.0.0/25")]   // cannot hold a /24: the old allocator returned one that stuck out
        [InlineData("10.0.0.0/32")]
        [InlineData("fd00::/48")]     // IPv6 only
        [InlineData("not-a-cidr")]
        [InlineData("/0")]
        [InlineData("")]
        public void AddressSpaceThatCannotHoldASlash24ReturnsNull(string space)
        {
            Assert.Null(Vnet(space).NextFreeSubnetPrefix());
        }

        [Fact]
        public void NoAddressSpaceReturnsNull()
        {
            Assert.Null(Vnet().NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("10.0.0.0/24")]   // exhausted by the existing subnet below
        [InlineData("10.0.0.0/28")]   // too narrow
        [InlineData("fd00::/48")]     // IPv6
        [InlineData("garbage")]
        public void LaterAddressSpaceIsUsedWhenAnEarlierOneCannotServe(string first)
        {
            var vnet = WithSubnets(Vnet(first, "192.168.0.0/16"), "10.0.0.0/24");

            Assert.Equal("192.168.0.0/24", vnet.NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("not-a-cidr")]
        [InlineData("10.0.0.0")]
        [InlineData("10.0.0.0/33")]
        [InlineData("10.0.0/24")]
        public void UnparseableExistingSubnetMakesAllocationUnavailable(string existing)
        {
            // Its real range is unknown, so any block could overlap it.
            Assert.Null(WithSubnets(new VirtualNetworkModel(), existing).NextFreeSubnetPrefix());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("fd00:0:0:1::/64")]
        public void BlankOrIpv6ExistingSubnetOccupiesNoIpv4Range(string existing)
        {
            Assert.Equal("10.0.0.0/24", WithSubnets(new VirtualNetworkModel(), existing).NextFreeSubnetPrefix());
        }

        [Fact]
        public void WholeInternetAddressSpaceIsHandledWithoutScanning()
        {
            Assert.Equal("0.0.0.0/24", Vnet("0.0.0.0/0").NextFreeSubnetPrefix());
            Assert.Equal("128.0.0.0/24", WithSubnets(Vnet("0.0.0.0/0"), "0.0.0.0/1").NextFreeSubnetPrefix());
            Assert.Null(WithSubnets(Vnet("0.0.0.0/0"), "0.0.0.0/1", "128.0.0.0/1").NextFreeSubnetPrefix());
        }

        [Fact]
        public void TopOfTheIpv4RangeDoesNotWrapAround()
        {
            Assert.Equal("255.255.255.0/24", Vnet("255.255.255.0/24").NextFreeSubnetPrefix());
            Assert.Null(WithSubnets(Vnet("255.255.255.0/24"), "255.255.255.0/24").NextFreeSubnetPrefix());
            Assert.Equal("255.255.255.0/24", WithSubnets(Vnet("255.255.254.0/23"), "255.255.254.0/24").NextFreeSubnetPrefix());
            Assert.Null(WithSubnets(Vnet("255.255.254.0/23"), "255.255.254.0/24", "255.255.255.128/25").NextFreeSubnetPrefix());
            Assert.Null(WithSubnets(Vnet("128.0.0.0/1"), "128.0.0.0/1").NextFreeSubnetPrefix());
        }

        [Fact]
        public void AddressSpaceWithHostBitsIsReadAsItsNetwork()
        {
            Assert.Equal("10.1.0.0/24", Vnet("10.1.2.3/16").NextFreeSubnetPrefix());
        }

        /// <summary>Compares the allocator with a brute-force walk over every /24 of
        /// every address space, for a few thousand generated layouts.</summary>
        [Fact]
        public void AgreesWithBruteForceOnGeneratedLayouts()
        {
            var random = new Random(20261112);
            for (var round = 0; round < 3000; round++)
            {
                var spaces = Enumerable.Range(0, random.Next(1, 3))
                    .Select(_ => RandomCidr(random, 18, 26, 0x0A000000u, 14))
                    .ToList();
                var subnets = Enumerable.Range(0, random.Next(0, 12))
                    .Select(_ => RandomCidr(random, 17, 30, 0x0A000000u, 14))
                    .ToList();

                var vnet = WithSubnets(
                    Vnet(spaces.Select(s => s.ToString()).ToArray()),
                    subnets.Select(s => s.ToString()).ToArray());

                string? expected = null;
                foreach (var space in spaces.Where(s => s.PrefixLength <= 24))
                {
                    for (ulong start = space.Network; start + 255 <= space.Last && expected is null; start += 256)
                    {
                        var block = new Ipv4Cidr((uint)start, 24);
                        if (!subnets.Any(block.Overlaps))
                            expected = block.ToString();
                    }

                    if (expected is not null)
                        break;
                }

                Assert.Equal(expected, vnet.NextFreeSubnetPrefix());
            }
        }

        // A random block somewhere in a 2^regionBits-address window above 'region'.
        private static Ipv4Cidr RandomCidr(Random random, int minPrefix, int maxPrefix, uint region, int regionBits)
        {
            var prefix = random.Next(minPrefix, maxPrefix + 1);
            var address = region | (uint)random.Next(0, 1 << regionBits);
            var mask = uint.MaxValue << (32 - prefix);
            return new Ipv4Cidr(address & mask, prefix);
        }
    }
}
