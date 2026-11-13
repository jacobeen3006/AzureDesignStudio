using AzureDesignStudio.Core.Network;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    public class Ipv4CidrTests
    {
        [Theory]
        [InlineData("10.0.0.0/16", 0x0A000000u, 16, 65536UL, 0x0A00FFFFu)]
        [InlineData("192.168.5.0/24", 0xC0A80500u, 24, 256UL, 0xC0A805FFu)]
        [InlineData("0.0.0.0/0", 0u, 0, 4294967296UL, 0xFFFFFFFFu)]
        [InlineData("255.255.255.255/32", 0xFFFFFFFFu, 32, 1UL, 0xFFFFFFFFu)]
        [InlineData("128.0.0.0/1", 0x80000000u, 1, 2147483648UL, 0xFFFFFFFFu)]
        [InlineData("  10.0.0.0/8  ", 0x0A000000u, 8, 16777216UL, 0x0AFFFFFFu)]
        public void ParsesCanonicalBlocks(string text, uint network, int prefix, ulong size, uint last)
        {
            Assert.True(Ipv4Cidr.TryParse(text, out var cidr, out var hasHostBits));
            Assert.False(hasHostBits);
            Assert.Equal(network, cidr.Network);
            Assert.Equal(prefix, cidr.PrefixLength);
            Assert.Equal(size, cidr.Size);
            Assert.Equal(last, cidr.Last);
            Assert.Equal(text.Trim(), cidr.ToString());
        }

        [Theory]
        [InlineData("10.0.0.5/24", "10.0.0.0/24")]
        [InlineData("10.0.1.0/23", "10.0.0.0/23")]
        [InlineData("255.255.255.255/0", "0.0.0.0/0")]
        public void NormalisesHostBitsAndReportsThem(string text, string canonical)
        {
            Assert.True(Ipv4Cidr.TryParse(text, out var cidr, out var hasHostBits));
            Assert.True(hasHostBits);
            Assert.Equal(canonical, cidr.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("10.0.0.0")]          // no prefix
        [InlineData("10.0.0.0/")]
        [InlineData("/24")]
        [InlineData("10.0.0.0/33")]
        [InlineData("10.0.0.0/-1")]
        [InlineData("10.0.0.0/+8")]
        [InlineData("10.0.0.0/ 24")]
        [InlineData("10.0.0.0/024")]      // leading zero
        [InlineData("10.0.0.0/24/1")]
        [InlineData("10.0.0/24")]         // three octets
        [InlineData("10.1/16")]           // shorthand IPAddress.Parse would accept
        [InlineData("10.0.0.0.0/24")]
        [InlineData("10.0.0.256/24")]
        [InlineData("010.0.0.0/8")]       // ambiguous octal form
        [InlineData("0x0a.0.0.0/8")]
        [InlineData("10..0.0/8")]
        [InlineData("10.0.0.0 /8")]
        [InlineData("a.b.c.d/8")]
        [InlineData("１０.0.0.0/8")]      // full-width digits
        [InlineData("fd00::/48")]
        public void RejectsEverythingElse(string? text)
        {
            Assert.False(Ipv4Cidr.TryParse(text, out _));
        }

        [Fact]
        public void ContainsAndOverlaps()
        {
            Ipv4Cidr Parse(string s) { Assert.True(Ipv4Cidr.TryParse(s, out var c)); return c; }

            var vnet = Parse("10.0.0.0/16");
            Assert.True(vnet.Contains(Parse("10.0.255.0/24")));
            Assert.True(vnet.Contains(vnet));
            Assert.False(vnet.Contains(Parse("10.1.0.0/24")));
            Assert.False(vnet.Contains(Parse("10.0.0.0/15")));

            Assert.True(Parse("10.0.0.0/24").Overlaps(Parse("10.0.0.128/25")));
            Assert.True(Parse("10.0.0.128/25").Overlaps(Parse("10.0.0.0/24")));
            Assert.False(Parse("10.0.0.0/25").Overlaps(Parse("10.0.0.128/25")));
            Assert.True(Parse("0.0.0.0/0").Overlaps(Parse("255.255.255.255/32")));
        }

        [Theory]
        [InlineData("fd00::/48", true)]
        [InlineData("::/0", true)]
        [InlineData("2001:db8:0:1::/64", true)]
        [InlineData("fd00::/129", false)]
        [InlineData("fd00::", false)]
        [InlineData("10.0.0.0/8", false)]
        [InlineData("not-a-cidr", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void RecognisesIpv6Cidr(string? text, bool expected)
        {
            Assert.Equal(expected, Ipv4Cidr.IsIpv6(text));
        }
    }
}
