using AzureDesignStudio.Core.Cost;
using AzureDesignStudio.SharedModels.Protos;
using Grpc.Net.ClientFactory;

namespace AzureDesignStudio.Services
{
    /// <summary>Thin wrapper over the Cost gRPC client (server-side proxy for the Azure Retail Prices API).</summary>
    public class CostGrpcService
    {
        private readonly Cost.CostClient _costClient = null!;
        private readonly GrpcClientFactory _clientFactory = null!;

        public CostGrpcService(GrpcClientFactory grpcClientFactory)
        {
            _clientFactory = grpcClientFactory;
            _costClient = _clientFactory.CreateClient<Cost.CostClient>("CostClient");
        }

        public async Task<IReadOnlyList<RetailMeter>> GetRetailPrices(string serviceName, string region, string skuName, string currency)
        {
            var request = new RetailPriceRequest
            {
                ServiceName = serviceName,
                ArmRegionName = region,
                SkuName = skuName,
                CurrencyCode = currency,
            };
            var response = await _costClient.GetRetailPricesAsync(request);
            if (response.StatusCode != 200)
                throw new HttpRequestException($"Retail prices proxy returned status {response.StatusCode}.");
            return response.Prices
                .Select(p => new RetailMeter(p.ServiceName, p.SkuName, p.MeterName, p.ProductName, p.UnitOfMeasure, p.RetailPrice, p.CurrencyCode))
                .ToList();
        }
    }
}
