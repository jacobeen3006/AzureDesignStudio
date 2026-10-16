using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Report;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// 9.6 closer: the report bundle's topology.svg. The point is a shareable, honest picture -
/// same items always mean same bytes, hostile names can never smuggle script into a bundle,
/// and an absent section stays absent rather than shipping an empty frame.
/// </summary>
public class TopologySvgWriterTests
{
    private const string Sub = "/subscriptions/sub1";
    private const string Vnet = Sub + "/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet1";
    private const string SubnetA = Vnet + "/subnets/subnetA";
    private const string Nic = Sub + "/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic1";

    private static InventoryItem Item(string id, string type, string? props = null) =>
        new(id, id.Split('/')[^1], type, "westeurope",
            id.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase)
                ? id.Split('/')[4] : string.Empty,
            id.Split('/')[2],
            new Dictionary<string, string>(),
            props is null ? null : System.Text.Json.JsonDocument.Parse(props).RootElement.Clone());

    private static InventoryItem VnetItem() => Item(Vnet, "Microsoft.Network/virtualNetworks",
        """{"subnets":[{"name":"subnetA","properties":{}}]}""");

    private static InventoryItem NicItem() => Item(Nic, "Microsoft.Network/networkInterfaces",
        "{\"ipConfigurations\":[{\"properties\":{\"subnet\":{\"id\":\"" + SubnetA + "\"}}}]}");

    [Fact]
    public void RenderDrawsNodesFramesEdgesAndLegend()
    {
        var svg = TopologySvgWriter.Render([VnetItem(), NicItem()]);

        Assert.NotNull(svg);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains(">vnet1<", svg);
        Assert.Contains(">nic1<", svg);
        Assert.Contains(">subnetA<", svg);
        Assert.Contains(">sub1<", svg);   // subscription frame
        Assert.Contains(">rg<", svg);     // resource group frame
        Assert.Contains(TopologyLayout.EdgeColor(TopologyEdgeKind.InSubnet), svg);
        Assert.Contains("in subnet", svg); // legend word, no hover in a static file
        Assert.EndsWith("</svg>", svg);
    }

    [Fact]
    public void SameItemsAlwaysProduceTheSameBytes()
    {
        var first = TopologySvgWriter.Render([VnetItem(), NicItem()]);
        var again = TopologySvgWriter.Render([VnetItem(), NicItem()]);
        var shuffled = TopologySvgWriter.Render([NicItem(), VnetItem()]);

        Assert.Equal(first, again);
        Assert.Equal(first, shuffled); // layout sorts by ARM id, input order is not identity
    }

    [Fact]
    public void HostileNamesAreEncodedAndNeverReachTheSafeSvgGate()
    {
        // The name is not the id tail here: the scan row is hostile, and a shareable
        // static file must survive it.
        InventoryItem evil = new(Sub + "/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/evil",
            "<script>alert(1)</script>&x", "Microsoft.Network/virtualNetworks", "westeurope", "rg", "sub1",
            new Dictionary<string, string>(), null);

        var svg = TopologySvgWriter.Render([evil]);

        Assert.NotNull(svg);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/scrip", svg); // label truncated, then encoded
        Assert.True(ReportBundle.SafeSvg(svg)); // the bundle's own gate accepts our output
    }

    [Fact]
    public void NothingToDrawMeansNoFileAndNoHtmlSection()
    {
        Assert.Null(TopologySvgWriter.Render([]));

        var bare = new ReportModel("t", "scope", DateTimeOffset.UnixEpoch, null, null, null, null, null, null);
        var withSvg = new ReportModel("t", "scope", DateTimeOffset.UnixEpoch, null, null, null, null, null,
            TopologySvgWriter.Render([VnetItem(), NicItem()]));
        var smuggled = new ReportModel("t", "scope", DateTimeOffset.UnixEpoch, null, null, null, null, null,
            "<svg><script>alert(1)</script></svg>");

        Assert.False(ReportBundle.Build(bare).ContainsKey(ReportBundle.SvgFile));
        Assert.DoesNotContain("<h2>Topology</h2>", ReportBundle.Build(bare)[ReportBundle.HtmlFile]);

        var files = ReportBundle.Build(withSvg);
        Assert.True(files.ContainsKey(ReportBundle.SvgFile));
        Assert.Contains("<h2>Topology</h2>", files[ReportBundle.HtmlFile]);
        Assert.Contains(files[ReportBundle.SvgFile], files[ReportBundle.HtmlFile]);

        var dirty = ReportBundle.Build(smuggled);
        Assert.False(dirty.ContainsKey(ReportBundle.SvgFile));
        Assert.DoesNotContain("alert(1)", dirty[ReportBundle.HtmlFile]);
    }
}
