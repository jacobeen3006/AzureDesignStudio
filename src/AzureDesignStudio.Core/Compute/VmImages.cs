using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.Core.Compute
{
    /// <summary>A marketplace image a virtual machine or scale set can start from.</summary>
    public sealed record VmImage(string Key, string Label, string Publisher, string Offer, string Sku, bool IsWindows);

    /// <summary>
    /// What a machine needs beyond its name to be deployable, shared by the virtual machine
    /// and the scale set: an image, a computer name that fits the operating system's rules,
    /// and an administrator password that is never written into the design - it is a secure
    /// template parameter the person deploying supplies.
    /// </summary>
    public static class VmImages
    {
        public const string Default = "ubuntu-22.04";
        // A current general-purpose size (2 vCPU, 8 GiB). The earlier Standard_B2s is
        // capacity-restricted or not offered in many regions now, and new subscriptions often
        // have no quota for the burstable B families; Azure refuses a deployment either way.
        public const string DefaultSize = "Standard_D2s_v6";
        public const string DefaultAdminUsername = "azureuser";
        public const string AdminPasswordParameter = "adminPassword";

        public static readonly IReadOnlyList<VmImage> All =
        [
            new("ubuntu-22.04", "Ubuntu Server 22.04 LTS", "Canonical", "0001-com-ubuntu-server-jammy", "22_04-lts-gen2", false),
            new("ubuntu-24.04", "Ubuntu Server 24.04 LTS", "Canonical", "ubuntu-24_04-lts", "server", false),
            new("debian-12", "Debian 12", "Debian", "debian-12", "12-gen2", false),
            new("windows-2022", "Windows Server 2022 Datacenter", "MicrosoftWindowsServer", "WindowsServer", "2022-datacenter-azure-edition", true),
        ];

        /// <summary>The image for a stored key; an unknown or empty key is the default image.</summary>
        public static VmImage Resolve(string? key) =>
            All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

        /// <summary>A host name derived from the resource name: letters, digits and hyphens,
        /// not starting or ending with a hyphen, at most 15 characters on Windows (NetBIOS)
        /// and 63 elsewhere. <paramref name="reserve"/> leaves room for a suffix the platform
        /// appends (scale-set instances).</summary>
        public static string ComputerName(string? resourceName, bool windows, int reserve = 0)
        {
            var cleaned = new string((resourceName ?? string.Empty)
                .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            while (cleaned.Contains("--"))
                cleaned = cleaned.Replace("--", "-");
            if (cleaned.Length == 0 || cleaned.All(char.IsAsciiDigit))
                cleaned = "vm" + cleaned;

            var max = Math.Max(1, (windows ? 15 : 63) - reserve);
            return cleaned.Length > max ? cleaned[..max].TrimEnd('-') : cleaned;
        }

        /// <summary>The secure parameter the exported template asks for. One name for every
        /// machine in a design, so a template with several asks once.</summary>
        public static IDictionary<string, Parameter> AdminPasswordParameters() => new Dictionary<string, Parameter>
        {
            [AdminPasswordParameter] = new Parameter
            {
                Type = "securestring",
                MinLength = 12,
                Metadata = new Dictionary<string, string>
                {
                    ["description"] = "Administrator password for the virtual machines in this template. 12 or more characters with upper case, lower case, a digit and a symbol.",
                },
            },
        };

        /// <summary>Whether the network interface should ask for accelerated networking. Current
        /// sizes (the v6 families among them) require it - a machine without it is refused at
        /// deployment, not at validation - while A-series and first-generation B-series sizes
        /// cannot have it.</summary>
        public static bool UsesAcceleratedNetworking(string? vmSize) =>
            !System.Text.RegularExpressions.Regex.IsMatch(vmSize ?? string.Empty,
                @"^(Basic_|Standard_A|Standard_B\d+[a-z]*$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static bool IsValidAdminUsername(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 20
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            && char.IsAsciiLetter(name[0])
            && !ReservedUsernames.Contains(name);

        // Names Azure refuses for the administrator account.
        private static readonly HashSet<string> ReservedUsernames = new(StringComparer.OrdinalIgnoreCase)
        {
            "administrator", "admin", "user", "user1", "test", "user2", "test1", "user3", "admin1", "1", "123", "a",
            "actuser", "adm", "admin2", "aspnet", "backup", "console", "david", "guest", "john", "owner", "root",
            "server", "sql", "support", "support_388945a0", "sys", "test2", "test3", "user4", "user5",
        };
    }
}
