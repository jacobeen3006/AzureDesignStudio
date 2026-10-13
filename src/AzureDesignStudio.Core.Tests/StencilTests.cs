using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Offline validation of the stencil catalog consumed by AdsContext.
    /// </summary>
    public class StencilTests
    {
        private static JsonElement[] LoadStencils()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ads-stencils.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
        }

        [Fact]
        public void StencilCatalogIsNotEmpty()
        {
            Assert.NotEmpty(LoadStencils());
        }

        [Fact]
        public void EveryStencilEntryHasRequiredFields()
        {
            foreach (var entry in LoadStencils())
            {
                foreach (var field in new[] { "key", "name", "iconPath", "label" })
                {
                    Assert.True(entry.TryGetProperty(field, out var value) && !string.IsNullOrWhiteSpace(value.GetString()),
                        $"Stencil entry missing or empty '{field}': {entry}");
                }
                Assert.True(entry.TryGetProperty("category", out var category)
                    && category.ValueKind == JsonValueKind.Number
                    && category.GetInt32() is >= 0 and <= 5,
                    $"Stencil entry has invalid category: {entry}");
            }
        }

        [Fact]
        public void StencilKeysAreUnique()
        {
            var keys = LoadStencils().Select(e => e.GetProperty("key").GetString()).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        [Fact]
        public void IconPathsFollowAssetConvention()
        {
            foreach (var entry in LoadStencils())
            {
                Assert.StartsWith("assets/azure/", entry.GetProperty("iconPath").GetString());
            }
        }
    }
}
