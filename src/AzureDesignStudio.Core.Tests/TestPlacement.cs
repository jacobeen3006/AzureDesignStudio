using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Puts a model where it can be exported, for tests that are about something else. A
    /// virtual machine, scale set, private endpoint or internal load balancer is created in a
    /// subnet; exported on its own it is not a deployable resource, and the models refuse it.
    /// </summary>
    internal static class TestPlacement
    {
        public const string SampleTargetId =
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Sql/servers/ads-sql";

        /// <summary>A fresh network with one /24 subnet.</summary>
        public static SubnetModel NewSubnet(string vnetName = "ads-vnet", string subnetName = "ads-snet")
        {
            var vnet = new VirtualNetworkModel { Name = vnetName };
            return vnet.AddSubnet(new SubnetModel { Name = subnetName, AddressPrefix = "10.0.0.0/24" });
        }

        /// <summary>Places <paramref name="model"/> in a subnet when it needs one, and gives a
        /// private endpoint a target. Other models are left as they are.</summary>
        public static T MakeExportable<T>(T model) where T : NodeModel
        {
            switch (model)
            {
                case VirtualMachineModel or VirtualMachineScaleSetModel or LoadBalancerModel:
                    NewSubnet().AddChild(model);
                    break;
                case NatGatewayModel nat:
                    {
                        // A NAT gateway without a public IP is refused by Azure, so it is not exportable.
                        var pip = new PublicIpModel { Name = "ads-nat-pip" };
                        new BlazorDiagram().Links.Add(new LinkModel(new SinglePortAnchor(pip.Ports[1]), new SinglePortAnchor(nat.Ports[0])));
                        break;
                    }
                case PrivateEndpointModel endpoint:
                    endpoint.TargetResourceId = SampleTargetId;
                    NewSubnet().AddChild(model);
                    break;
            }

            return model;
        }
    }
}
