// Licensed under the MIT License. See LICENSE in the project root for license information.
using AzureDesignStudio.Core.AKS;
using AzureDesignStudio.Core.App;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Insights;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.OperationalInsights;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Storage;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Validation
{
    /// <summary>
    /// Offline Well-Architected advice from a fixed rule table. Unlike
    /// <see cref="DesignValidator"/> nothing here blocks a deployment: findings are
    /// Warning or Info only.
    /// </summary>
    public static class DesignLinter
    {
        private const string Learn = "https://learn.microsoft.com/en-us/azure/";

        private static readonly (LintRule Rule, Func<List<NodeModel>, IEnumerable<LintFinding>> Check)[] Table =
        [
            (new LintRule("REL-VM-SINGLE", LintPillar.Reliability, ValidationSeverity.Warning,
                "Single virtual machine",
                Learn + "well-architected/service-guides/virtual-machines"), VmSingle),
            (new LintRule("REL-AVSET-ONE", LintPillar.Reliability, ValidationSeverity.Warning,
                "Availability set with one machine",
                Learn + "virtual-machines/availability-set-overview"), AvSetOne),
            (new LintRule("REL-APPPLAN-TIER", LintPillar.Reliability, ValidationSeverity.Warning,
                "App service plan on a low tier",
                Learn + "well-architected/service-guides/app-service-web-apps"), AppPlanTier),
            (new LintRule("REL-APPGW-INSTANCES", LintPillar.Reliability, ValidationSeverity.Warning,
                "Application gateway with one instance",
                Learn + "well-architected/service-guides/azure-application-gateway"), AppGwInstances),
            (new LintRule("REL-SQL-BASIC", LintPillar.Reliability, ValidationSeverity.Info,
                "SQL database on the Basic tier",
                Learn + "well-architected/service-guides/azure-sql-database"), SqlBasic),
            (new LintRule("REL-STORAGE-LRS", LintPillar.Reliability, ValidationSeverity.Info,
                "Locally redundant storage",
                Learn + "storage/common/storage-redundancy"), StorageLrs),
            (new LintRule("SEC-APPGW-NO-WAF", LintPillar.Security, ValidationSeverity.Warning,
                "Application gateway without a web application firewall",
                Learn + "web-application-firewall/ag/ag-overview"), AppGwNoWaf),
            (new LintRule("SEC-NO-PRIVATE-ENDPOINT", LintPillar.Security, ValidationSeverity.Info,
                "Service reachable only over its public endpoint",
                Learn + "private-link/private-endpoint-overview"), NoPrivateEndpoint),
            (new LintRule("SEC-NO-BASTION", LintPillar.Security, ValidationSeverity.Info,
                "Virtual machines without Azure Bastion",
                Learn + "bastion/bastion-overview"), NoBastion),
            (new LintRule("OPS-NO-MONITORING", LintPillar.OperationalExcellence, ValidationSeverity.Info,
                "No monitoring resource",
                Learn + "well-architected/operational-excellence/observability"), NoMonitoring),
        ];

        public static IReadOnlyList<LintRule> Rules { get; } = Table.Select(t => t.Rule).ToList();

        public static IReadOnlyList<LintFinding> Lint(IEnumerable<NodeModel> nodes)
        {
            var list = nodes.ToList();
            return Table
                .SelectMany(t => t.Check(list))
                .OrderBy(f => f.Pillar)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ThenBy(f => f.NodeName, StringComparer.Ordinal)
                .ThenBy(f => f.Message, StringComparer.Ordinal)
                .ToList();
        }

        private static LintFinding Finding(string ruleId, string nodeName, string message)
        {
            var rule = Table.First(t => t.Rule.Id == ruleId).Rule;
            return new LintFinding(rule.Id, rule.Pillar, rule.Severity, nodeName, message, rule.LearnUrl);
        }

        private static string NameOf(NodeModel node) => (node as IAzureResource)?.Name ?? string.Empty;

        private static IEnumerable<LintFinding> VmSingle(List<NodeModel> nodes)
        {
            foreach (var vm in nodes.OfType<VirtualMachineModel>().Where(v => v.Group is not AvailabilitySetModel))
            {
                var name = NameOf(vm);
                yield return Finding("REL-VM-SINGLE", name,
                    $"Virtual machine '{name}' is a single instance outside an availability set. Use an availability set or a scale set so maintenance or a host failure does not take the workload down.");
            }
        }

        private static IEnumerable<LintFinding> AvSetOne(List<NodeModel> nodes)
        {
            foreach (var set in nodes.OfType<AvailabilitySetModel>().Where(a => a.Children.OfType<VirtualMachineModel>().Count() == 1))
            {
                var name = NameOf(set);
                yield return Finding("REL-AVSET-ONE", name,
                    $"Availability set '{name}' holds one virtual machine. It needs at least two to protect against a host failure.");
            }
        }

        private static IEnumerable<LintFinding> AppPlanTier(List<NodeModel> nodes)
        {
            foreach (var plan in nodes.OfType<AppServicePlanModel>())
            {
                var tier = plan.SkuTier ?? string.Empty;
                var low = tier.Equals("Free", StringComparison.OrdinalIgnoreCase)
                    || tier.Equals("Shared", StringComparison.OrdinalIgnoreCase)
                    || tier.Equals("Basic", StringComparison.OrdinalIgnoreCase);
                // A plan with a consumption or premium function app is re-tiered at export.
                if (!low || !plan.Children.OfType<WebAppModel>().Any() || plan.SkuFollowsFunctionApp)
                    continue;

                var name = NameOf(plan);
                yield return Finding("REL-APPPLAN-TIER", name,
                    $"App service plan '{name}' is on the {tier} tier, which cannot scale out automatically. Use Standard or higher for production workloads.");
            }
        }

        private static IEnumerable<LintFinding> AppGwInstances(List<NodeModel> nodes)
        {
            foreach (var gw in nodes.OfType<AppGatewayModel>().Where(g => !g.Autoscaling && g.InstanceCount < 2))
            {
                var name = NameOf(gw);
                yield return Finding("REL-APPGW-INSTANCES", name,
                    $"Application gateway '{name}' runs a single fixed instance. Use at least two instances or turn on autoscaling.");
            }
        }

        private static IEnumerable<LintFinding> SqlBasic(List<NodeModel> nodes)
        {
            foreach (var db in nodes.OfType<SqlDatabaseModel>().Where(d => string.Equals(d.SkuTier, "Basic", StringComparison.OrdinalIgnoreCase)))
            {
                var name = NameOf(db);
                yield return Finding("REL-SQL-BASIC", name,
                    $"SQL database '{name}' is on the Basic tier, intended for development and small workloads.");
            }
        }

        private static IEnumerable<LintFinding> StorageLrs(List<NodeModel> nodes)
        {
            foreach (var st in nodes.OfType<StorageAccountModel>().Where(s => (s.SkuName ?? string.Empty).EndsWith("_LRS", StringComparison.OrdinalIgnoreCase)))
            {
                var name = NameOf(st);
                yield return Finding("REL-STORAGE-LRS", name,
                    $"Storage account '{name}' uses locally redundant storage: three copies in one datacenter. Consider zone-redundant or geo-redundant storage for data that must survive a datacenter outage.");
            }
        }

        private static IEnumerable<LintFinding> AppGwNoWaf(List<NodeModel> nodes)
        {
            foreach (var gw in nodes.OfType<AppGatewayModel>().Where(g => string.Equals(g.Tier, "Standard_v2", StringComparison.OrdinalIgnoreCase)))
            {
                var name = NameOf(gw);
                yield return Finding("SEC-APPGW-NO-WAF", name,
                    $"Application gateway '{name}' has no web application firewall. Use the WAF v2 tier for internet-facing applications.");
            }
        }

        private static IEnumerable<LintFinding> NoPrivateEndpoint(List<NodeModel> nodes)
        {
            if (!nodes.OfType<VirtualNetworkModel>().Any())
                yield break;

            var endpoints = nodes.OfType<PrivateEndpointModel>().ToList();
            var targetIds = endpoints.Select(e => e.EffectiveTargetId).Where(id => id.Length > 0).ToList();

            foreach (var node in nodes)
            {
                // Unknown stencils have no ARM type (it throws), so they are skipped first.
                if (node is UnknownNodeModel)
                    continue;
                var type = node switch
                {
                    AzureNodeBase n => n.ArmResourceType,
                    AzureGroupBase g => g.ArmResourceType,
                    _ => null,
                };
                if (type is null || !PrivateEndpointModel.SupportsPrivateLink(type))
                    continue;

                var resource = (IAzureResource)node;
                var covered = endpoints.Any(e => ReferenceEquals(e.LinkedTarget, node))
                    || targetIds.Any(id => IdTargets(id, resource, type));
                if (covered)
                    continue;

                var name = NameOf(node);
                yield return Finding("SEC-NO-PRIVATE-ENDPOINT", name,
                    $"{((IAzureNode)node).ServiceName} '{name}' is reachable only over its public endpoint. Add a private endpoint to keep traffic on the virtual network.");
            }
        }

        /// <summary>A typed target id is either the node's own template expression (spacing aside) or
        /// a full ARM id that ends in <c>/providers/{type}/{name}</c>.</summary>
        private static bool IdTargets(string id, IAzureResource resource, string armType)
        {
            if (resource.ResourceId.Length > 0 && StripWhitespace(id).Equals(StripWhitespace(resource.ResourceId), StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrEmpty(resource.Name))
                return false;
            return id.TrimEnd('/').EndsWith($"/providers/{armType}/{resource.Name}", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripWhitespace(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

        private static IEnumerable<LintFinding> NoBastion(List<NodeModel> nodes)
        {
            if (nodes.Any(n => n is VirtualMachineModel or VirtualMachineScaleSetModel) && !nodes.OfType<BastionsModel>().Any())
                yield return Finding("SEC-NO-BASTION", "design",
                    "The design has virtual machines but no Azure Bastion. Plan how administrators reach them without public IP addresses.");
        }

        private static IEnumerable<LintFinding> NoMonitoring(List<NodeModel> nodes)
        {
            var hasWorkload = nodes.Any(n => n is VirtualMachineModel or VirtualMachineScaleSetModel or WebAppModel
                or ContainerAppModel or AksClusterModel);
            var hasMonitoring = nodes.Any(n => n is LogAnalyticsWorkspaceModel or ApplicationInsightsModel);
            if (hasWorkload && !hasMonitoring)
                yield return Finding("OPS-NO-MONITORING", "design",
                    "The design has no Log Analytics workspace or Application Insights. Add one so the workload can be observed.");
        }
    }
}
