using AzureDesignStudio.Core.Cost;
using System.Collections.Generic;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Offline tests for meter selection and monthly math. Meter fixtures use the
    /// real shapes verified against prices.azure.com (westus2, Oct 2026).
    /// </summary>
    public class CostEstimatorTests
    {
        private static RetailMeter Meter(string sku, string meter, string unit, double price, string product = "Test Product") =>
            new("Test Service", sku, meter, product, unit, price, "USD");

        private static CostMapEntry Entry(string policy, string normalize = "none", string hint = "", string defaultSku = "", string[]? exclude = null) =>
            new()
            {
                StencilKey = "test",
                ServiceLabel = "Test",
                ServiceName = "Test Service",
                UnitPolicy = policy,
                SkuNormalize = normalize,
                MeterHint = hint,
                DefaultSku = defaultSku,
                ExcludeProductContains = exclude ?? System.Array.Empty<string>(),
            };

        [Fact]
        public void HourlyMeter_ComputesMonthlyVia730Hours()
        {
            var result = CostEstimator.Estimate(
                Entry("hourly", normalize: "vm", exclude: new[] { "Windows", "Spot", "Low Priority" }),
                "Standard_D2s_v3",
                new[] { Meter("D2s v3", "D2s v3", "1 Hour", 0.096) });

            Assert.Equal(CostStatus.Computed, result.Status);
            Assert.Equal(70.08, result.Monthly);
        }

        [Fact]
        public void DailyMeter_ComputesMonthlyViaDays()
        {
            var result = CostEstimator.Estimate(
                Entry("daily", hint: "DTU"),
                "S0",
                new[] { Meter("S0", "S0 DTUs", "1/Day", 0.4839, "SQL Database Single Standard") });

            Assert.Equal(CostStatus.Computed, result.Status);
            Assert.Equal(14.73, result.Monthly);
        }

        [Fact]
        public void MeterHint_PrefersInstanceMeterOverTotal()
        {
            var result = CostEstimator.Estimate(
                Entry("hourly", hint: "Instance"),
                "P1",
                new[]
                {
                    Meter("P1", "P1 Cache", "1 Hour", 0.555, "Azure Redis Cache Premium"),
                    Meter("P1", "P1 Cache Instance", "1 Hour", 0.277, "Azure Redis Cache Premium"),
                });

            Assert.Equal(CostStatus.Computed, result.Status);
            Assert.Equal("P1 Cache Instance", result.MeterName);
            Assert.Equal(202.21, result.Monthly);
        }

        [Fact]
        public void UsageMeter_ReturnsUnitPriceNotMonthly()
        {
            var result = CostEstimator.Estimate(
                Entry("usage", hint: "Operations"),
                "Basic",
                new[] { Meter("Basic", "Basic Messaging Operations", "1M", 0.05) });

            Assert.Equal(CostStatus.UsageBased, result.Status);
            Assert.Equal(0.05, result.UnitPrice);
            Assert.Equal("1M", result.Unit);
            Assert.Null(result.Monthly);
        }

        [Fact]
        public void WindowsAndSpotMeters_ExcludedForLinuxVm()
        {
            var result = CostEstimator.Estimate(
                Entry("hourly", normalize: "vm", exclude: new[] { "Windows", "Spot", "Low Priority" }),
                "Standard_D2s_v3",
                new[]
                {
                    Meter("D2s v3", "D2s v3", "1 Hour", 0.15, "Virtual Machines D2s v3 Series Windows"),
                    Meter("D2s v3", "D2s v3 Spot", "1 Hour", 0.02, "Virtual Machines D2s v3 Series Spot"),
                });

            Assert.Equal(CostStatus.Unavailable, result.Status);
        }

        [Fact]
        public void AppServiceSku_MatchesDespiteSpaceDifference()
        {
            // Node form stores "P1V3"; the retail API skuName is "P1 v3".
            var result = CostEstimator.Estimate(
                Entry("hourly", hint: "App"),
                "P1V3",
                new[] { Meter("P1 v3", "P1 v3 App", "1 Hour", 0.155, "Azure App Service") });

            Assert.Equal(CostStatus.Computed, result.Status);
            Assert.Equal(113.15, result.Monthly);
        }

        [Fact]
        public void StorageReplication_MapsToHotMeterSku()
        {
            var result = CostEstimator.Estimate(
                Entry("usage", normalize: "storage", hint: "Data Stored", defaultSku: "Standard_LRS"),
                "Standard_LRS",
                new[] { Meter("Hot LRS", "Hot LRS Data Stored", "1 GB/Month", 0.0184, "Blob Storage") });

            Assert.Equal(CostStatus.UsageBased, result.Status);
            Assert.Equal(0.0184, result.UnitPrice);
        }

        [Fact]
        public void MissingNodeSku_FallsBackToDefaultSku()
        {
            var result = CostEstimator.Estimate(
                Entry("hourly", normalize: "vm", defaultSku: "Standard_D2s_v3"),
                null,
                new[] { Meter("D2s v3", "D2s v3", "1 Hour", 0.096) });

            Assert.Equal(CostStatus.Computed, result.Status);
            Assert.Equal("D2s v3", result.Sku);
        }

        [Fact]
        public void NoMatchingSku_IsUnavailable()
        {
            var result = CostEstimator.Estimate(
                Entry("hourly"),
                "P3v3",
                new[] { Meter("P1v3", "P1v3", "1 Hour", 0.146) });

            Assert.Equal(CostStatus.Unavailable, result.Status);
        }

        [Fact]
        public void FreePolicy_ReturnsFreeWithoutMeterLookup()
        {
            var result = CostEstimator.Estimate(
                Entry("free", hint: "billed through its App Service plan"),
                null,
                new List<RetailMeter>());

            Assert.Equal(CostStatus.Free, result.Status);
            Assert.Equal("billed through its App Service plan", result.Note);
        }

        [Fact]
        public void UnavailablePolicy_ReturnsUnavailable()
        {
            var result = CostEstimator.Estimate(Entry("unavailable"), "Basic", new List<RetailMeter>());
            Assert.Equal(CostStatus.Unavailable, result.Status);
        }

        [Fact]
        public void MultipleHourlyMeters_PicksMostExpensive()
        {
            var result = CostEstimator.Estimate(
                Entry("hourly"),
                "Standard",
                new[]
                {
                    Meter("Standard", "Standard Included Rules", "1 Hour", 0.025),
                    Meter("Standard", "Standard Data Processed", "1 GB", 0.005),
                });

            Assert.Equal(CostStatus.Computed, result.Status);
            Assert.Equal("Standard Included Rules", result.MeterName);
        }
    }
}
