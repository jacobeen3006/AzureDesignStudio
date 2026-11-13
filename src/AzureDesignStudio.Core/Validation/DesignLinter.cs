// Licensed under the MIT License. See LICENSE in the project root for license information.
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
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
    }
}
