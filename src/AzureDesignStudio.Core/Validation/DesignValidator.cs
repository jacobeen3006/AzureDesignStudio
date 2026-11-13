// Licensed under the MIT License. See LICENSE in the project root for license information.
using System.Text.RegularExpressions;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.App;
using AzureDesignStudio.Core.Cache;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Fabric;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Storage;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Validation
{
    /// <summary>
    /// Cross-node design validator: naming rules, parent/child placement,
    /// SKU/region availability and subnet delegation requirements.
    /// </summary>
    public static class DesignValidator
    {
        private static readonly Dictionary<Type, (Regex Pattern, string Hint)> NameRules = new()
        {
            [typeof(StorageAccountModel)] = (new Regex("^[a-z0-9]{3,24}$"), "Storage account name must be 3-24 lowercase letters and digits."),
            [typeof(SqlServerModel)] = (new Regex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$"), "SQL server name must be 1-63 lowercase letters, digits and hyphens, and cannot start or end with a hyphen."),
            [typeof(SqlDatabaseModel)] = (new Regex("^[a-zA-Z0-9_-]{1,128}$"), "SQL database name must be 1-128 alphanumerics/_/-."),
            [typeof(AppServicePlanModel)] = (new Regex("^[a-zA-Z0-9][a-zA-Z0-9-]{0,58}[a-zA-Z0-9]$", RegexOptions.IgnoreCase), "App service plan name must be 2-60 alphanumerics/hyphens."),
            [typeof(WebAppModel)] = (new Regex("^[a-zA-Z][a-zA-Z0-9-]{0,58}[a-zA-Z0-9]$", RegexOptions.IgnoreCase), "Web app name must start with a letter, 2-60 chars, alphanumerics/hyphens."),
            [typeof(FunctionAppModel)] = (new Regex("^[a-zA-Z][a-zA-Z0-9-]{1,62}[a-zA-Z0-9]$", RegexOptions.IgnoreCase), "Function app name must start with a letter, 2-63 chars, alphanumerics/hyphens."),
            [typeof(APIMModel)] = (new Regex("^[a-zA-Z][a-zA-Z0-9-]{1,59}[a-zA-Z0-9]$", RegexOptions.IgnoreCase), "APIM name must start with a letter, 2-62 chars, alphanumerics/hyphens."),
        };

        // type -> (sku, regions where the sku is available); null regions = no constraint.
        private static readonly Dictionary<Type, Func<IAzureNode, (string Sku, HashSet<string>? Regions)>> SkuRegionRules = new()
        {
            [typeof(FabricCapacityModel)] = n => (((FabricCapacityModel)n).Sku, FabricRegions),
            [typeof(AzureRedisModel)] = n => (((AzureRedisModel)n).Family == "P" ? ("Premium", RedisPremiumRegions) : (string.Empty, null)),
        };

        private static readonly HashSet<string> FabricRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            "westus2", "westus3", "eastus", "eastus2", "centralus", "northcentralus",
            "southcentralus", "westus", "canadacentral", "centralindia", "japaneast",
            "koreacentral", "northeurope", "westeurope", "uksouth", "francecentral",
            "germanywestcentral", "norwayeast", "switzerlandnorth", "swedencentral",
            "brazilsouth", "australiaeast", "southeastasia", "uaenorth", "southafricanorth",
        };

        private static readonly HashSet<string> RedisPremiumRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            "westus", "westus2", "westus3", "eastus", "eastus2", "eastus2euap", "centralus",
            "northcentralus", "southcentralus", "westcentralus", "canadacentral", "canadaeast",
            "brazilsouth", "northeurope", "westeurope", "uksouth", "francecentral",
            "germanywestcentral", "norwayeast", "switzerlandnorth", "swedencentral",
            "uaenorth", "southafricanorth", "australiaeast", "australiasoutheast",
            "southeastasia", "eastasia", "japaneast", "japanwest", "koreacentral",
        };

        public static IReadOnlyList<ValidationIssue> Validate(IEnumerable<NodeModel> nodes)
        {
            var list = nodes.ToList();
            var issues = new List<ValidationIssue>();

            foreach (var node in list)
            {
                if (node is not IAzureNode azure)
                    continue;

                var name = (node as IAzureResource)?.Name ?? string.Empty;

                // Per-node configuration validity (name/sku rules owned by each model).
                if (!azure.IsValid)
                    issues.Add(new(ValidationSeverity.Error, "node-config", name, $"{azure.ServiceName} '{name}' has an invalid configuration."));

                // ARM naming rules for models without their own IsValid check.
                if (NameRules.TryGetValue(node.GetType(), out var rule) && !rule.Pattern.IsMatch(name))
                    issues.Add(new(ValidationSeverity.Error, "naming", name, rule.Hint));

                // Parent/child placement.
                switch (node)
                {
                    case SqlDatabaseModel { Group: not SqlServerModel }:
                        issues.Add(new(ValidationSeverity.Error, "placement", name, $"SQL database '{name}' must be placed inside a SQL server."));
                        break;
                    case ContainerAppModel { Group: not ContainerAppsEnvironmentModel }:
                        issues.Add(new(ValidationSeverity.Error, "placement", name, $"Container app '{name}' must be placed inside a Container Apps environment."));
                        break;
                    case ContainerAppsJobModel { Group: not ContainerAppsEnvironmentModel }:
                        issues.Add(new(ValidationSeverity.Error, "placement", name, $"Container apps job '{name}' must be placed inside a Container Apps environment."));
                        break;
                    case SubnetModel { Group: not VirtualNetworkModel }:
                        issues.Add(new(ValidationSeverity.Error, "placement", name, $"Subnet '{name}' must be placed inside a virtual network."));
                        break;
                    case AppServicePlanModel functionPlan
                        when functionPlan.Children.OfType<FunctionAppModel>().FirstOrDefault(f => f.HostingPlan is "consumption" or "premium") is { } function:
                        // The function's hosting plan sets the plan's SKU at export (Y1 / EP1); a
                        // web app on the same plan would silently be deployed onto that SKU.
                        foreach (var webApp in functionPlan.Children.Where(c => c is WebAppModel and not FunctionAppModel).OfType<WebAppModel>())
                            issues.Add(new(ValidationSeverity.Error, "placement", webApp.Name,
                                $"App service plan '{name}' is a {(function.HostingPlan == "consumption" ? "Consumption" : "Elastic Premium")} plan because of function app '{function.Name}'; web app '{webApp.Name}' needs a plan of its own."));
                        break;
                    case AvailabilitySetModel availabilitySet:
                        foreach (var child in availabilitySet.Children.Where(c => c is not VirtualMachineModel))
                            issues.Add(new(ValidationSeverity.Error, "placement", (child as IAzureResource)?.Name ?? string.Empty,
                                $"Availability set '{name}' can only contain virtual machines."));
                        break;
                }

                // Required/expected child resources: deployable but pointless when empty.
                switch (node)
                {
                    case AppServicePlanModel plan when !plan.Children.Any(c => c is WebAppModel or FunctionAppModel or ContainerAppModel):
                        issues.Add(new(ValidationSeverity.Warning, "required-children", name,
                            $"App service plan '{name}' has no apps deployed."));
                        break;
                    case AvailabilitySetModel set when !set.Children.OfType<VirtualMachineModel>().Any():
                        issues.Add(new(ValidationSeverity.Warning, "required-children", name,
                            $"Availability set '{name}' has no virtual machines."));
                        break;
                    case ContainerAppsEnvironmentModel env when env.Children.Count == 0:
                        issues.Add(new(ValidationSeverity.Warning, "required-children", name,
                            $"Container Apps environment '{name}' has no apps or jobs."));
                        break;
                }

                // SKU/region availability.
                if (SkuRegionRules.TryGetValue(node.GetType(), out var skuRule))
                {
                    var (sku, regions) = skuRule(azure);
                    var location = (node as AzureNodeBase)?.Location ?? string.Empty;
                    if (!string.IsNullOrEmpty(sku) && regions is not null
                        && !location.StartsWith('[') // parameterized location cannot be checked statically
                        && !regions.Contains(location))
                        issues.Add(new(ValidationSeverity.Warning, "sku-region", name,
                            $"{azure.ServiceName} '{name}' sku '{sku}' may not be available in region '{location}'."));
                }
            }

            AddDuplicateNameIssues(list, issues);
            foreach (var vnet in list.OfType<VirtualNetworkModel>())
                AddAddressSpaceIssues(vnet, issues);
            AddOutboundAccessIssues(list, issues);

            // Container Apps environment requires a delegated subnet somewhere in the design.
            if (list.OfType<ContainerAppsEnvironmentModel>().Any()
                && !list.OfType<SubnetModel>().Any(s => s.Delegation == "Microsoft.App/environments"))
                issues.Add(new(ValidationSeverity.Warning, "delegation", "container-apps",
                    "Container Apps environment requires a subnet delegated to Microsoft.App/environments."));

            return issues;
        }

        /// <summary>A subnet that hosts machines needs an explicit way out: Azure is retiring
        /// default outbound access for new virtual networks. A NAT gateway is the one path the
        /// design models; a public IP on a machine and load balancer outbound rules are not
        /// exported, so they are not counted.</summary>
        private static void AddOutboundAccessIssues(List<NodeModel> nodes, List<ValidationIssue> issues)
        {
            foreach (var subnet in nodes.OfType<SubnetModel>())
            {
                if (!subnet.SupportsPrivateSubnet || !HostsMachines(subnet) || subnet.NatGateway is not null)
                    continue;

                var name = subnet.Name;
                if (subnet.Group is VirtualNetworkModel vnet && HasFirewall(vnet))
                    issues.Add(new(ValidationSeverity.Info, "outbound-access", name,
                        $"Subnet '{name}' has no NAT gateway. If outbound traffic goes through the firewall, add the route table after export; this design does not model routes."));
                else if (subnet.PrivateSubnet)
                    issues.Add(new(ValidationSeverity.Warning, "outbound-access", name,
                        $"Subnet '{name}' is private and has no outbound path. Its machines cannot reach the internet, including Windows activation and updates. Link a NAT gateway."));
                else
                    issues.Add(new(ValidationSeverity.Warning, "outbound-access", name,
                        $"Subnet '{name}' relies on default outbound access, which Azure is retiring for new virtual networks. Link a NAT gateway, or mark the subnet private once it has an outbound path."));
            }
        }

        private static bool HostsMachines(SubnetModel subnet) =>
            subnet.Children.SelectMany(c => c is AvailabilitySetModel set ? set.Children : [c])
                .Any(c => c is VirtualMachineModel or VirtualMachineScaleSetModel);

        /// <summary>An Azure Firewall in this network or in one linked to it (a peering).</summary>
        private static bool HasFirewall(VirtualNetworkModel vnet) =>
            ContainsFirewall(vnet)
            || vnet.Ports.SelectMany(p => p.Links)
                .SelectMany(l => new[] { (l.Source.Model as PortModel)?.Parent, (l.Target.Model as PortModel)?.Parent })
                .OfType<VirtualNetworkModel>()
                .Any(peer => !ReferenceEquals(peer, vnet) && ContainsFirewall(peer));

        private static bool ContainsFirewall(VirtualNetworkModel vnet) =>
            vnet.Subnets.Any(s => s.Children.OfType<AzureFirewallModel>().Any());

        /// <summary>ARM refuses a template that defines one resource twice. Two nodes of the
        /// same kind with the same name are that template, and so are two firewalls left on
        /// the same firewall-policy name.</summary>
        private static void AddDuplicateNameIssues(List<NodeModel> nodes, List<ValidationIssue> issues)
        {
            var named = nodes
                .Where(n => n is IAzureNode and IAzureResource and not UnknownNodeModel)
                .Select(n => (Node: n, Name: ((IAzureResource)n).Name?.Trim() ?? string.Empty))
                .Where(n => n.Name.Length > 0);

            // A subnet or a database only has to be unique inside its own network or server.
            foreach (var group in named.GroupBy(n => (n.Node.GetType(), Scope: n.Node is SubnetModel or SqlDatabaseModel ? n.Node.Group : null, Name: n.Name.ToUpperInvariant()))
                         .Where(g => g.Count() > 1))
            {
                var first = group.First();
                issues.Add(new(ValidationSeverity.Error, "duplicate-name", first.Name,
                    $"{group.Count()} {((IAzureNode)first.Node).ServiceName} resources are named '{first.Name}'; each needs its own name."));
            }

            foreach (var group in nodes.OfType<AzureFirewallModel>()
                         .GroupBy(f => f.FirewallPolicy?.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Key.Length > 0 && g.Count() > 1))
                issues.Add(new(ValidationSeverity.Error, "duplicate-name", group.Key,
                    $"Firewalls {string.Join(", ", group.Select(f => $"'{f.Name}'"))} all use the firewall policy name '{group.Key}'; give each firewall its own policy name."));
        }

        /// <summary>Address ranges Azure would reject at deployment: a prefix that is not a
        /// CIDR block or is not the network address for its length, a subnet outside every
        /// address space of its virtual network, and subnets that overlap one another.</summary>
        private static void AddAddressSpaceIssues(VirtualNetworkModel vnet, List<ValidationIssue> issues)
        {
            var vnetName = vnet.Name;
            var spaces = new List<Ipv4Cidr>();
            foreach (var space in vnet.IpSpace)
            {
                if (Ipv4Cidr.IsIpv6(space.AddressPrefix))
                    continue;
                if (!Ipv4Cidr.TryParse(space.AddressPrefix, out var block, out var hostBits))
                    issues.Add(new(ValidationSeverity.Error, "address-space", vnetName,
                        $"Virtual network '{vnetName}' has an address space '{space.AddressPrefix}' that is not a valid CIDR block (for example 10.0.0.0/16)."));
                else
                {
                    if (hostBits)
                        issues.Add(new(ValidationSeverity.Error, "address-space", vnetName,
                            $"Virtual network '{vnetName}' address space '{space.AddressPrefix.Trim()}' is not a network address; use {block}."));
                    spaces.Add(block);
                }
            }

            if (vnet.IpSpace.Count == 0)
                issues.Add(new(ValidationSeverity.Error, "address-space", vnetName, $"Virtual network '{vnetName}' has no address space."));

            var placed = new List<(SubnetModel Subnet, Ipv4Cidr Range)>();
            foreach (var subnet in vnet.Subnets)
            {
                if (Ipv4Cidr.IsIpv6(subnet.AddressPrefix))
                    continue;
                if (!Ipv4Cidr.TryParse(subnet.AddressPrefix, out var range, out var hostBits))
                {
                    issues.Add(new(ValidationSeverity.Error, "address-space", subnet.Name,
                        $"Subnet '{subnet.Name}' has an address prefix '{subnet.AddressPrefix}' that is not a valid CIDR block (for example 10.0.1.0/24)."));
                    continue;
                }

                if (hostBits)
                    issues.Add(new(ValidationSeverity.Error, "address-space", subnet.Name,
                        $"Subnet '{subnet.Name}' prefix '{subnet.AddressPrefix.Trim()}' is not a network address; use {range}."));

                if (spaces.Count > 0 && !spaces.Any(space => space.Contains(range)))
                    issues.Add(new(ValidationSeverity.Error, "address-space", subnet.Name,
                        $"Subnet '{subnet.Name}' ({range}) is outside the address space of virtual network '{vnetName}'."));

                foreach (var (other, otherRange) in placed.Where(p => p.Range.Overlaps(range)))
                    issues.Add(new(ValidationSeverity.Error, "address-space", subnet.Name,
                        $"Subnets '{other.Name}' ({otherRange}) and '{subnet.Name}' ({range}) overlap."));

                placed.Add((subnet, range));
            }
        }
    }
}
