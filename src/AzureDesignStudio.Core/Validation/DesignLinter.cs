// Licensed under the MIT License. See LICENSE in the project root for license information.
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Models;
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
    }
}
