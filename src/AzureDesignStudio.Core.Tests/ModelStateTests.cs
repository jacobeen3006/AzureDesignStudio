using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutoMapper;
using AzureDesignStudio.AzureResources.ApiManagement;
using AzureDesignStudio.AzureResources.Web;
using AzureDesignStudio.Core.AKS;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.CognitiveServices;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Validation;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>Export state that used to go stale, values the serializer used to drop, and
    /// model data that used to be shared or silently re-parented.</summary>
    public class ModelStateTests
    {
        private static readonly IMapper Mapper =
            new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper();

        private static JsonElement Export(AzureNodeBase node)
        {
            var template = new ArmTemplate();
            template.AddResource(node.GetArmResources());
            using var doc = JsonDocument.Parse(template.GenerateArmTemplate());
            return doc.RootElement.GetProperty("resources").EnumerateArray()
                .First(r => r.GetProperty("name").GetString()!.EndsWith(node.Name, StringComparison.Ordinal)).Clone();
        }

        // --- explicit false / 0 ---

        [Fact]
        public void WebAppWritesClientAffinityDisabled()
        {
            // Azure's default is true, so an omitted false silently turned affinity on.
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux" };
            var web = new WebAppModel { Name = "web", RuntimeStack = "node|~14" };
            plan.AddChild(web);

            Assert.False(Export(web).GetProperty("properties").GetProperty("clientAffinityEnabled").GetBoolean());
        }

        [Fact]
        public void ConsumptionApimWritesCapacityZero()
        {
            var apim = new APIMModel { Name = "apim1", SkuName = "Consumption", SkuCapacity = 0, PublisherEmail = "ops@contoso.com", PublisherName = "Contoso" };

            Assert.Equal(0, Export(apim).GetProperty("sku").GetProperty("capacity").GetInt32());
        }

        // --- state that must not survive a regroup or a rename ---

        [Fact]
        public void ApimMovedOutOfItsSubnetDropsTheNetworkConfiguration()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "apim" });
            var apim = new APIMModel { Name = "apim1", SkuName = "Developer", SkuCapacity = 1, PublisherEmail = "ops@contoso.com", PublisherName = "Contoso", VnetType = "External" };
            subnet.AddChild(apim);
            Assert.True(Export(apim).GetProperty("properties").TryGetProperty("virtualNetworkConfiguration", out _));

            subnet.RemoveChild(apim);
            var exported = Export(apim);

            Assert.False(exported.GetProperty("properties").TryGetProperty("virtualNetworkConfiguration", out _));
            Assert.False(exported.TryGetProperty("dependsOn", out _));
        }

        [Fact]
        public void VmTakenOutOfItsAvailabilitySetDropsTheReference()
        {
            var subnet = TestPlacement.NewSubnet();
            var set = new AvailabilitySetModel { Name = "avset" };
            subnet.AddChild(set);
            var vm = new VirtualMachineModel { Name = "vm1" };
            set.AddChild(vm); // takes its network from the subnet around the set
            Assert.Contains("availabilitySet", Export(vm).GetRawText());

            set.RemoveChild(vm);
            subnet.AddChild(vm);

            Assert.DoesNotContain("availabilitySet", Export(vm).GetRawText());
        }

        [Fact]
        public void OpenAiSubdomainDefaultFollowsARenameAndIsNotStoredAsTyped()
        {
            var account = new AzureOpenAIModel { Name = "first" };
            Assert.Equal("first", Export(account).GetProperty("properties").GetProperty("customSubDomainName").GetString());
            Assert.Equal(string.Empty, account.CustomSubDomainName); // nothing was typed

            account.Name = "second";
            Assert.Equal("second", Export(account).GetProperty("properties").GetProperty("customSubDomainName").GetString());

            account.CustomSubDomainName = "chosen";
            Assert.Equal("chosen", Export(account).GetProperty("properties").GetProperty("customSubDomainName").GetString());
        }

        [Fact]
        public void AksDnsPrefixDefaultFollowsARename()
        {
            var cluster = (AksClusterModel)DataModelFactory.CreateNodeModelFromKey(Common.AdsConstants.AKSCluster, "first-aks", "");
            var first = Export(cluster).GetProperty("properties").GetProperty("dnsPrefix").GetString();

            cluster.Name = "second-aks";
            var second = Export(cluster).GetProperty("properties").GetProperty("dnsPrefix").GetString();

            Assert.StartsWith("first", first);
            Assert.StartsWith("second", second);
            Assert.Equal(string.Empty, cluster.DnsPrefix);
        }

        // --- copies do not share state ---

        [Fact]
        public void PastedVirtualNetworkHasItsOwnAddressSpaces()
        {
            var original = new VirtualNetworkModel { Name = "vnet" };
            var dto = original.GetNodeDto(Mapper);                       // copy
            var first = (VirtualNetworkModel)DataModelFactory.GetNodeModelFromDto(dto, Mapper)!;   // paste
            var second = (VirtualNetworkModel)DataModelFactory.GetNodeModelFromDto(dto, Mapper)!;  // paste again

            first.IpSpace[0].AddressPrefix = "172.16.0.0/16";

            Assert.Equal("10.0.0.0/16", original.IpSpace[0].AddressPrefix);
            Assert.Equal("10.0.0.0/16", second.IpSpace[0].AddressPrefix);
        }

        // --- validation ---

        [Fact]
        public void WebAppOnAConsumptionFunctionPlanIsAnError()
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux" };
            var function = new FunctionAppModel { Name = "func1", RuntimeStack = "node|~14", HostingPlan = "consumption" };
            var web = new WebAppModel { Name = "web1", RuntimeStack = "node|~14" };
            plan.AddChild(function);
            plan.AddChild(web);

            var issue = Assert.Single(DesignValidator.Validate(new NodeModel[] { plan, function, web }), i => i.RuleId == "placement");

            Assert.Contains("web1", issue.Message);
            Assert.Contains("Consumption", issue.Message);
        }

        [Fact]
        public void FunctionAppOnADedicatedPlanMaySharePlanWithAWebApp()
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux" };
            var function = new FunctionAppModel { Name = "func1", RuntimeStack = "node|~14", HostingPlan = "asp" };
            var web = new WebAppModel { Name = "web1", RuntimeStack = "node|~14" };
            plan.AddChild(function);
            plan.AddChild(web);

            Assert.DoesNotContain(DesignValidator.Validate(new NodeModel[] { plan, function, web }), i => i.RuleId == "placement");
        }

        // --- App Service plan size ---

        private static (string Name, string Tier) PlanSku(AppServicePlanModel plan, AzureNodeBase exportedChild)
        {
            exportedChild.GetArmResources(); // the app decides what its plan must be
            return (plan.SkuName, plan.SkuTier);
        }

        [Fact]
        public void DedicatedFunctionAppKeepsThePlanSizeTheUserChose()
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux" };
            Assert.True(plan.UseDedicatedSku("s2"));
            var function = new FunctionAppModel { Name = "func1", RuntimeStack = "node|~14", HostingPlan = "asp" };
            plan.AddChild(function);

            Assert.Equal(("S2", "Standard"), PlanSku(plan, function)); // was forced to P1V2 on every export
        }

        [Fact]
        public void FunctionAppSwitchedFromConsumptionToDedicatedGetsADedicatedPlan()
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux" };
            var function = new FunctionAppModel { Name = "func1", RuntimeStack = "node|~14", HostingPlan = "consumption" };
            plan.AddChild(function);
            Assert.Equal(("Y1", "Dynamic"), PlanSku(plan, function));
            Assert.True(plan.SkuFollowsFunctionApp);

            function.HostingPlan = "asp";

            Assert.Equal(("P1v2", "PremiumV2"), PlanSku(plan, function));
            Assert.False(plan.SkuFollowsFunctionApp);
        }

        [Fact]
        public void PremiumFunctionAppKeepsALargerElasticSize()
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux", SkuName = "EP3", SkuTier = "ElasticPremium" };
            var function = new FunctionAppModel { Name = "func1", RuntimeStack = "node|~14", HostingPlan = "premium" };
            plan.AddChild(function);

            Assert.Equal(("EP3", "ElasticPremium"), PlanSku(plan, function));
        }

        [Fact]
        public void WebAppLeftOnAPlanAFunctionAppMadeServerlessGetsADedicatedPlan()
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux", SkuName = "Y1", SkuTier = "Dynamic" };
            var web = new WebAppModel { Name = "web1", RuntimeStack = "node|~14" };
            plan.AddChild(web); // the Function App that set Y1 is gone

            Assert.Equal(("P1v2", "PremiumV2"), PlanSku(plan, web));
        }

        [Fact]
        public void UnknownPlanSizeIsRefused()
        {
            var plan = new AppServicePlanModel { Name = "plan" };

            Assert.False(plan.UseDedicatedSku("F1")); // Free cannot run the always-on sites this exports
            Assert.Equal("P1v2", plan.SkuName);
        }

        // --- load ---

        [Fact]
        public async Task ItemsWhoseParentGroupIsMissingAreLoadedAndReported()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "workloads" });
            var firewall = new AzureFirewallModel { Name = "fw" };
            subnet.AddChild(firewall);
            var source = new BlazorDiagram();
            source.Groups.Add(vnet);
            source.Groups.Add(subnet);
            source.Nodes.Add(firewall);
            var graph = await DataModelFactory.SaveDiagramToDto(source, Mapper);

            // The subnet's own entry is gone from the file; the firewall still names it.
            graph.Groups.Remove(graph.Groups.OfType<AzureNodeDto>().Single(g => g.Name == "workloads"));
            var detached = new List<string>();
            var target = new BlazorDiagram();
            target.Options.Links.Factory = DataModelFactory.LinkModelFactory;

            DataModelFactory.LoadDiagramFromDto(target, graph, Mapper, detached: detached);

            Assert.Equal(new[] { "fw" }, detached);
            Assert.Null(Assert.Single(target.Nodes.OfType<AzureFirewallModel>()).Group); // loaded, at the root
        }

        [Fact]
        public async Task IntactDesignReportsNothingDetached()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "workloads" });
            var source = new BlazorDiagram();
            source.Groups.Add(vnet);
            source.Groups.Add(subnet);
            var detached = new List<string>();
            var target = new BlazorDiagram();

            DataModelFactory.LoadDiagramFromDto(target, await DataModelFactory.SaveDiagramToDto(source, Mapper), Mapper, detached: detached);

            Assert.Empty(detached);
        }
    }
}
