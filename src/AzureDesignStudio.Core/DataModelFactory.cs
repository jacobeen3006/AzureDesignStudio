using AutoMapper;
using AzureDesignStudio.Core.AKS;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.App;
using AzureDesignStudio.Core.Cache;
using AzureDesignStudio.Core.CognitiveServices;
using AzureDesignStudio.Core.AppConfiguration;
using AzureDesignStudio.Core.Insights;
using AzureDesignStudio.Core.Logic;
using AzureDesignStudio.Core.DataFactory;
using AzureDesignStudio.Core.DocumentDB;
using AzureDesignStudio.Core.Search;
using AzureDesignStudio.Core.SignalRService;
using AzureDesignStudio.Core.ContainerInstance;
using AzureDesignStudio.Core.OperationalInsights;
using AzureDesignStudio.Core.RecoveryServices;
using AzureDesignStudio.Core.Web;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Components;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.ContainerRegistry;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Fabric;
using AzureDesignStudio.Core.KeyVault;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Messaging;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Storage;
using Blazor.Diagrams;
using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using System.Text.Json;

namespace AzureDesignStudio.Core;

public static partial class DataModelFactory
{
    public static void RegisterAzureModels(BlazorDiagram diagram)
    {
        // replace: true so circuit re-initializations (desktop reloads) re-register idempotently.
        diagram.RegisterComponent<VirtualNetworkModel, AzureGroupComponent>(replace: true);
        diagram.RegisterComponent<SubnetModel, AzureGroupComponent>(replace: true);
        diagram.RegisterComponent<SqlServerModel, AzureGroupComponent>(replace: true);
        diagram.RegisterComponent<AppServicePlanModel, AzureGroupComponent>(replace: true);
        diagram.RegisterComponent<ContainerAppsEnvironmentModel, AzureGroupComponent>(replace: true);
        diagram.RegisterComponent<AvailabilitySetModel, AzureGroupComponent>(replace: true);
        diagram.RegisterComponent<UnknownNodeModel, AzureNodeComponent>(replace: true);
    }
    public static NodeModel CreateNodeModelFromKey(string key, string name, string imagePath)
    {
        NodeModel result = key switch
        {
            //AdsConstants.ResourceGroup => new ResourceGroupModel(),
            AdsConstants.StorageAccount => new StorageAccountModel(),
            AdsConstants.VirtualNetwork => new VirtualNetworkModel(),
            AdsConstants.VirtualMachine => new VirtualMachineModel(),
            AdsConstants.Subnet => new SubnetModel(),
            AdsConstants.AzureFirewall => new AzureFirewallModel(),
            AdsConstants.Bastions => new BastionsModel(),
            AdsConstants.PublicIp => new PublicIpModel(),
            AdsConstants.SqlServer => new SqlServerModel(),
            AdsConstants.SqlDatabase => new SqlDatabaseModel(),
            AdsConstants.AppServicePlan => new AppServicePlanModel(),
            AdsConstants.FunctionApp => new FunctionAppModel(),
            AdsConstants.WebApp => new WebAppModel(),
            AdsConstants.AKSCluster => new AksClusterModel(),
            AdsConstants.APIM => new APIMModel(),
            AdsConstants.AppGateway => new AppGatewayModel(),
            AdsConstants.LoadBalancer => new LoadBalancerModel(),
            AdsConstants.PrivateEndpoint => new PrivateEndpointModel(),
            AdsConstants.DnsZone => new DnsZoneModel(),
            AdsConstants.AvailabilitySet => new AvailabilitySetModel(),
            AdsConstants.ServiceBus => new ServiceBusModel(),
            AdsConstants.EventHubs => new EventHubsModel(),
            AdsConstants.EventGrid => new EventGridModel(),
            AdsConstants.ContainerAppsEnvironment => new ContainerAppsEnvironmentModel(),
            AdsConstants.ContainerApp => new ContainerAppModel(),
            AdsConstants.ContainerAppsJob => new ContainerAppsJobModel(),
            AdsConstants.ContainerRegistry => new ContainerRegistryModel(),
            AdsConstants.KeyVault => new KeyVaultModel(),
            AdsConstants.AzureRedis => new AzureRedisModel(),
            AdsConstants.AzureOpenAI => new AzureOpenAIModel(),
            AdsConstants.FabricCapacity => new FabricCapacityModel(),
            AdsConstants.LogicApps => new LogicAppsModel(),
            AdsConstants.StaticWebApp => new StaticWebAppModel(),
            AdsConstants.ApplicationInsights => new ApplicationInsightsModel(),
            AdsConstants.AppConfiguration => new AppConfigurationModel(),
            AdsConstants.CosmosDBAccount => new CosmosDBAccountModel(),
            AdsConstants.AiSearch => new AiSearchModel(),
            AdsConstants.DataFactory => new DataFactoryModel(),
            AdsConstants.SignalR => new SignalRModel(),
            AdsConstants.AzureContainerInstance => new ContainerInstanceModel(),
            AdsConstants.VirtualMachineScaleSet => new VirtualMachineScaleSetModel(),
            AdsConstants.RecoveryServicesVault => new RecoveryServicesVaultModel(),
            AdsConstants.LogAnalyticsWorkspace => new LogAnalyticsWorkspaceModel(),
            AdsConstants.NatGateway => new NatGatewayModel(),
            AdsConstants.TrafficManagerProfile => new TrafficManagerProfileModel(),
            AdsConstants.ExpressRouteCircuit => new ExpressRouteCircuitModel(),
            AdsConstants.NetworkSecurityGroup => new NetworkSecurityGroupModel(),
            _ => new UnknownNodeModel(),
        };

        if (result is IAzureNode n)
        {
            n.ImagePath = imagePath;
        }
        if (result is AzureNodeBase ab)
        {
            ab.StencilKey = key;
        }
        if (result is IAzureResource r)
        {
            r.Name = name;
        }

        return result;
    }
    public static BaseLinkModel? LinkModelFactory(Diagram diagram, ILinkable source, Anchor targetAnchor)
    {
        if (source is not PortModel sourcePort)
            return null;

        var link = new LinkModel(new SinglePortAnchor(sourcePort), targetAnchor)
        {
            TargetMarker = LinkMarker.Arrow,
            Width = 3,
        };

        // The drag behavior starts with a PositionAnchor and swaps in the real port
        // on pointer-up/snap; re-derive the label then so an interactive link gets
        // the same type as one reloaded from a saved design.
        link.TargetChanged += (changed, _, _) => ApplySemantics((LinkModel)changed);
        link.SourceChanged += (changed, _, _) => ApplySemantics((LinkModel)changed);
        ApplySemantics(link);

        return link;
    }

    private static void ApplySemantics(LinkModel link)
    {
        var semantics = LinkTypeClassifier.Classify(ParentOf(link.Source), ParentOf(link.Target));
        link.Color = semantics.Color;
        link.SourceMarker = semantics.Bidirectional ? LinkMarker.Arrow : null;
        link.TargetMarker = LinkMarker.Arrow;

        if (link.Labels.Count == 0)
            link.AddLabel(semantics.Label, 0.5);
        else
            link.Labels[0].Content = semantics.Label;
    }

    private static NodeModel? ParentOf(Anchor anchor) => anchor switch
    {
        SinglePortAnchor portAnchor => portAnchor.Port.Parent,
        _ => anchor.Model as NodeModel,
    };
    public static partial Type? GetDtoTypeFromKey(string key);

    private static readonly Dictionary<Type, string> StencilKeyByType = new()
    {
        [typeof(StorageAccountModel)] = AdsConstants.StorageAccount,
        [typeof(VirtualNetworkModel)] = AdsConstants.VirtualNetwork,
        [typeof(VirtualMachineModel)] = AdsConstants.VirtualMachine,
        [typeof(SubnetModel)] = AdsConstants.Subnet,
        [typeof(AzureFirewallModel)] = AdsConstants.AzureFirewall,
        [typeof(BastionsModel)] = AdsConstants.Bastions,
        [typeof(PublicIpModel)] = AdsConstants.PublicIp,
        [typeof(SqlServerModel)] = AdsConstants.SqlServer,
        [typeof(SqlDatabaseModel)] = AdsConstants.SqlDatabase,
        [typeof(AppServicePlanModel)] = AdsConstants.AppServicePlan,
        [typeof(FunctionAppModel)] = AdsConstants.FunctionApp,
        [typeof(AksClusterModel)] = AdsConstants.AKSCluster,
        [typeof(WebAppModel)] = AdsConstants.WebApp,
        [typeof(APIMModel)] = AdsConstants.APIM,
        [typeof(AppGatewayModel)] = AdsConstants.AppGateway,
        [typeof(LoadBalancerModel)] = AdsConstants.LoadBalancer,
        [typeof(PrivateEndpointModel)] = AdsConstants.PrivateEndpoint,
        [typeof(DnsZoneModel)] = AdsConstants.DnsZone,
        [typeof(AvailabilitySetModel)] = AdsConstants.AvailabilitySet,
        [typeof(ServiceBusModel)] = AdsConstants.ServiceBus,
        [typeof(EventHubsModel)] = AdsConstants.EventHubs,
        [typeof(EventGridModel)] = AdsConstants.EventGrid,
        [typeof(ContainerAppsEnvironmentModel)] = AdsConstants.ContainerAppsEnvironment,
        [typeof(ContainerAppModel)] = AdsConstants.ContainerApp,
        [typeof(ContainerAppsJobModel)] = AdsConstants.ContainerAppsJob,
        [typeof(ContainerRegistryModel)] = AdsConstants.ContainerRegistry,
        [typeof(KeyVaultModel)] = AdsConstants.KeyVault,
        [typeof(AzureRedisModel)] = AdsConstants.AzureRedis,
        [typeof(AzureOpenAIModel)] = AdsConstants.AzureOpenAI,
        [typeof(FabricCapacityModel)] = AdsConstants.FabricCapacity,
        [typeof(LogicAppsModel)] = AdsConstants.LogicApps,
        [typeof(StaticWebAppModel)] = AdsConstants.StaticWebApp,
        [typeof(ApplicationInsightsModel)] = AdsConstants.ApplicationInsights,
        [typeof(AppConfigurationModel)] = AdsConstants.AppConfiguration,
        [typeof(CosmosDBAccountModel)] = AdsConstants.CosmosDBAccount,
        [typeof(AiSearchModel)] = AdsConstants.AiSearch,
        [typeof(DataFactoryModel)] = AdsConstants.DataFactory,
        [typeof(SignalRModel)] = AdsConstants.SignalR,
        [typeof(ContainerInstanceModel)] = AdsConstants.AzureContainerInstance,
        [typeof(VirtualMachineScaleSetModel)] = AdsConstants.VirtualMachineScaleSet,
        [typeof(RecoveryServicesVaultModel)] = AdsConstants.RecoveryServicesVault,
        [typeof(LogAnalyticsWorkspaceModel)] = AdsConstants.LogAnalyticsWorkspace,
        [typeof(NatGatewayModel)] = AdsConstants.NatGateway,
        [typeof(TrafficManagerProfileModel)] = AdsConstants.TrafficManagerProfile,
        [typeof(ExpressRouteCircuitModel)] = AdsConstants.ExpressRouteCircuit,
        [typeof(NetworkSecurityGroupModel)] = AdsConstants.NetworkSecurityGroup,
    };

    /// <summary>Stencil key for a node: the key stored at creation, or the type-based fallback for legacy designs.</summary>
    public static string GetStencilKey(NodeModel node) =>
        node is AzureNodeBase { StencilKey.Length: > 0 } b ? b.StencilKey : StencilKeyByType.GetValueOrDefault(node.GetType(), string.Empty);
    public static partial NodeModel? GetNodeModelFromDto(AzureNodeDto dto, IMapper mapper);
    public static async Task<DiagramGraph> SaveDiagramToDto(Diagram diagram, IMapper mapper)
    {
        if (diagram.Groups.Count == 0 && diagram.Nodes.Count == 0)
            return null!;

        var diagramGraph = new DiagramGraph();
        await Task.Run(() =>
        {
            // Groups
            foreach (var group in diagram.Groups)
            {
                if (group is IAzureNode n)
                {
                    diagramGraph.Groups.Add(n.GetNodeDto(mapper));
                }
            }
            // Nodes
            foreach (var node in diagram.Nodes)
            {
                if (node is IAzureNode n)
                {
                    diagramGraph.Nodes.Add(n.GetNodeDto(mapper));
                }
            }

            // Links
            foreach (var link in diagram.Links)
            {
                diagramGraph.Links.Add(mapper.Map<LinkModelDto>(link));
            }
        });

        return diagramGraph;
    }

    private static IList<AzureNodeDto>? GetNodesDto(IList<object> source)
    {
        var nodesDto = new List<AzureNodeDto>();
        foreach (var node in source)
        {
            // In-memory graphs (Adopt/Merge) hold typed DTOs; loaded JSON holds JsonElement.
            if (node is AzureNodeDto d)
            {
                // The engines emit base AzureNodeDto; GetNodeModelFromDto resolves the map by
                // RUNTIME type, so re-type base instances to their registered derived DTO.
                if (d.GetType() == typeof(AzureNodeDto) && GetDtoTypeFromKey(d.TypeKey) is { } derived)
                    nodesDto.Add((AzureNodeDto)JsonSerializer.Deserialize(JsonSerializer.Serialize(d), derived)!);
                else
                    nodesDto.Add(d);
            }
            else if (node is JsonElement je)
            {
                var typeKey = je.GetProperty("TypeKey").GetString() ?? string.Empty;
                var returnType = GetDtoTypeFromKey(typeKey) ?? typeof(AzureNodeDto);

                var g = JsonSerializer.Deserialize(je, returnType);
                if (g is AzureNodeDto dto)
                    nodesDto.Add(dto);
            }
        }
        return nodesDto;
    }

    public static void LoadDiagramFromDto(Diagram diagram, DiagramGraph diagramGraph, IMapper mapper, Dictionary<NodeModel, string>? savedEntityIds = null)
    {
        diagram.SuspendRefresh = true;
        // Handle Json deserialization.
        var groupsDto = GetNodesDto(diagramGraph.Groups) ?? throw new Exception("Groups' NodesDto is null.");

        // Model Ids are regenerated on load, so look them up by the saved dto Id.
        var groupById = new Dictionary<string, GroupModel>();

        // Load all groups
        var parentGroups = groupsDto.Where(g => g is AzureNodeDto d && string.IsNullOrEmpty(d.GroupId)).ToList();
        var childGroups = groupsDto.Where(g => g is AzureNodeDto d && !string.IsNullOrEmpty(d.GroupId)).ToList();
        foreach (var parent in parentGroups)
        {
            var group = GetNodeModelFromDto(parent, mapper);
            if (group is GroupModel g)
            {
                diagram.Groups.Add(g);
                groupById[parent.Id] = g;
                savedEntityIds?[g] = parent.Id;
            }
        }
        foreach (var child in childGroups)
        {
            var node = GetNodeModelFromDto(child, mapper);
            if (node is GroupModel n)
            {
                var group = groupById.GetValueOrDefault(child.GroupId!);
                // Sequence is important
                diagram.Groups.Add(n);
                savedEntityIds?[n] = child.Id;
                group?.AddChild(n);
            }
        }

        // Load all nodes
        var nodesDto = GetNodesDto(diagramGraph.Nodes) ?? throw new Exception("Nodes' NodesDto is null.");
        var nodeById = new Dictionary<string, NodeModel>();
        foreach (var nodeDto in nodesDto)
        {
            var node = GetNodeModelFromDto(nodeDto, mapper) ?? throw new Exception("NodeModel is null");
            diagram.Nodes.Add(node);
            nodeById[nodeDto.Id] = node;
            savedEntityIds?[node] = nodeDto.Id;
            if (!string.IsNullOrEmpty(nodeDto.GroupId))
            {
                var parentGroup = groupById.GetValueOrDefault(nodeDto.GroupId);
                parentGroup?.AddChild(node);
            }
        }

        // Load all links
        foreach (var linkDto in diagramGraph.Links)
        {
            // Search all groups first.
            NodeModel? sourceNode = groupById.GetValueOrDefault(linkDto.SourcePortParentId)
                    ?? nodeById.GetValueOrDefault(linkDto.SourcePortParentId)
                    ?? throw new Exception($"Cannot find the link's source node. SourceParentId = {linkDto.SourcePortParentId}");

            NodeModel? targetNode = groupById.GetValueOrDefault(linkDto.TargetPortParentId)
                    ?? nodeById.GetValueOrDefault(linkDto.TargetPortParentId)
                    ?? throw new Exception($"Cannot find the target node. TargetParentId = {linkDto.TargetPortParentId}");

            // A saved alignment with no matching port means the document predates the node's
            // port set (or is corrupt); name the offender instead of a bare 'no matching element'.
            var sourcePort = sourceNode.Ports.FirstOrDefault(p => p.Alignment == linkDto.SourcePortAlignment)
                    ?? throw new Exception($"Cannot find the link's source port. SourceParentId = {linkDto.SourcePortParentId}, Alignment = {linkDto.SourcePortAlignment}");
            var targetPort = targetNode.Ports.FirstOrDefault(p => p.Alignment == linkDto.TargetPortAlignment)
                    ?? throw new Exception($"Cannot find the link's target port. TargetParentId = {linkDto.TargetPortParentId}, Alignment = {linkDto.TargetPortAlignment}");
            var link = diagram.Options.Links.Factory!(diagram, sourcePort, new SinglePortAnchor(targetPort));
            if (link is not null)
                diagram.Links.Add(link);
        }

        diagram.SuspendRefresh = false;
        diagram.Refresh();
    }
}
