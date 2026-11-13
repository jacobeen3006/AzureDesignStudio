using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Storage;
using AzureDesignStudio.Core.Web;
using AzureDesignStudio.Core.Validation;
using AzureDesignStudio.Core.Insights;
using AzureDesignStudio.Core.OperationalInsights;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
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
            Assert.Equal(
                ["REL-VM-SINGLE:vm-a", "REL-VM-SINGLE:vm-b", "REL-VM-SINGLE:vm-c", "SEC-NO-BASTION:design", "OPS-NO-MONITORING:design"],
                findings.Select(f => f.RuleId + ":" + f.NodeName));
        }

        [Fact]
        public void Lint_RulesInTheSamePillar_SortByRuleId()
        {
            var findings = Lint(Create(AdsConstants.VirtualMachine, "vm1"), Create(AdsConstants.StorageAccount, "st1"));
            Assert.Equal(
                ["REL-STORAGE-LRS:st1", "REL-VM-SINGLE:vm1", "SEC-NO-BASTION:design", "OPS-NO-MONITORING:design"],
                findings.Select(f => f.RuleId + ":" + f.NodeName));
        }

        private static LintFinding[] ForRule(LintFinding[] findings, string id) =>
            findings.Where(f => f.RuleId == id).ToArray();

        [Fact]
        public void REL_VM_SINGLE_Fires_ForAVmOutsideAnAvailabilitySet()
        {
            var f = Assert.Single(ForRule(Lint(Create(AdsConstants.VirtualMachine, "vm1")), "REL-VM-SINGLE"));
            Assert.Equal(LintPillar.Reliability, f.Pillar);
            Assert.Equal(ValidationSeverity.Warning, f.Severity);
            Assert.Equal("vm1", f.NodeName);
            Assert.Equal("Virtual machine 'vm1' is a single instance outside an availability set. Use an availability set or a scale set so maintenance or a host failure does not take the workload down.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/well-architected/service-guides/virtual-machines", f.LearnUrl);
        }

        [Fact]
        public void REL_VM_SINGLE_DoesNotFire_ForVmsInAnAvailabilitySet()
        {
            var set = (AvailabilitySetModel)Create(AdsConstants.AvailabilitySet, "avset");
            var a = Create(AdsConstants.VirtualMachine, "vm1");
            var b = Create(AdsConstants.VirtualMachine, "vm2");
            set.AddChild(a);
            set.AddChild(b);
            Assert.Empty(ForRule(Lint(set, a, b), "REL-VM-SINGLE"));
        }

        [Fact]
        public void REL_AVSET_ONE_Fires_ForOneMachine()
        {
            var set = (AvailabilitySetModel)Create(AdsConstants.AvailabilitySet, "avset");
            var a = Create(AdsConstants.VirtualMachine, "vm1");
            set.AddChild(a);
            var f = Assert.Single(ForRule(Lint(set, a), "REL-AVSET-ONE"));
            Assert.Equal(ValidationSeverity.Warning, f.Severity);
            Assert.Equal("avset", f.NodeName);
            Assert.Equal("Availability set 'avset' holds one virtual machine. It needs at least two to protect against a host failure.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/virtual-machines/availability-set-overview", f.LearnUrl);
        }

        [Fact]
        public void REL_AVSET_ONE_DoesNotFire_ForTwoOrZeroMachines()
        {
            var two = (AvailabilitySetModel)Create(AdsConstants.AvailabilitySet, "avset");
            two.AddChild(Create(AdsConstants.VirtualMachine, "vm1"));
            two.AddChild(Create(AdsConstants.VirtualMachine, "vm2"));
            var empty = Create(AdsConstants.AvailabilitySet, "empty");
            Assert.Empty(ForRule(Lint(two, empty), "REL-AVSET-ONE"));
        }

        private static AppServicePlanModel PlanWithApp(string tier, string appKey = AdsConstants.WebApp)
        {
            var plan = (AppServicePlanModel)Create(AdsConstants.AppServicePlan, "plan1");
            plan.SkuTier = tier;
            plan.AddChild(Create(appKey, "app1"));
            return plan;
        }

        [Theory]
        [InlineData("Free")]
        [InlineData("shared")]
        [InlineData("Basic")]
        public void REL_APPPLAN_TIER_Fires_ForLowTiers(string tier)
        {
            var plan = PlanWithApp(tier);
            var f = Assert.Single(ForRule(Lint(plan), "REL-APPPLAN-TIER"));
            Assert.Equal(ValidationSeverity.Warning, f.Severity);
            Assert.Equal("plan1", f.NodeName);
            Assert.Equal($"App service plan 'plan1' is on the {tier} tier, which cannot scale out automatically. Use Standard or higher for production workloads.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/well-architected/service-guides/app-service-web-apps", f.LearnUrl);
        }

        [Fact]
        public void REL_APPPLAN_TIER_DoesNotFire_ForStandardOrAnEmptyPlan()
        {
            Assert.Empty(ForRule(Lint(PlanWithApp("Standard")), "REL-APPPLAN-TIER"));
            var empty = (AppServicePlanModel)Create(AdsConstants.AppServicePlan, "plan2");
            empty.SkuTier = "Basic";
            Assert.Empty(ForRule(Lint(empty), "REL-APPPLAN-TIER"));
        }

        [Theory]
        [InlineData("consumption")]
        [InlineData("premium")]
        public void REL_APPPLAN_TIER_SkipsFunctionReTieredPlans(string hosting)
        {
            var plan = PlanWithApp("Basic", AdsConstants.FunctionApp);
            ((FunctionAppModel)plan.Children.Single()).HostingPlan = hosting;
            Assert.Empty(ForRule(Lint(plan), "REL-APPPLAN-TIER"));
        }

        [Fact]
        public void REL_APPPLAN_TIER_Fires_ForADedicatedFunctionPlanOnBasic()
        {
            var plan = PlanWithApp("Basic", AdsConstants.FunctionApp);
            ((FunctionAppModel)plan.Children.Single()).HostingPlan = "dedicated";
            Assert.Single(ForRule(Lint(plan), "REL-APPPLAN-TIER"));
        }

        [Fact]
        public void REL_APPGW_INSTANCES_Fires_ForOneFixedInstance()
        {
            var gw = (AppGatewayModel)Create(AdsConstants.AppGateway, "gw1");
            gw.Autoscaling = false;
            gw.InstanceCount = 1;
            var f = Assert.Single(ForRule(Lint(gw), "REL-APPGW-INSTANCES"));
            Assert.Equal(ValidationSeverity.Warning, f.Severity);
            Assert.Equal("gw1", f.NodeName);
            Assert.Equal("Application gateway 'gw1' runs a single fixed instance. Use at least two instances or turn on autoscaling.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-application-gateway", f.LearnUrl);
        }

        [Fact]
        public void REL_APPGW_INSTANCES_DoesNotFire_WithAutoscalingOrTwoInstances()
        {
            var auto = (AppGatewayModel)Create(AdsConstants.AppGateway, "gw1");
            auto.Autoscaling = true;
            auto.InstanceCount = 1;
            var two = (AppGatewayModel)Create(AdsConstants.AppGateway, "gw2");
            two.InstanceCount = 2;
            Assert.Empty(ForRule(Lint(auto, two), "REL-APPGW-INSTANCES"));
        }

        [Fact]
        public void REL_SQL_BASIC_Fires_ForBasic()
        {
            var db = (SqlDatabaseModel)Create(AdsConstants.SqlDatabase, "db1");
            db.SkuTier = "basic";
            var f = Assert.Single(ForRule(Lint(db), "REL-SQL-BASIC"));
            Assert.Equal(ValidationSeverity.Info, f.Severity);
            Assert.Equal("db1", f.NodeName);
            Assert.Equal("SQL database 'db1' is on the Basic tier, intended for development and small workloads.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-sql-database", f.LearnUrl);
        }

        [Fact]
        public void REL_SQL_BASIC_DoesNotFire_ForGeneralPurpose()
        {
            var db = (SqlDatabaseModel)Create(AdsConstants.SqlDatabase, "db1");
            Assert.Empty(ForRule(Lint(db), "REL-SQL-BASIC"));
        }

        [Fact]
        public void REL_STORAGE_LRS_Fires_ForLrs()
        {
            var st = (StorageAccountModel)Create(AdsConstants.StorageAccount, "st1");
            st.SkuName = "Standard_LRS";
            var f = Assert.Single(ForRule(Lint(st), "REL-STORAGE-LRS"));
            Assert.Equal(ValidationSeverity.Info, f.Severity);
            Assert.Equal("st1", f.NodeName);
            Assert.Equal("Storage account 'st1' uses locally redundant storage: three copies in one datacenter. Consider zone-redundant or geo-redundant storage for data that must survive a datacenter outage.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/storage/common/storage-redundancy", f.LearnUrl);
        }

        [Fact]
        public void REL_STORAGE_LRS_DoesNotFire_ForZrs_OrAFunctionsEmbeddedAccount()
        {
            var st = (StorageAccountModel)Create(AdsConstants.StorageAccount, "st1");
            st.SkuName = "Standard_ZRS";
            var fn = Create(AdsConstants.FunctionApp, "fn1");
            Assert.Empty(ForRule(Lint(st, fn), "REL-STORAGE-LRS"));
        }

        private static void Link(NodeModel a, NodeModel b) =>
            new BlazorDiagram().Links.Add(new LinkModel(new SinglePortAnchor(a.Ports[1]), new SinglePortAnchor(b.Ports[0])));

        [Fact]
        public void SEC_APPGW_NO_WAF_Fires_ForStandardV2()
        {
            var gw = (AppGatewayModel)Create(AdsConstants.AppGateway, "gw1");
            gw.Tier = "Standard_v2";
            var f = Assert.Single(ForRule(Lint(gw), "SEC-APPGW-NO-WAF"));
            Assert.Equal(LintPillar.Security, f.Pillar);
            Assert.Equal(ValidationSeverity.Warning, f.Severity);
            Assert.Equal("gw1", f.NodeName);
            Assert.Equal("Application gateway 'gw1' has no web application firewall. Use the WAF v2 tier for internet-facing applications.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/web-application-firewall/ag/ag-overview", f.LearnUrl);
        }

        [Fact]
        public void SEC_APPGW_NO_WAF_DoesNotFire_ForWafV2()
        {
            var gw = (AppGatewayModel)Create(AdsConstants.AppGateway, "gw1");
            gw.Tier = "WAF_v2";
            Assert.Empty(ForRule(Lint(gw), "SEC-APPGW-NO-WAF"));
        }

        private static NodeModel Vnet() => Create(AdsConstants.VirtualNetwork, "vnet1");

        [Fact]
        public void SEC_NO_PRIVATE_ENDPOINT_Fires_ForAStorageAccountInAVnetDesign()
        {
            var f = Assert.Single(ForRule(Lint(Vnet(), Create(AdsConstants.StorageAccount, "st1")), "SEC-NO-PRIVATE-ENDPOINT"));
            Assert.Equal(ValidationSeverity.Info, f.Severity);
            Assert.Equal("st1", f.NodeName);
            Assert.Equal("Storage Account 'st1' is reachable only over its public endpoint. Add a private endpoint to keep traffic on the virtual network.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-overview", f.LearnUrl);
        }

        [Fact]
        public void SEC_NO_PRIVATE_ENDPOINT_Fires_ForAWebApp_ButNotForASqlDatabase()
        {
            var findings = ForRule(Lint(Vnet(), Create(AdsConstants.WebApp, "app1"), Create(AdsConstants.SqlDatabase, "db1")), "SEC-NO-PRIVATE-ENDPOINT");
            Assert.Equal("app1", Assert.Single(findings).NodeName);
        }

        [Fact]
        public void SEC_NO_PRIVATE_ENDPOINT_Fires_ForASqlServer()
        {
            var findings = ForRule(Lint(Vnet(), Create(AdsConstants.SqlServer, "sql1")), "SEC-NO-PRIVATE-ENDPOINT");
            var f = Assert.Single(findings);
            Assert.Equal("Azure SQL Server 'sql1' is reachable only over its public endpoint. Add a private endpoint to keep traffic on the virtual network.", f.Message);
        }

        [Fact]
        public void SEC_NO_PRIVATE_ENDPOINT_NoVirtualNetwork_DoesNotFire() =>
            Assert.Empty(ForRule(Lint(Create(AdsConstants.StorageAccount, "st1")), "SEC-NO-PRIVATE-ENDPOINT"));

        [Fact]
        public void SEC_NO_PRIVATE_ENDPOINT_EndpointTypedById_CountsAsCovered()
        {
            var st = (StorageAccountModel)Create(AdsConstants.StorageAccount, "st1");
            var pe = (PrivateEndpointModel)Create(AdsConstants.PrivateEndpoint, "pe1");
            pe.TargetResourceId = st.ResourceId.ToUpperInvariant();
            Assert.Empty(ForRule(Lint(Vnet(), st, pe), "SEC-NO-PRIVATE-ENDPOINT"));
        }

        [Fact]
        public void SEC_NO_PRIVATE_ENDPOINT_LinkedEndpoint_CountsAsCovered()
        {
            var st = Create(AdsConstants.StorageAccount, "st1");
            var other = Create(AdsConstants.StorageAccount, "st2");
            var pe = Create(AdsConstants.PrivateEndpoint, "pe1");
            Link(pe, st);
            var findings = ForRule(Lint(Vnet(), st, other, pe), "SEC-NO-PRIVATE-ENDPOINT");
            Assert.Equal("st2", Assert.Single(findings).NodeName);
        }

        [Fact]
        public void SupportsPrivateLink_KnowsTheSupportedTypes()
        {
            Assert.True(PrivateEndpointModel.SupportsPrivateLink("microsoft.storage/storageaccounts"));
            Assert.False(PrivateEndpointModel.SupportsPrivateLink("Microsoft.Sql/servers/databases"));
            Assert.False(PrivateEndpointModel.SupportsPrivateLink(""));
        }

        [Fact]
        public void SEC_NO_BASTION_FiresOncePerDesign()
        {
            var findings = ForRule(Lint(Create(AdsConstants.VirtualMachine, "vm1"), Create(AdsConstants.VirtualMachine, "vm2"),
                Create(AdsConstants.VirtualMachine, "vm3")), "SEC-NO-BASTION");
            var f = Assert.Single(findings);
            Assert.Equal(ValidationSeverity.Info, f.Severity);
            Assert.Equal("design", f.NodeName);
            Assert.Equal("The design has virtual machines but no Azure Bastion. Plan how administrators reach them without public IP addresses.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/bastion/bastion-overview", f.LearnUrl);
        }

        [Fact]
        public void SEC_NO_BASTION_Fires_ForAScaleSet_AndNotWithABastionOrNoMachines()
        {
            Assert.Single(ForRule(Lint(Create(AdsConstants.VirtualMachineScaleSet, "vmss1")), "SEC-NO-BASTION"));
            Assert.Empty(ForRule(Lint(Create(AdsConstants.VirtualMachine, "vm1"), Create(AdsConstants.Bastions, "bas1")), "SEC-NO-BASTION"));
            Assert.Empty(ForRule(Lint(Create(AdsConstants.StorageAccount, "st1")), "SEC-NO-BASTION"));
        }

        [Theory]
        [InlineData(AdsConstants.VirtualMachine)]
        [InlineData(AdsConstants.VirtualMachineScaleSet)]
        [InlineData(AdsConstants.WebApp)]
        [InlineData(AdsConstants.FunctionApp)]
        [InlineData(AdsConstants.ContainerApp)]
        [InlineData(AdsConstants.AKSCluster)]
        public void OPS_NO_MONITORING_Fires_ForComputeWithoutMonitoring(string key)
        {
            var f = Assert.Single(ForRule(Lint(Create(key, "w1")), "OPS-NO-MONITORING"));
            Assert.Equal(LintPillar.OperationalExcellence, f.Pillar);
            Assert.Equal(ValidationSeverity.Info, f.Severity);
            Assert.Equal("design", f.NodeName);
            Assert.Equal("The design has no Log Analytics workspace or Application Insights. Add one so the workload can be observed.", f.Message);
            Assert.Equal("https://learn.microsoft.com/en-us/azure/well-architected/operational-excellence/observability", f.LearnUrl);
        }

        [Theory]
        [InlineData(AdsConstants.LogAnalyticsWorkspace)]
        [InlineData(AdsConstants.ApplicationInsights)]
        public void OPS_NO_MONITORING_WorkspacePresent_DoesNotFire(string monitoringKey) =>
            Assert.Empty(ForRule(Lint(Create(AdsConstants.WebApp, "app1"), Create(monitoringKey, "mon1")), "OPS-NO-MONITORING"));

        [Fact]
        public void OPS_NO_MONITORING_NoComputeNodes_DoesNotFire() =>
            Assert.Empty(ForRule(Lint(Create(AdsConstants.StorageAccount, "st1")), "OPS-NO-MONITORING"));

        [Fact]
        public void ValidationReport_For_RunsBoth()
        {
            var report = ValidationReport.For(new[] { Create(AdsConstants.PublicIp, "pip1"), Create(AdsConstants.VirtualMachine, "vm1") });
            Assert.Equal(DesignValidator.Validate(new[] { Create(AdsConstants.PublicIp, "pip1"), Create(AdsConstants.VirtualMachine, "vm1") }), report.Issues);
            Assert.NotEmpty(report.Findings);
            Assert.Contains(report.Findings, f => f.NodeName == "vm1");
        }

        [Fact]
        public void ValidationReport_RoundTripsThroughJson()
        {
            var report = new ValidationReport(
                new[] { new ValidationIssue(ValidationSeverity.Error, "R1", "n1", "bad") },
                new[] { new LintFinding("REL-X", LintPillar.OperationalExcellence, ValidationSeverity.Info, "n2", "advice", "https://learn.microsoft.com/x") });
            var json = System.Text.Json.JsonSerializer.Serialize(report, ValidationJson.Options);
            var back = ValidationReport.Parse(json);
            Assert.Equal(report.Issues, back.Issues);
            Assert.Equal(report.Findings, back.Findings);
            Assert.Contains("OperationalExcellence", json);
        }

        [Fact]
        public void ValidationReport_ParsesLegacyIssueArray()
        {
            var issues = new[] { new ValidationIssue(ValidationSeverity.Warning, "R1", "n1", "careful") };
            var json = System.Text.Json.JsonSerializer.Serialize(issues, ValidationJson.Options);
            var report = ValidationReport.Parse(json);
            Assert.Equal(issues, report.Issues);
            Assert.Empty(report.Findings);
            Assert.Empty(ValidationReport.Parse("").Issues);
            Assert.Empty(ValidationReport.Parse(null).Findings);
        }
    }
}
