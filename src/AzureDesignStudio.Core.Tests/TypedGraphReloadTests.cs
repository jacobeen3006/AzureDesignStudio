using System;
using System.Threading.Tasks;
using Blazor.Diagrams.Core.Models;
using AutoMapper;
using AzureDesignStudio.Core;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using Blazor.Diagrams;
using AzureDesignStudio.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

// AdoptEngine/Merge hand the canvas loader in-memory graphs with typed DTO members;
// the TopMenu load path hands it deserialized JSON (JsonElement members). Both shapes
// must load - the typed shape once loaded zero nodes and froze the drawer.
public class TypedGraphReloadTests
{
    [Fact]
    public async Task LoadDiagramFromDto_TypedAdoptionGraph_LoadsNodes()
    {
        var adoption = AdoptEngine.Adopt(new[]
        {
            new AdoptableResource(
                "/subscriptions/SUB-1/resourceGroups/rg-smoke/providers/Microsoft.Storage/storageAccounts/stbsmokea1b2",
                "Microsoft.Storage/storageAccounts", "stbsmokea1b2", "westeurope"),
        });

        var diagram = new BlazorDiagram();
        var mapper = new Mapper(new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()));
        var load = Task.Run(() => DataModelFactory.LoadDiagramFromDto(diagram, adoption.Graph, mapper));
        var done = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == load, "LoadDiagramFromDto did not return within 10s.");
        await load;

        var node = Assert.Single(diagram.Nodes);
        Assert.Equal("stbsmokea1b2", ((IAzureResource)node).Name);
    }

    [Fact]
    public void LoadDiagramFromDto_UnknownPortAlignment_NamesTheOffendingLinkPort()
    {
        // A saved link whose alignment matches no port on the node (corrupt or
        // catalog-evolved document) must fail with an actionable message naming the
        // link endpoint, not a bare LINQ "Sequence contains no matching element".
        var adoption = AdoptEngine.Adopt(new[]
        {
            new AdoptableResource(
                "/subscriptions/SUB-1/resourceGroups/rg-smoke/providers/Microsoft.Storage/storageAccounts/stbsmokea1b2",
                "Microsoft.Storage/storageAccounts", "stbsmokea1b2", "westeurope"),
            new AdoptableResource(
                "/subscriptions/SUB-1/resourceGroups/rg-smoke/providers/Microsoft.Storage/storageAccounts/stbsmokec3d4",
                "Microsoft.Storage/storageAccounts", "stbsmokec3d4", "westeurope"),
        });
        var sourceId = ((AzureNodeDto)adoption.Graph.Nodes[0]).Id;
        var targetId = ((AzureNodeDto)adoption.Graph.Nodes[1]).Id;
        adoption.Graph.Links.Add(new LinkModelDto
        {
            SourcePortParentId = sourceId,
            SourcePortAlignment = (PortAlignment)3, // diagonal: models register cardinal ports only
            TargetPortParentId = targetId,
            TargetPortAlignment = PortAlignment.Left,
        });

        var diagram = new BlazorDiagram();
        var mapper = new Mapper(new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()));
        var ex = Assert.Throws<Exception>(() => DataModelFactory.LoadDiagramFromDto(diagram, adoption.Graph, mapper));

        Assert.Contains("source port", ex.Message);
        Assert.Contains(sourceId, ex.Message);
    }
}
