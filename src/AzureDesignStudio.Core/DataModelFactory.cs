using AutoMapper;
//using AzureDesignStudio.Core.AKS;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.App;
using AzureDesignStudio.Core.Cache;
using AzureDesignStudio.Core.CognitiveServices;
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
        diagram.RegisterComponent<VirtualNetworkModel, AzureGroupComponent>();
        diagram.RegisterComponent<SubnetModel, AzureGroupComponent>();
        diagram.RegisterComponent<SqlServerModel, AzureGroupComponent>();
        diagram.RegisterComponent<AppServicePlanModel, AzureGroupComponent>();
        diagram.RegisterComponent<ContainerAppsEnvironmentModel, AzureGroupComponent>();
        diagram.RegisterComponent<AvailabilitySetModel, AzureGroupComponent>();
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
            //AdsConstants.AKSCluster => new AKSModel(),
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
            _ => throw new NotImplementedException(),
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
            Width = 1,
        };

        if (sourcePort is VirtualNetworkPort)
        {
            // VNet peering link.
            link.Color = "#0078d7";
            link.SourceMarker = LinkMarker.Arrow;
        }

        return link;
    }
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
            if (node is JsonElement je)
            {
                var typeKey = je.GetProperty("TypeKey").GetString() ?? string.Empty;
                var returnType = GetDtoTypeFromKey(typeKey) 
                    ?? throw new Exception($"Cannot find the deserialization type from the type key: {typeKey}.");

                var g = JsonSerializer.Deserialize(je, returnType);
                if (g is AzureNodeDto d)
                    nodesDto.Add(d);
            }
        }
        return nodesDto;
    }

    public static void LoadDiagramFromDto(Diagram diagram, DiagramGraph diagramGraph, IMapper mapper)
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

            var sourcePort = sourceNode.Ports.Single(p => p.Alignment == linkDto.SourcePortAlignment);
            var targetPort = targetNode.Ports.Single(p => p.Alignment == linkDto.TargetPortAlignment);
            var link = diagram.Options.Links.Factory!(diagram, sourcePort, new SinglePortAnchor(targetPort));
            diagram.Links.Add(link);
        }

        diagram.SuspendRefresh = false;
        diagram.Refresh();
    }
}
