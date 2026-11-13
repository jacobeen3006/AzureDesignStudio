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

        // The MCP host serializes the report with default options; agents read names, not numbers.
        [Fact]
        public async Task Report_SerializesEnumsAsNamesWithDefaultOptions()
        {
            var stored = JsonSerializer.Serialize(await DataModelFactory.SaveDiagramToDto(GalleryDesignTests.Builders["hub-spoke"](), Mapper));

            var json = JsonSerializer.Serialize(DesignCheck.Run(stored).Findings);

            Assert.Contains("\"Pillar\":\"Reliability\"", json);
            Assert.Contains("\"Severity\":\"Warning\"", json);
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
