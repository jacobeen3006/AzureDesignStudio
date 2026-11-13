namespace AzureDesignStudio.Core.Network
{
    /// <summary>An IPv4 CIDR block, normalised to its network address. Range maths is
    /// done in <see cref="ulong"/> so <c>/0</c> and the top of the address space
    /// (<c>255.255.255.0/24</c>) never wrap.</summary>
    public readonly record struct Ipv4Cidr(uint Network, int PrefixLength)
    {
        /// <summary>Number of addresses in the block (2^32 for <c>/0</c>).</summary>
        public ulong Size => 1UL << (32 - PrefixLength);

        /// <summary>Last address in the block, inclusive.</summary>
        public uint Last => (uint)(Network + Size - 1);

        public bool Contains(Ipv4Cidr other) => other.Network >= Network && other.Last <= Last;

        public bool Overlaps(Ipv4Cidr other) => Network <= other.Last && other.Network <= Last;

        public override string ToString() => Format(Network, PrefixLength);

        public static string Format(uint address, int prefixLength) =>
            $"{address >> 24}.{(address >> 16) & 0xff}.{(address >> 8) & 0xff}.{address & 0xff}/{prefixLength}";

        public static bool TryParse(string? text, out Ipv4Cidr cidr) => TryParse(text, out cidr, out _);

        /// <summary>Strict dotted-decimal parse: four octets, a <c>/0</c>–<c>/32</c> prefix,
        /// no leading zeros, signs, whitespace inside, or shorthand forms that
        /// <see cref="System.Net.IPAddress"/> would accept (<c>10.1</c>, <c>0x0a.0.0.0</c>).
        /// <paramref name="hasHostBits"/> reports an address that is not the network
        /// address for its prefix (<c>10.0.0.5/24</c>), which Azure rejects.</summary>
        public static bool TryParse(string? text, out Ipv4Cidr cidr, out bool hasHostBits)
        {
            cidr = default;
            hasHostBits = false;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var span = text.AsSpan().Trim();
            var slash = span.IndexOf('/');
            if (slash <= 0 || slash != span.LastIndexOf('/'))
                return false;

            if (!TryParseNumber(span[(slash + 1)..], 2, 32, out var prefix))
                return false;

            var address = span[..slash];
            uint value = 0;
            for (var octet = 0; octet < 4; octet++)
            {
                var dot = address.IndexOf('.');
                if (octet < 3 ? dot <= 0 : dot >= 0)
                    return false;

                var part = octet < 3 ? address[..dot] : address;
                if (!TryParseNumber(part, 3, 255, out var number))
                    return false;

                value = (value << 8) | (uint)number;
                if (octet < 3)
                    address = address[(dot + 1)..];
            }

            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            hasHostBits = (value & ~mask) != 0;
            cidr = new Ipv4Cidr(value & mask, prefix);
            return true;
        }

        /// <summary>True when the text is a syntactically valid IPv6 CIDR. Dual-stack VNets
        /// carry these; they are valid, they just never overlap an IPv4 range.</summary>
        public static bool IsIpv6(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var span = text.AsSpan().Trim();
            var slash = span.IndexOf('/');
            return slash > 0
                && span[..slash].Contains(':')
                && TryParseNumber(span[(slash + 1)..], 3, 128, out _)
                && System.Net.IPAddress.TryParse(span[..slash], out var ip)
                && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        private static bool TryParseNumber(ReadOnlySpan<char> digits, int maxDigits, int max, out int value)
        {
            value = 0;
            if (digits.Length == 0 || digits.Length > maxDigits || (digits.Length > 1 && digits[0] == '0'))
                return false;

            foreach (var c in digits)
            {
                if (c is < '0' or > '9')
                    return false;
                value = value * 10 + (c - '0');
            }

            return value <= max;
        }
    }
}
