using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutoMapper;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Validation;
using Blazor.Diagrams.Core.Models;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>What the ads_design_lint MCP tool runs: a stored design payload in, a report out.</summary>
    public class DesignCheckTests
    {
        private static readonly IMapper Mapper =
            new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper();

        [Theory]
        [InlineData("hub-spoke")]
        [InlineData("aks-baseline")]
        [InlineData("apim-landingzone")]
        public async Task Run_GalleryDesign_MatchesTheDirectLint(string key)
        {
            var diagram = GalleryDesignTests.Builders[key]();
            var stored = JsonSerializer.Serialize(await DataModelFactory.SaveDiagramToDto(diagram, Mapper));
            var models = diagram.Groups.Cast<NodeModel>().Concat(diagram.Nodes).ToList();

            var report = DesignCheck.Run(stored);

            var direct = ValidationReport.For(models);
            Assert.NotEmpty(direct.Findings);
            Assert.Equal(direct.Findings.Select(f => $"{f.RuleId}:{f.NodeName}").OrderBy(s => s),
                report.Findings.Select(f => $"{f.RuleId}:{f.NodeName}").OrderBy(s => s));
            Assert.Equal(direct.Issues.Count, report.Issues.Count);
        }

        // Agents read enum names, not numbers: the [JsonConverter] attributes keep that true under any options.
        [Fact]
        public async Task Report_SerializesEnumsAsNames()
        {
            var stored = JsonSerializer.Serialize(await DataModelFactory.SaveDiagramToDto(GalleryDesignTests.Builders["hub-spoke"](), Mapper));
            var report = DesignCheck.Run(stored);
            Assert.NotEmpty(report.Findings);
            Assert.NotEmpty(report.Issues);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(report));

            // Property casing is the host's business; only the value kind matters here.
            static JsonElement Prop(JsonElement o, string name) =>
                o.EnumerateObject().Single(p => p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)).Value;

            foreach (var finding in Prop(doc.RootElement, "findings").EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, Prop(finding, "pillar").ValueKind);
                Assert.Equal(JsonValueKind.String, Prop(finding, "severity").ValueKind);
            }
            foreach (var issue in Prop(doc.RootElement, "issues").EnumerateArray())
                Assert.Equal(JsonValueKind.String, Prop(issue, "severity").ValueKind);
        }

        [Fact]
        public async Task Run_UnknownGroupType_ThrowsInvalidData()
        {
            var stored = JsonSerializer.Serialize(await DataModelFactory.SaveDiagramToDto(GalleryDesignTests.Builders["hub-spoke"](), Mapper));
            var root = System.Text.Json.Nodes.JsonNode.Parse(stored)!;
            var groups = root["Groups"]!.AsArray();
            Assert.NotEmpty(groups);
            // A copy of a stored group that no link points at, as a newer build's group type would look.
            var unknown = groups[0]!.DeepClone();
            unknown["Id"] = "unknown-group-1";
            unknown["GroupId"] = null;
            unknown["TypeKey"] = "no-such-group-type";
            groups.Add(unknown);

            var ex = Assert.Throws<InvalidDataException>(() => DesignCheck.Run(root.ToJsonString()));
            Assert.Contains("group", ex.Message);
            Assert.Contains("no-such-group-type", ex.Message);
        }

        [Theory]
        [InlineData("hub-spoke")]
        [InlineData("aks-baseline")]
        [InlineData("apim-landingzone")]
        public async Task Run_GalleryDesigns_StillLoadCompletely(string key)
        {
            var stored = JsonSerializer.Serialize(await DataModelFactory.SaveDiagramToDto(GalleryDesignTests.Builders[key](), Mapper));
            DesignCheck.Run(stored);
        }

        [Fact]
        public void Run_EmptyDesign_HasNothingToReport()
        {
            var report = DesignCheck.Run("{}");
            Assert.Empty(report.Issues);
            Assert.Empty(report.Findings);
        }

        [Theory]
        [InlineData("{not json")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("\"text\"")]
        [InlineData("")]
        public void Run_PayloadThatIsNotAGraph_ThrowsInvalidData(string payload)
        {
            var ex = Assert.Throws<InvalidDataException>(() => DesignCheck.Run(payload));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
    }
}
