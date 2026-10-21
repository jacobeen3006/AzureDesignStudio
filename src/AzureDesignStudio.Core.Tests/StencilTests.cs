using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Models;
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
                    && category.GetInt32() is >= 0 and <= 9,
                    $"Stencil entry has invalid category: {entry}");
            }
        }

        [Fact]
        public void EveryCategoryHasAtLeastOneStencil()
        {
            var used = LoadStencils()
                .Select(e => e.GetProperty("category").GetInt32())
                .Distinct()
                .OrderBy(c => c)
                .ToArray();

            Assert.Equal(Enumerable.Range(0, 10), used);
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

        [Fact]
        public void EveryImplementedResourceTypeHasAStencil()
        {
            var catalogKeys = LoadStencils().Select(e => e.GetProperty("key").GetString()!).ToHashSet();
            var modeledKeys = typeof(AdsConstants)
                .GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .Where(key => DataModelFactory.CreateNodeModelFromKey(key, "ads-stencil", "assets/azure/test.svg")
                    is not UnknownNodeModel)
                .ToHashSet();

            Assert.Subset(catalogKeys, modeledKeys);
        }
    }
}
