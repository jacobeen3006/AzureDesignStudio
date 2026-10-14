using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 10.1 - text-first .ads.json: lossless roundtrip, git-diff determinism, typed rejection.</summary>
public class AdsDesignTextTests
{
    private static AzureNodeDto Node(string id, string typeKey = "vm") => new()
    {
        Id = id,
        TypeKey = typeKey,
        Name = $"name-{id}",
        Location = "westeurope",
    };

    private static DiagramGraph Graph() => new()
    {
        Nodes =
        {
            Node("b"),
            new { TypeKey = "vnet", Id = "a", Name = "vnet-a", Extra = new { subnet = "default", cidr = "10.0.0.0/16" } },
        },
        Groups = { Node("g1", "vnet") },
        Links =
        {
            new LinkModelDto { SourcePortParentId = "b", TargetPortParentId = "a" },
            new LinkModelDto { SourcePortParentId = "a", TargetPortParentId = "g1" },
        },
    };

    [Fact]
    public void WriteThenRead_PreservesEveryFieldIncludingExtras()
    {
        var text = AdsDesignText.Write(Graph());
        var file = AdsDesignText.Read(text);

        Assert.Equal(AdsDesignText.Version, file.Version);
        Assert.Null(file.Name);
        Assert.Equal(2, file.Graph.Nodes.Count);
        Assert.Single(file.Graph.Groups);
        Assert.Equal(2, file.Graph.Links.Count);

        var vnet = Assert.IsType<JsonElement>(file.Graph.Nodes.First(n =>
            n is JsonElement e && e.TryGetProperty("Name", out var nm) && nm.GetString() == "vnet-a"));
        Assert.Equal("10.0.0.0/16", vnet.GetProperty("Extra").GetProperty("cidr").GetString()); // unknown payload survives
        Assert.Equal("vnet", vnet.GetProperty("TypeKey").GetString()); // loader dispatch key intact
    }

    [Fact]
    public void Write_IsIdempotentAndIdentitySorted()
    {
        var once = AdsDesignText.Write(Graph());
        var twice = AdsDesignText.Write(AdsDesignText.Read(once));
        Assert.Equal(once, twice); // the git-diff invariant: unchanged design -> unchanged bytes

        var nodes = JsonDocument.Parse(once).RootElement.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(["a", "b"], nodes.Select(n => n.GetProperty("Id").GetString()!).ToArray()); // sorted by id, insertion order irrelevant
        var links = JsonDocument.Parse(once).RootElement.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal("a", links[0].GetProperty("SourcePortParentId").GetString());
    }

    [Fact]
    public void Write_UsesSavedDesignCasing_AndCarriesName()
    {
        var text = AdsDesignText.Write(Graph(), "prod-network");
        Assert.Contains("\"TypeKey\"", text); // PascalCase matches the live saved-design format the canvas loader parses
        Assert.Contains("\"name\":\"prod-network\"", text.Replace("\r", "").Replace("\n", "").Replace(" ", ""));

        var back = AdsDesignText.Read(text);
        Assert.Equal("prod-network", back.Name);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")] // array root
    [InlineData("""{"version":999,"nodes":[]}""")] // unknown version
    [InlineData("""{"nodes":[]}""")] // missing version
    [InlineData("""{"version":1,"nodes":{}}""")] // section not array
    [InlineData("""{"version":1,"nodes":[7]}""")] // entity not object
    public void Read_MalformedInput_IsTypedFormatError(string text)
    {
        var e = Assert.Throws<AdsDesignFormatException>(() => AdsDesignText.Read(text));
        Assert.False(string.IsNullOrWhiteSpace(e.Message)); // localized, not a raw parser leak
    }

    [Fact]
    public void Read_BadLinkEntity_IsTypedFormatError()
    {
        var e = Assert.Throws<AdsDesignFormatException>(() =>
            AdsDesignText.Read("""{"version":1,"links":[{"SourcePortParentId":42}]}"""));
        Assert.Contains("link", e.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
