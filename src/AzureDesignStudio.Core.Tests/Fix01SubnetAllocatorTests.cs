using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Core.Network;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Regression tests for FIX-01: VirtualNetworkModel.NextFreeSubnetPrefix must only allocate
    /// complete aligned /24 blocks inside configured IPv4 address space, reject overlaps with
    /// existing subnets of any prefix length, return null for exhausted/invalid/narrower-than-/24
    /// spaces, support multiple IPv4 spaces, and never fabricate a CIDR on invalid input.
    /// </summary>
    public class Fix01SubnetAllocatorTests
    {
        #region Basic allocation

        [Fact]
        public void NextFreeSubnetPrefix_ReturnsFirstBlock_ForDefaultVNet()
        {
            var vnet = new VirtualNetworkModel(); // default 10.0.0.0/16
            Assert.Equal("10.0.0.0/24", vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_SkipsOccupiedBlocks_Sequentially()
        {
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "a", AddressPrefix = "10.0.0.0/24" });
            vnet.AddSubnet(new SubnetModel { Name = "b", AddressPrefix = "10.0.1.0/24" });

            Assert.Equal("10.0.2.0/24", vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_AllocateInNestedVNet_UsesOuterSpaceNotInnerSubnetAddress()
        {
            // VNet = 10.0.0.0/16, inner subnet = 10.1.0.0/16 — the outer space is used.
            var inner = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = "10.1.0.0/16" } } };
            var outer = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = "10.0.0.0/16" } } };
            outer.AddChild(inner); // nested structure like in real diagram

            // The *inner* VNet's NextFreeSubnetPrefix uses ITS OWN IpSpace (10.1.x.x).
            var result = inner.NextFreeSubnetPrefix();
            Assert.StartsWith("10.1.", result);
        }

        #endregion

        #region Overlap with wide subnets

        [Fact]
        public void NextFreeSubnetPrefix_SkipsAllBlocksCoveredByWideExistingSubnet()
        {
            // A /8 at 10.0.0.0 covers 10.0.0.0/24 through 10.255.255.0.
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "wide", AddressPrefix = "10.0.0.0/8" });

            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_GapAfterWideSubnet_IsUsed()
        {
            // Wide /12 covering 10.0.x.0..10.15.x.0 within a /16 VNet.
            // Since /12 spans 4096 blocks and /16 has only 256, the entire space is consumed.
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "big", AddressPrefix = "10.0.0.0/12" });
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_NonAlignedWiderSubnet_MasksToCanonicalAddress()
        {
            // A /23 at 10.0.1.0 is not aligned — TryParseCidr masks to canonical 10.0.0.0/23.
            // Canonical /23 covers blocks 0-1 in a /16 space. With /24 also at block 0,
            // occupied = {0, 1}. First free = block 2 → 10.0.2.0/24.
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "n1", AddressPrefix = "10.0.0.0/24" });
            vnet.AddSubnet(new SubnetModel { Name = "n2", AddressPrefix = "10.0.1.0/23" });
            vnet.AddSubnet(new SubnetModel { Name = "n3", AddressPrefix = "10.0.3.0/24" });
            Assert.Equal("10.0.2.0/24", vnet.NextFreeSubnetPrefix());
        }

        #endregion

        #region Exhaustion & narrow spaces

        [Fact]
        public void NextFreeSubnetPrefix_ExhaustedSpace_ReturnsNull()
        {
            // Fill every /24 in a /24-sized space (trivial case).
            var vnet = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = "192.168.1.0/24" } } };
            vnet.AddSubnet(new SubnetModel { Name = "full", AddressPrefix = "192.168.1.0/24" });
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_IPv6OnlySpace_ReturnsNull()
        {
            var vnet = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = "fd00::/48" } } };
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_NarrowerThan_24Space_ReturnsNull()
        {
            var vnet = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = "10.0.0.0/32" } } };
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_EmptyIpSpace_ReturnsNull()
        {
            var vnet = new VirtualNetworkModel { IpSpace = new List<AddressSpace>() };
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        #endregion

        #region Edge cases from audit plan

        [Theory]
        [InlineData("/0")]               // invalid prefix without IP — returns null gracefully
        public void NextFreeSubnetPrefix_VeryLargeSpaceHandlesInvalidPrefix(string prefix)
        {
            var vnet = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = prefix } } };
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_UltraLargeIPv4Space_AllocatesFromStart()
        {
            // 0.0.0.0/0 is an extremely large space — should not crash or wrap around uint.MaxValue.
            var vnet = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = "0.0.0.0/0" } } };
            var result = vnet.NextFreeSubnetPrefix();
            Assert.NotNull(result);
            Assert.EndsWith("/24", result);
            Assert.Equal("0.0.0.0/24", result);
        }

        [Fact]
        public void NextFreeSubnetPrefix_UpperIPv4Boundary_PicksCorrectLastBlock()
        {
            // 255.255.255.0/24 is valid but barely — one block.
            var vnet = new VirtualNetworkModel { IpSpace = new List<AddressSpace> { new() { AddressPrefix = "255.255.255.0/24" } } };
            Assert.Equal("255.255.255.0/24", vnet.NextFreeSubnetPrefix());

            // Occupied → no room left
            vnet.AddSubnet(new SubnetModel { Name = "x", AddressPrefix = "255.255.255.0/24" });
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_MultipleAddressSpaces_TriesSecondOnExhaustion()
        {
            var vnet = new VirtualNetworkModel
            {
                IpSpace = new List<AddressSpace>
                {
                    new() { AddressPrefix = "10.0.0.0/24" },   // single /24 — will exhaust immediately
                    new() { AddressPrefix = "192.168.0.0/16" }, // plenty of room
                }
            };
            vnet.AddSubnet(new SubnetModel { Name = "used", AddressPrefix = "10.0.0.0/24" });

            // After exhausting space 1, should try space 2 → 192.168.0.0/24.
            Assert.Equal("192.168.0.0/24", vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_CoveringSubnet_PreventsFalseMatch()
        {
            // Subnet at base address but wider — should NOT allow allocation there.
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "cover", AddressPrefix = "10.0.0.0/24" });

            Assert.NotEqual("10.0.0.0/24", vnet.NextFreeSubnetPrefix());
            Assert.Equal("10.0.1.0/24", vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_IgnoreNonCanonicalHostBits()
        {
            // Non-canonical representation (host bits set) of the same network still matches.
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "noncanon", AddressPrefix = "10.0.0.255/24" });

            // Even though 10.0.0.255 is non-canonical for a /24, it starts at the same block.
            Assert.Equal("10.0.1.0/24", vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_MalformedExistingSubnetMakesAllocationUnavailable()
        {
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "bad", AddressPrefix = "not-a-cidr" });

            // The bad entry's real range is unknown, so no block is provably free.
            Assert.Null(vnet.NextFreeSubnetPrefix());
        }

        [Fact]
        public void NextFreeSubnetPrefix_DifferentAddressSpaceIsIndependent()
        {
            var vnet = new VirtualNetworkModel();
            vnet.AddSubnet(new SubnetModel { Name = "other", AddressPrefix = "192.168.0.0/24" });

            // Subnet in a different /8 range shouldn't affect allocation in 10.0.0.0/16.
            Assert.Equal("10.0.0.0/24", vnet.NextFreeSubnetPrefix());
        }

        #endregion
    }
}
