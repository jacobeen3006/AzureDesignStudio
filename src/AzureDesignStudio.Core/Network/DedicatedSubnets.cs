namespace AzureDesignStudio.Core.Network
{
    /// <summary>
    /// The subnet names Azure reserves for particular services. One place decides whether a
    /// subnet "is" one of them, so placement, validity, the forms and the auto-provision
    /// lookup cannot disagree: they used to compare with different casing rules, and a subnet
    /// called <c>azurefirewallsubnet</c> was found as the firewall subnet and then rejected
    /// as a place to put the firewall.
    ///
    /// ARM resource names are case-insensitive, so matching ignores case; the export always
    /// writes the documented spelling (<see cref="Canonical"/>), which is right whichever way
    /// a service validates the name.
    /// </summary>
    public static class DedicatedSubnets
    {
        public const string AzureFirewall = "AzureFirewallSubnet";
        public const string AzureBastion = "AzureBastionSubnet";

        private static readonly string[] Reserved =
        [
            AzureFirewall,
            AzureBastion,
            "AzureFirewallManagementSubnet",
            "GatewaySubnet",
            "RouteServerSubnet",
        ];

        public static bool Is(string? subnetName, string dedicatedName) =>
            string.Equals(subnetName?.Trim(), dedicatedName, StringComparison.OrdinalIgnoreCase);

        public static bool Is(SubnetModel? subnet, string dedicatedName) => Is(subnet?.Name, dedicatedName);

        /// <summary>True when the name is one Azure reserves for a particular service.</summary>
        public static bool IsReserved(string? subnetName) =>
            Reserved.Any(reserved => Is(subnetName, reserved));

        /// <summary>The documented spelling of a reserved name, or the name unchanged.</summary>
        public static string Canonical(string subnetName) =>
            Reserved.FirstOrDefault(reserved => Is(subnetName, reserved)) ?? subnetName;
    }
}
