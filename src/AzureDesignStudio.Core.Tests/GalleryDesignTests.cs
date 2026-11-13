using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutoMapper;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.Validation;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The reference architectures the Gallery offers. Each one is built here from the same
    /// models the palette creates, so it is a design the app itself could have produced, and
    /// the checked-in files under <c>wwwroot/gallery</c> are verified against the app's own
    /// loader, validator and exporter. The catalogue used to name a file that was never in
    /// the repository, and two entries had no design at all.
    ///
    /// To regenerate the files after changing a builder, run the tests with
    /// <c>ADS_WRITE_GALLERY</c> set to the <c>wwwroot/gallery</c> directory.
    /// </summary>
    public class GalleryDesignTests
    {
        private static readonly IMapper Mapper =
            new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper();

        private static readonly IReadOnlyList<JsonElement> Stencils = LoadStencils();

        private static readonly Dictionary<string, string> Icons =
            Stencils.ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("iconPath").GetString()!);

        private static IReadOnlyList<JsonElement> LoadStencils()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ads-stencils.json")));
            return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
        }

        /// <summary>Gallery entries: stencil key and the design file it points at ("" = none).</summary>
        public static IEnumerable<object[]> GalleryEntries() =>
            Stencils.Where(s => s.TryGetProperty("referenceArchPath", out _))
                .Select(s => new object[] { s.GetProperty("key").GetString()!, s.GetProperty("referenceArchPath").GetString() ?? string.Empty });

        // ---------------------------------------------------------------- building

        /// <summary>A canvas that only accepts what the real one would: a node goes into a
        /// group only if its own drop rule allows it, and a link only between ports that
        /// may be connected.</summary>
        private sealed class Canvas
        {
            public BlazorDiagram Diagram { get; } = new();

            public Canvas() => Diagram.Options.Links.Factory = DataModelFactory.LinkModelFactory;

            public T Add<T>(string key, string name, double x, double y, GroupModel? parent = null) where T : NodeModel
            {
                var node = (T)DataModelFactory.CreateNodeModelFromKey(key, name, Icons[key]);
                node.SetPosition(x, y);

                var (allowed, why) = ((IAzureNode)node).IsDrappable(parent!);
                Assert.True(allowed, $"{name}: {why}");

                if (node is GroupModel group)
                    Diagram.Groups.Add(group);
                else
                    Diagram.Nodes.Add(node);
                parent?.AddChild(node);
                return node;
            }

            public SubnetModel Subnet(VirtualNetworkModel vnet, string name, string prefix, double x, double y, double width = 141, double height = 141)
            {
                var subnet = Add<SubnetModel>("subnet", name, x, y, vnet);
                subnet.AddressPrefix = prefix;
                subnet.Size = new Size(width, height); // used as is while the subnet is empty
                return subnet;
            }

            public void Link(NodeModel from, PortAlignment fromSide, NodeModel to, PortAlignment toSide)
            {
                var source = from.GetPort(fromSide) ?? throw new InvalidOperationException($"{from.Title} has no {fromSide} port.");
                var target = to.GetPort(toSide) ?? throw new InvalidOperationException($"{to.Title} has no {toSide} port.");
                Assert.True(source.CanAttachTo(target), $"A link from {((IAzureResource)from).Name} to {((IAzureResource)to).Name} is not allowed.");
                Diagram.Links.Add(new LinkModel(source, target));
            }
        }

        private static VirtualNetworkModel Network(Canvas canvas, string name, string addressSpace, double x, double y)
        {
            var vnet = canvas.Add<VirtualNetworkModel>("virtual-network", name, x, y);
            vnet.IpSpace = [new AddressSpace { AddressPrefix = addressSpace }];
            return vnet;
        }

        /// <summary>Hub and spoke: shared firewall, bastion and gateway subnet in the hub; two
        /// workload networks peered to it.</summary>
        private static BlazorDiagram HubSpoke()
        {
            var canvas = new Canvas();

            var hub = Network(canvas, "vnet-hub", "10.0.0.0/16", 520, 160);
            var firewallSubnet = canvas.Subnet(hub, "AzureFirewallSubnet", "10.0.1.0/26", 550, 200);
            var bastionSubnet = canvas.Subnet(hub, "AzureBastionSubnet", "10.0.2.0/26", 760, 200);
            canvas.Subnet(hub, "GatewaySubnet", "10.0.3.0/27", 550, 420, width: 351, height: 110);
            var firewall = canvas.Add<AzureFirewallModel>("azure-firewall", "afw-hub", 580, 230, firewallSubnet);
            var bastion = canvas.Add<BastionsModel>("azure-bastions", "bas-hub", 790, 230, bastionSubnet);
            var firewallIp = canvas.Add<PublicIpModel>("public-ip", "pip-afw-hub", 580, 20);
            var bastionIp = canvas.Add<PublicIpModel>("public-ip", "pip-bas-hub", 790, 20);
            canvas.Link(firewallIp, PortAlignment.Bottom, firewall, PortAlignment.Top);
            canvas.Link(bastionIp, PortAlignment.Bottom, bastion, PortAlignment.Top);

            var spoke1 = Network(canvas, "vnet-spoke-1", "10.1.0.0/16", 60, 220);
            var workload1 = canvas.Subnet(spoke1, "snet-workload", "10.1.1.0/24", 90, 260);
            canvas.Add<NodeModel>("virtual-machine", "vm-spoke-1", 120, 290, workload1);

            var spoke2 = Network(canvas, "vnet-spoke-2", "10.2.0.0/16", 1080, 220);
            var workload2 = canvas.Subnet(spoke2, "snet-workload", "10.2.1.0/24", 1110, 260);
            canvas.Add<NodeModel>("virtual-machine", "vm-spoke-2", 1140, 290, workload2);

            canvas.Link(spoke1, PortAlignment.Right, hub, PortAlignment.Left);
            canvas.Link(hub, PortAlignment.Right, spoke2, PortAlignment.Left);
            return canvas.Diagram;
        }

        /// <summary>AKS baseline: a cluster in its own subnet behind an application gateway,
        /// with the registry, vault and workspace it depends on.</summary>
        private static BlazorDiagram AksBaseline()
        {
            var canvas = new Canvas();

            var vnet = Network(canvas, "vnet-aks", "10.10.0.0/16", 260, 180);
            var gatewaySubnet = canvas.Subnet(vnet, "snet-appgw", "10.10.1.0/24", 290, 220);
            var nodeSubnet = canvas.Subnet(vnet, "snet-aks-nodes", "10.10.16.0/20", 520, 220);
            var gateway = canvas.Add<NodeModel>("app-gateway", "agw-aks", 320, 250, gatewaySubnet);
            canvas.Add<NodeModel>("aks-cluster", "aks-baseline", 550, 250, nodeSubnet);
            var gatewayIp = canvas.Add<PublicIpModel>("public-ip", "pip-agw-aks", 320, 30);
            canvas.Link(gatewayIp, PortAlignment.Bottom, gateway, PortAlignment.Top);

            canvas.Add<NodeModel>("container-registry", "acraksbaseline", 820, 180);
            canvas.Add<NodeModel>("key-vault", "kv-aks-baseline", 820, 300);
            canvas.Add<NodeModel>("log-analytics-workspace", "log-aks-baseline", 820, 420);
            return canvas.Diagram;
        }

        /// <summary>API Management landing zone: an internal API Management instance published
        /// through an application gateway, with its vault and monitoring.</summary>
        private static BlazorDiagram ApimLandingZone()
        {
            var canvas = new Canvas();

            var vnet = Network(canvas, "vnet-apim", "10.20.0.0/16", 260, 180);
            var gatewaySubnet = canvas.Subnet(vnet, "snet-appgw", "10.20.1.0/24", 290, 220);
            var apimSubnet = canvas.Subnet(vnet, "snet-apim", "10.20.2.0/24", 520, 220);
            var gateway = canvas.Add<NodeModel>("app-gateway", "agw-apim", 320, 250, gatewaySubnet);
            var apim = canvas.Add<APIM.APIMModel>("apim", "apim-landingzone", 550, 250, apimSubnet);
            apim.SkuName = "Developer";
            apim.SkuCapacity = 1;
            apim.VnetType = "Internal";
            apim.PublisherName = "Contoso";
            apim.PublisherEmail = "apiadmin@contoso.com";
            var gatewayIp = canvas.Add<PublicIpModel>("public-ip", "pip-agw-apim", 320, 30);
            canvas.Link(gatewayIp, PortAlignment.Bottom, gateway, PortAlignment.Top);

            canvas.Add<NodeModel>("key-vault", "kv-apim-landingzone", 820, 180);
            canvas.Add<NodeModel>("application-insights", "appi-apim-landingzone", 820, 300);
            canvas.Add<NodeModel>("log-analytics-workspace", "log-apim-landingzone", 820, 420);
            return canvas.Diagram;
        }

        private static readonly Dictionary<string, Func<BlazorDiagram>> Builders = new()
        {
            ["hub-spoke"] = HubSpoke,
            ["aks-baseline"] = AksBaseline,
            ["apim-landingzone"] = ApimLandingZone,
        };

        // ---------------------------------------------------------------- checking

        private static BlazorDiagram Load(string json, ICollection<string> detached)
        {
            // Read exactly as the app reads a gallery file: web defaults for the document,
            // the members as raw JSON.
            var graph = JsonSerializer.Deserialize<DiagramGraph>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var diagram = new BlazorDiagram();
            diagram.Options.Links.Factory = DataModelFactory.LinkModelFactory;
            DataModelFactory.LoadDiagramFromDto(diagram, graph, Mapper, detached: detached);
            return diagram;
        }

        /// <summary>The ARM template the app's Export menu produces for a canvas: every group
        /// except a subnet inside a network (its network emits it), then every plain node.</summary>
        private static string ExportTemplate(BlazorDiagram diagram)
        {
            var template = new ArmTemplate();
            foreach (var resource in diagram.Groups.Where(g => g.Group == null || g is not SubnetModel)
                         .Cast<NodeModel>().Concat(diagram.Nodes.Where(n => n is not GroupModel)).OfType<IAzureResource>())
            {
                template.AddParameters(resource.GetArmParameters());
                template.AddResource(resource.GetArmResources());
            }

            return template.GenerateArmTemplate();
        }

        private static void AssertSound(BlazorDiagram diagram)
        {
            var all = diagram.Groups.Cast<NodeModel>().Concat(diagram.Nodes).ToList();

            var errors = DesignValidator.Validate(all).Where(i => i.Severity == ValidationSeverity.Error).ToList();
            Assert.True(errors.Count == 0, "Validation errors: " + string.Join(" | ", errors.Select(e => e.Message)));

            var template = ExportTemplate(diagram);
            using var exported = JsonDocument.Parse(template);
            Assert.True(exported.RootElement.GetProperty("resources").GetArrayLength() >= all.Count,
                "Every item on the canvas should export at least one resource.");

            // The same gate the deploy path runs before any Azure call (DeployService).
            Assert.False(AzureDesignStudio.Discovery.Iac.ArmTemplateValidator.TryFindBlockingErrors(template, null, out var blocking),
                "The pre-deploy check refuses the export: " + blocking);
        }

        [Theory]
        [MemberData(nameof(GalleryEntries))]
        public void EveryGalleryEntryHasADesign(string key, string path)
        {
            Assert.True(Builders.ContainsKey(key), $"Gallery entry '{key}' has no builder.");
            Assert.False(string.IsNullOrWhiteSpace(path), $"Gallery entry '{key}' points at no file.");
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar))),
                $"Gallery entry '{key}' points at '{path}', which is not in wwwroot.");
        }

        [Theory]
        [MemberData(nameof(GalleryEntries))]
        public void CheckedInDesignLoadsValidatesAndExports(string key, string path)
        {
            var file = Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"'{path}' ({key}) is missing.");
            var detached = new List<string>();

            var diagram = Load(File.ReadAllText(file), detached);

            Assert.Empty(detached);
            Assert.DoesNotContain(diagram.Nodes, n => n is UnknownNodeModel);
            AssertSound(diagram);
        }

        [Theory]
        [MemberData(nameof(GalleryEntries))]
        public async Task CheckedInDesignMatchesItsBuilder(string key, string path)
        {
            // Same items, same nesting, same connections: the file is the builder's output,
            // not something edited by hand that the builder no longer describes.
            var file = Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"'{path}' ({key}) is missing.");
            var built = JsonSerializer.Serialize(await DataModelFactory.SaveDiagramToDto(Builders[key](), Mapper));

            Assert.Equal(Shape(Load(built, [])), Shape(Load(File.ReadAllText(file), [])));
        }

        private static string Shape(BlazorDiagram diagram)
        {
            static string NameOf(object? model) => (model as IAzureResource)?.Name ?? "-";
            static string End(Blazor.Diagrams.Core.Anchors.Anchor anchor) =>
                anchor.Model is PortModel port ? $"{NameOf(port.Parent)}.{port.Alignment}" : "?";

            var items = diagram.Groups.Cast<NodeModel>().Concat(diagram.Nodes)
                .Select(n => $"{n.GetType().Name}:{NameOf(n)}<{NameOf(n.Group)}")
                .OrderBy(s => s, StringComparer.Ordinal);
            var links = diagram.Links.Select(l => $"{End(l.Source)}->{End(l.Target)}").OrderBy(s => s, StringComparer.Ordinal);
            return string.Join("\n", items) + "\n--\n" + string.Join("\n", links);
        }

        // ---------------------------------------------------------------- one design per stencil

        /// <summary>Every palette entry that is a resource (the gallery entries and the
        /// not-yet-implemented resource group aside).</summary>
        public static IEnumerable<object[]> ResourceKeys() =>
            Stencils.Where(s => !s.TryGetProperty("referenceArchPath", out _))
                .Select(s => s.GetProperty("key").GetString()!)
                .Where(k => k != "resource-group")
                .Select(k => new object[] { k });

        /// <summary>
        /// The smallest design in which one palette entry is complete: the entry itself plus
        /// whatever it cannot be deployed without (a network and subnet, a plan, a server, a
        /// public address). Built through the same drop and link rules as the canvas.
        /// <paramref name="unique"/> goes into every name, so a run against Azure does not
        /// collide with names that are global (storage accounts, registries, vaults).
        /// </summary>
        private static BlazorDiagram SingleStencilDesign(string key, string unique)
        {
            var canvas = new Canvas();
            string Name(string stencil) => Common.NamingEngine.Suggest(stencil, "ads" + unique, "v");

            VirtualNetworkModel? vnet = null;
            SubnetModel Subnet(string? name = null)
            {
                vnet ??= Network(canvas, Name("virtual-network"), "10.50.0.0/16", 100, 100);
                return canvas.Subnet(vnet, name ?? "snet-workload", vnet.NextFreeSubnetPrefix()!, 140, 140);
            }

            T WithPublicIp<T>(T target) where T : NodeModel
            {
                var address = canvas.Add<PublicIpModel>("public-ip", Name("public-ip"), 170, 0);
                canvas.Link(address, PortAlignment.Bottom, target, PortAlignment.Top);
                return target;
            }

            switch (key)
            {
                case "virtual-network":
                    Network(canvas, Name(key), "10.50.0.0/16", 100, 100);
                    break;
                case "subnet":
                    Subnet();
                    break;
                case "azure-firewall":
                    WithPublicIp(canvas.Add<NodeModel>(key, Name(key), 170, 170, Subnet("AzureFirewallSubnet")));
                    break;
                case "nat-gateway": // ARM refuses one without a public IP
                    WithPublicIp(canvas.Add<NodeModel>(key, Name(key), 170, 170));
                    break;
                case "azure-bastions":
                    WithPublicIp(canvas.Add<NodeModel>(key, Name(key), 170, 170, Subnet("AzureBastionSubnet")));
                    break;
                case "app-gateway":
                    WithPublicIp(canvas.Add<NodeModel>(key, Name(key), 170, 170, Subnet("snet-appgw")));
                    break;
                case "sql-database":
                    canvas.Add<NodeModel>(key, Name(key), 170, 170, canvas.Add<GroupModel>("sql-server", Name("sql-server"), 100, 100));
                    break;
                case "web-app":
                case "function-app":
                    {
                        var plan = canvas.Add<Web.AppServicePlanModel>("appservice-plan", Name("appservice-plan"), 100, 100);
                        var app = canvas.Add<Web.WebAppModel>(key, Name(key), 170, 170, plan);
                        app.RuntimeStack = "node|~14";
                        if (app is Web.FunctionAppModel function)
                            function.HostingPlan = "consumption";
                        break;
                    }
                case "container-app":
                case "container-apps-job":
                    canvas.Add<NodeModel>(key, Name(key), 170, 170,
                        canvas.Add<GroupModel>("container-apps-environment", Name("container-apps-environment"), 100, 100));
                    break;
                case "virtual-machine":
                case "virtual-machine-scale-set":
                case "load-balancer": // internal: the frontend takes an address in the subnet
                    canvas.Add<NodeModel>(key, Name(key), 170, 170, Subnet());
                    break;
                case "private-endpoint":
                    {
                        // Connected to a vault through a link, the way one is drawn on the canvas.
                        var vault = canvas.Add<NodeModel>("key-vault", Name("key-vault"), 400, 100);
                        var endpoint = canvas.Add<NodeModel>(key, Name(key), 170, 170, Subnet("snet-endpoints"));
                        canvas.Link(endpoint, PortAlignment.Right, vault, PortAlignment.Left);
                        break;
                    }
                case "dns-zone":
                    canvas.Add<NodeModel>(key, $"ads{unique}.example.org", 100, 100); // a zone is named by its domain
                    break;
                case "apim":
                    {
                        var apim = canvas.Add<APIM.APIMModel>(key, Name(key), 100, 100);
                        apim.PublisherName = "Contoso";
                        apim.PublisherEmail = "apiadmin@contoso.com";
                        break;
                    }
                default:
                    {
                        // At the canvas root when the entry allows it, otherwise in a subnet.
                        var probe = (IAzureNode)DataModelFactory.CreateNodeModelFromKey(key, "probe", string.Empty);
                        canvas.Add<NodeModel>(key, Name(key), 170, 170, probe.IsDrappable(null!).result ? null : Subnet());
                        break;
                    }
            }

            return canvas.Diagram;
        }

        [Theory]
        [MemberData(nameof(ResourceKeys))]
        public void EveryPaletteEntryHasACompleteMinimalDesign(string key)
        {
            AssertSound(SingleStencilDesign(key, "t1"));
        }

        /// <summary>
        /// Writes each gallery design's exported ARM template, and the deployment-stack request
        /// that would carry it, to the directory named by <c>ADS_WRITE_TEMPLATES</c>. That is
        /// the input for checking the export against Azure itself:
        /// <c>az deployment group validate</c> / <c>what-if</c> for the template and
        /// <c>POST .../deploymentStacks/{name}/validate</c> for the stack request. Neither
        /// creates anything.
        /// </summary>
        [Fact]
        public void WriteExportedTemplatesWhenAsked()
        {
            var target = Environment.GetEnvironmentVariable("ADS_WRITE_TEMPLATES");
            foreach (var (key, build) in Builders)
            {
                var template = ExportTemplate(build());
                var stack = AzureDesignStudio.Discovery.Iac.DeploymentStack.Build(
                    "ads-" + key, template, new AzureDesignStudio.Discovery.Iac.StackPolicy(AzureDesignStudio.Discovery.Iac.StackUnmanageAction.Detach));
                Assert.True(stack.Success, string.Join(" | ", stack.Diagnostics.Select(d => d.Message)));

                if (string.IsNullOrWhiteSpace(target))
                    continue;

                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, key + ".template.json"), template);
                File.WriteAllText(Path.Combine(target, key + ".stack.json"), stack.Json);
            }

            if (string.IsNullOrWhiteSpace(target))
                return;

            // One template per palette entry, uniquely named for this run.
            var unique = Environment.GetEnvironmentVariable("ADS_TEMPLATE_SUFFIX") is { Length: > 0 } suffix
                ? suffix
                : Guid.NewGuid().ToString("N")[..5];
            foreach (var key in ResourceKeys().Select(k => (string)k[0]))
            {
                try
                {
                    File.WriteAllText(Path.Combine(target, "stencil-" + key + ".template.json"), ExportTemplate(SingleStencilDesign(key, unique)));
                }
                catch (Exception e)
                {
                    File.WriteAllText(Path.Combine(target, "stencil-" + key + ".error.txt"), e.Message);
                }
            }
        }

        [Fact]
        public async Task WriteGalleryFilesWhenAsked()
        {
            var target = Environment.GetEnvironmentVariable("ADS_WRITE_GALLERY");
            foreach (var (key, build) in Builders)
            {
                var diagram = build();
                AssertSound(diagram); // a builder that produces an invalid design is a bug either way

                if (string.IsNullOrWhiteSpace(target))
                    continue;

                var path = Stencils.Single(s => s.GetProperty("key").GetString() == key).GetProperty("referenceArchPath").GetString()!;
                var graph = await DataModelFactory.SaveDiagramToDto(diagram, Mapper);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, Path.GetFileName(path)),
                    JsonSerializer.Serialize(graph, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }
    }
}
