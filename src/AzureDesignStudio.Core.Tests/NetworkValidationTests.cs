using System.Linq;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Storage;
using AzureDesignStudio.Core.Validation;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>Network rules that used to disagree with each other or not exist at all.</summary>
    public class NetworkValidationTests
    {
        private static VirtualNetworkModel Vnet(string space, params (string Name, string Prefix)[] subnets)
        {
            var vnet = new VirtualNetworkModel { Name = "vnet", IpSpace = [new AddressSpace { AddressPrefix = space }] };
            foreach (var (name, prefix) in subnets)
                vnet.AddSubnet(new SubnetModel { Name = name, AddressPrefix = prefix });
            return vnet;
        }

        private static string[] AddressIssues(VirtualNetworkModel vnet) =>
            DesignValidator.Validate(new NodeModel[] { vnet }.Concat(vnet.Subnets))
                .Where(i => i.RuleId == "address-space").Select(i => i.Message).ToArray();

        // --- dedicated subnet names: one rule for lookup, placement, validity and export ---

        [Theory]
        [InlineData("AzureFirewallSubnet")]
        [InlineData("azurefirewallsubnet")]
        [InlineData(" AZUREFIREWALLSUBNET ")]
        public void FirewallAcceptsItsSubnetWhateverTheCasing(string subnetName)
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = subnetName });

            Assert.True(new AzureFirewallModel().IsDrappable(subnet).result);
        }

        [Theory]
        [InlineData("azurefirewallsubnet", "AzureFirewallSubnet")]
        [InlineData("AZUREBASTIONSUBNET", "AzureBastionSubnet")]
        [InlineData("gatewaysubnet", "GatewaySubnet")]
        [InlineData("workloads", "workloads")]
        public void ReservedSubnetNameIsExportedInItsDocumentedSpelling(string typed, string exported)
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = typed });

            Assert.Equal($"vnet/{exported}", Assert.Single(subnet.GetArmResources()).Name);
            Assert.Contains($"'{exported}'", subnet.ResourceId);
        }

        [Fact]
        public void BastionRejectsAnOrdinarySubnet()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = vnet.AddSubnet(new SubnetModel { Name = "workloads" });

            Assert.False(new BastionsModel().IsDrappable(subnet).result);
            Assert.True(new BastionsModel().IsDrappable(vnet.AddSubnet(new SubnetModel { Name = "azurebastionsubnet" })).result);
        }

        // --- new subnets ---

        [Fact]
        public void EachNewSubnetGetsItsOwnNameAndRange()
        {
            var vnet = new VirtualNetworkModel { Name = "vnet" };

            var first = vnet.AddSubnet(vnet.NewSubnet());
            var second = vnet.AddSubnet(vnet.NewSubnet());
            var third = vnet.AddSubnet(vnet.NewSubnet());

            Assert.Equal(new[] { "Subnet1", "Subnet2", "Subnet3" }, new[] { first.Name, second.Name, third.Name });
            Assert.Equal(new[] { "10.0.0.0/24", "10.0.1.0/24", "10.0.2.0/24" }, new[] { first.AddressPrefix, second.AddressPrefix, third.AddressPrefix });
            Assert.Empty(AddressIssues(vnet));
        }

        [Fact]
        public void NewSubnetReusesAFreedNameAndRange()
        {
            var vnet = Vnet("10.0.0.0/16", ("Subnet1", "10.0.0.0/24"), ("Subnet3", "10.0.2.0/24"));

            var added = vnet.NewSubnet();

            Assert.Equal("Subnet2", added.Name);
            Assert.Equal("10.0.1.0/24", added.AddressPrefix);
        }

        // --- address-range validation ---

        [Fact]
        public void WellFormedNetworkHasNoAddressIssues()
        {
            Assert.Empty(AddressIssues(Vnet("10.0.0.0/16", ("a", "10.0.0.0/24"), ("b", "10.0.1.0/26"), ("c", "10.0.1.64/26"), ("v6", "fd00:0:0:1::/64"))));
        }

        [Theory]
        [InlineData("not-a-cidr")]
        [InlineData("10.0.0.0")]
        [InlineData("10.0.0.0/33")]
        [InlineData("")]
        public void MalformedSubnetPrefixIsAnError(string prefix)
        {
            Assert.Contains("is not a valid CIDR block", Assert.Single(AddressIssues(Vnet("10.0.0.0/16", ("bad", prefix)))));
        }

        [Fact]
        public void PrefixWithHostBitsIsAnErrorNamingTheNetworkAddress()
        {
            var issue = Assert.Single(AddressIssues(Vnet("10.0.0.0/16", ("a", "10.0.1.5/24"))));

            Assert.Contains("use 10.0.1.0/24", issue);
        }

        [Fact]
        public void SubnetOutsideTheVirtualNetworkIsAnError()
        {
            var issue = Assert.Single(AddressIssues(Vnet("10.0.0.0/16", ("far", "10.1.0.0/24"))));

            Assert.Contains("outside the address space", issue);
        }

        [Theory]
        [InlineData("10.0.0.0/24", "10.0.0.0/24")]    // identical: what two default subnets used to be
        [InlineData("10.0.0.0/24", "10.0.0.128/25")]  // one inside the other
        [InlineData("10.0.0.0/23", "10.0.1.0/24")]
        public void OverlappingSubnetsAreAnError(string first, string second)
        {
            var issue = Assert.Single(AddressIssues(Vnet("10.0.0.0/16", ("one", first), ("two", second))));

            Assert.Contains("overlap", issue);
        }

        [Fact]
        public void InvalidVirtualNetworkAddressSpaceIsAnError()
        {
            Assert.Contains(AddressIssues(Vnet("10.0.0.0/40")), m => m.Contains("is not a valid CIDR block"));
            Assert.Contains(AddressIssues(Vnet("10.0.5.9/16")), m => m.Contains("use 10.0.0.0/16"));
        }

        // --- names ---

        [Fact]
        public void TwoResourcesOfOneKindWithTheSameNameAreAnError()
        {
            var nodes = new NodeModel[]
            {
                new StorageAccountModel { Name = "store1" },
                new StorageAccountModel { Name = "STORE1" },
                new StorageAccountModel { Name = "store2" },
            };

            var issue = Assert.Single(DesignValidator.Validate(nodes), i => i.RuleId == "duplicate-name");
            Assert.Equal(ValidationSeverity.Error, issue.Severity);
        }

        [Fact]
        public void SameSubnetNameInDifferentNetworksIsFine()
        {
            var a = Vnet("10.0.0.0/16", ("default", "10.0.0.0/24"));
            a.Name = "vnet-a";
            var b = Vnet("10.1.0.0/16", ("default", "10.1.0.0/24"));
            b.Name = "vnet-b";

            var issues = DesignValidator.Validate(new NodeModel[] { a, b }.Concat(a.Subnets).Concat(b.Subnets));

            Assert.DoesNotContain(issues, i => i.RuleId == "duplicate-name");
        }

        [Fact]
        public void FirewallsSharingOnePolicyNameAreAnError()
        {
            var nodes = new NodeModel[] { new AzureFirewallModel { Name = "fw-a" }, new AzureFirewallModel { Name = "fw-b" } };

            var issue = Assert.Single(DesignValidator.Validate(nodes), i => i.RuleId == "duplicate-name");
            Assert.Contains("firewall policy name", issue.Message);
        }

        [Theory]
        [InlineData("sql-server", true)]   // the stencil's own default name was reported as invalid
        [InlineData("sqlprod01", true)]
        [InlineData("a", true)]
        [InlineData("Sql-Server", false)]  // capitals are not allowed
        [InlineData("-sql", false)]
        [InlineData("sql-", false)]
        [InlineData("sql_server", false)]
        public void SqlServerNameRuleMatchesAzure(string name, bool valid)
        {
            var issues = DesignValidator.Validate(new NodeModel[] { new SqlServerModel { Name = name } });

            Assert.Equal(valid, !issues.Any(i => i.RuleId == "naming"));
        }

        [Theory]
        [InlineData(AdsConstants.WebApp, "my-web", true)]
        [InlineData(AdsConstants.CosmosDBAccount, "orders-db", true)]
        [InlineData(AdsConstants.SqlServer, "sql-prod-01", true)]
        [InlineData(AdsConstants.WebApp, "My-Web", false)]
        [InlineData(AdsConstants.StorageAccount, "my-store", false)] // storage allows no hyphen at all
        public void LowercaseProfilesAllowTheSeparatorsTheyDeclare(string key, string name, bool valid)
        {
            Assert.Equal(valid, NamingEngine.IsValid(key, name));
        }

        [Fact]
        public void SuggestedNamesPassTheirOwnProfile()
        {
            foreach (var key in new[] { AdsConstants.WebApp, AdsConstants.FunctionApp, AdsConstants.CosmosDBAccount, AdsConstants.AiSearch, AdsConstants.APIM, AdsConstants.SqlServer })
                Assert.True(NamingEngine.IsValid(key, NamingEngine.Suggest(key, "orders", "prod")), key);
        }
    }
}
