using System.Linq;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>Undo/redo over deletes, following the sequence the canvas delete command runs.</summary>
    public class DiagramHistoryTests
    {
        private static (BlazorDiagram Diagram, DiagramHistory History, VirtualNetworkModel Vnet, SubnetModel Subnet, AzureFirewallModel Firewall) Design()
        {
            var diagram = new BlazorDiagram();
            var vnet = new VirtualNetworkModel { Name = "vnet" };
            var subnet = new SubnetModel { Name = "AzureFirewallSubnet" };
            var firewall = new AzureFirewallModel { Name = "fw" };
            diagram.Groups.Add(vnet);
            diagram.Groups.Add(subnet);
            diagram.Nodes.Add(firewall);
            vnet.AddChild(subnet);
            subnet.AddChild(firewall);

            var history = diagram.EnsureHistory();
            history.Attach();
            return (diagram, history, vnet, subnet, firewall);
        }

        // The layers detach a model from its parent before the Removed event fires, so the
        // parent is unknown by the time the removal is recorded unless the delete path records it.
        [Fact]
        public void UndoingANodeDeleteReturnsItToItsGroup()
        {
            var (diagram, history, _, subnet, firewall) = Design();

            history.Capture(() =>
            {
                history.RecordChildMembership(subnet, firewall, added: false);
                diagram.Nodes.Remove(firewall);
            });
            Assert.Empty(diagram.Nodes);
            Assert.Empty(subnet.Children);

            history.Undo();

            Assert.Same(firewall, Assert.Single(diagram.Nodes));
            Assert.Same(subnet, firewall.Group);
            Assert.Same(firewall, Assert.Single(subnet.Children));
        }

        [Fact]
        public void UndoingAGroupDeleteReturnsItToItsParentWithItsChildren()
        {
            var (diagram, history, vnet, subnet, firewall) = Design();

            history.Capture(() =>
            {
                history.SnapshotChildren(subnet);
                history.RecordChildMembership(vnet, subnet, added: false);
                diagram.Groups.Delete(subnet);
            });
            Assert.DoesNotContain(subnet, diagram.Groups);
            Assert.Empty(vnet.Children);

            history.Undo();

            Assert.Contains(subnet, diagram.Groups);
            Assert.Same(vnet, subnet.Group);
            Assert.Same(subnet, firewall.Group);
            Assert.Contains(firewall, diagram.Nodes);
        }

        [Fact]
        public void RedoRemovesItAgainAndASecondUndoRestoresItAgain()
        {
            var (diagram, history, _, subnet, firewall) = Design();
            history.Capture(() =>
            {
                history.RecordChildMembership(subnet, firewall, added: false);
                diagram.Nodes.Remove(firewall);
            });

            history.Undo();
            history.Redo();
            Assert.Empty(diagram.Nodes);
            Assert.Empty(subnet.Children);

            history.Undo();
            Assert.Same(subnet, firewall.Group);
        }
    }
}
