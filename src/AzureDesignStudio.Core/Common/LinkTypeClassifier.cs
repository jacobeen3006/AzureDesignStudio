using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.AKS;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Common;

/// <summary>Semantic type of a canvas connection, derived from its endpoints.</summary>
public readonly record struct LinkSemantics(string Label, string? Color = null, bool Bidirectional = false);

/// <summary>
/// Names a connection from the resource types it joins. The label is regenerated
/// whenever a link is created or loaded, so saved designs need no extra schema.
/// </summary>
public static class LinkTypeClassifier
{
    public static LinkSemantics Classify(NodeModel? source, NodeModel? target)
    {
        if (source is null || target is null)
            return new("Connection");

        if (source is VirtualNetworkModel && target is VirtualNetworkModel)
            return new("Peering", "#0078d7", Bidirectional: true);

        if (source is PublicIpModel || target is PublicIpModel)
            return new("Public IP");

        if (source is PrivateEndpointModel || target is PrivateEndpointModel)
            return source is DnsZoneModel || target is DnsZoneModel ? new("Private DNS") : new("Private Link");

        if (source is DnsZoneModel || target is DnsZoneModel)
            return new("DNS");

        if (source is NetworkSecurityGroupModel || target is NetworkSecurityGroupModel)
            return new("Network security");

        if (source is NatGatewayModel || target is NatGatewayModel)
            return new("Outbound NAT");

        if (source is ExpressRouteCircuitModel || target is ExpressRouteCircuitModel)
            return new("ExpressRoute");

        if (source is TrafficManagerProfileModel || target is TrafficManagerProfileModel)
            return new("Traffic routing");

        if (source is LoadBalancerModel || target is LoadBalancerModel)
            return new("Load balancing");

        if (source is AppGatewayModel || target is AppGatewayModel)
            return new("Ingress");

        if (source is AzureFirewallModel || target is AzureFirewallModel)
            return new("Inspection");

        if (source is BastionsModel || target is BastionsModel)
            return new("Remote access");

        if (source is APIMModel || target is APIMModel)
            return new("API gateway");

        if (source is AksClusterModel || target is AksClusterModel)
            return new("Node network");

        if (source is VirtualNetworkModel || target is VirtualNetworkModel)
            return new("Network");

        return new("Connection");
    }
}
