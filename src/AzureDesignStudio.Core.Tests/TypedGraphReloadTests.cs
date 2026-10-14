using System;
using System.Threading.Tasks;
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
}
