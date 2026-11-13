using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Validation;
using Blazor.Diagrams.Core.Models;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    public class DesignLinterTests
    {
        private static NodeModel Create(string key, string name) =>
            DataModelFactory.CreateNodeModelFromKey(key, name, "assets/azure/test.svg");

        private static LintFinding[] Lint(params NodeModel[] nodes) => DesignLinter.Lint(nodes).ToArray();

        [Fact]
        public void Rules_HaveUniqueIds_AndLearnLinks()
        {
            var rules = DesignLinter.Rules;
            Assert.NotEmpty(rules);
            Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
            foreach (var rule in rules)
            {
                Assert.StartsWith("https://learn.microsoft.com/", rule.LearnUrl);
                Assert.NotEqual(ValidationSeverity.Error, rule.Severity);
                var prefix = rule.Pillar switch
                {
                    LintPillar.Reliability => "REL-",
                    LintPillar.Security => "SEC-",
                    LintPillar.OperationalExcellence => "OPS-",
                    _ => null,
                };
                Assert.NotNull(prefix);
                Assert.StartsWith(prefix, rule.Id);
                Assert.Equal(rule.Id.ToUpperInvariant(), rule.Id);
            }
        }

        [Fact]
        public void Lint_EmptyDesign_ReturnsNothing() => Assert.Empty(Lint());

        [Fact]
        public void Lint_OnlyGroups_ReturnsNothing()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet1" };
            vnet.AddSubnet(new SubnetModel { Name = "snet1" });
            Assert.Empty(Lint(vnet, vnet.Subnets[0]));
        }

        [Fact]
        public void Lint_UnknownNode_IsIgnored()
        {
            var unknown = Create("no-such-key", "mystery");
            Assert.IsType<UnknownNodeModel>(unknown);
            Assert.Empty(Lint(unknown));
        }

        [Fact]
        public void Lint_IsOrderedByPillarRuleAndName()
        {
            var findings = Lint(Create(AdsConstants.VirtualMachine, "vm-b"), Create(AdsConstants.VirtualMachine, "vm-a"),
                Create(AdsConstants.VirtualMachine, "vm-c"));
            Assert.Equal(["vm-a", "vm-b", "vm-c"], findings.Select(f => f.NodeName));
            Assert.All(findings, f => Assert.Equal("REL-VM-SINGLE", f.RuleId));
        }
    }
}
