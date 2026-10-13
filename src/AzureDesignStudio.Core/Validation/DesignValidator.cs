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
            [typeof(SqlServerModel)] = (new Regex("^(?!sql)[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$", RegexOptions.IgnoreCase), "SQL server name must be 2-63 alphanumerics/hyphens, not start with 'sql'."),
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

            // Container Apps environment requires a delegated subnet somewhere in the design.
            if (list.OfType<ContainerAppsEnvironmentModel>().Any()
                && !list.OfType<SubnetModel>().Any(s => s.Delegation == "Microsoft.App/environments"))
                issues.Add(new(ValidationSeverity.Warning, "delegation", "container-apps",
                    "Container Apps environment requires a subnet delegated to Microsoft.App/environments."));

            return issues;
        }
    }
}
