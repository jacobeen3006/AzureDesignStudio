using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Network
{
    [MapToDto(TypeKey = AdsConstants.ExpressRouteCircuit)]
    public class ExpressRouteCircuitModel : AzureNodeBase
    {
        public static readonly IReadOnlyList<int> Bandwidths = [50, 100, 200, 500, 1000, 2000, 5000, 10000];

        public ExpressRouteCircuitModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "ExpressRoute Circuit";

        private readonly ExpressRouteCircuits _expressRouteCircuit = new()
        {
            Sku = new(),
            Properties = new(),
        };
        protected override ResourceBase ArmResource => _expressRouteCircuit;
        public override Type? DataFormType => typeof(ExpressRouteCircuitForm);

        private string _serviceProvider = "Equinix";
        private string _peeringLocation = "Amsterdam";
        private string _skuTier = "Standard";
        private string _skuFamily = "MeteredData";
        private int _bandwidthInMbps = 50;

        // Empty or unknown values keep the current one, so a design saved before these fields
        // existed - or with the old made-up "Standard50Mbps" SKU name - loads with defaults.
        [MapToDto]
        [DisplayName("Service provider")]
        public string ServiceProvider
        {
            get => _serviceProvider;
            set => _serviceProvider = string.IsNullOrWhiteSpace(value) ? _serviceProvider : value.Trim();
        }

        [MapToDto]
        [DisplayName("Peering location")]
        public string PeeringLocation
        {
            get => _peeringLocation;
            set => _peeringLocation = string.IsNullOrWhiteSpace(value) ? _peeringLocation : value.Trim();
        }

        [MapToDto]
        [DisplayName("SKU tier")]
        public string SkuTier
        {
            get => _skuTier;
            set => _skuTier = value is "Local" or "Standard" or "Premium" ? value : _skuTier;
        }

        [MapToDto]
        [DisplayName("Billing")]
        public string SkuFamily
        {
            get => _skuFamily;
            set => _skuFamily = value is "MeteredData" or "UnlimitedData" ? value : _skuFamily;
        }

        [MapToDto]
        [DisplayName("Bandwidth (Mbps)")]
        public int BandwidthInMbps
        {
            get => _bandwidthInMbps;
            set => _bandwidthInMbps = value > 0 ? value : _bandwidthInMbps;
        }

        /// <summary>The SKU name Azure expects: tier and billing model joined, as in
        /// <c>Standard_MeteredData</c>. The bandwidth is not part of it.</summary>
        public string SkuName => $"{SkuTier}_{SkuFamily}";

        public override bool IsValid => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-_.]{0,78}[a-zA-Z0-9_]$");

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"ExpressRoute Circuit cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            _expressRouteCircuit.Sku.Name = SkuName;
            _expressRouteCircuit.Sku.Tier = SkuTier;
            _expressRouteCircuit.Sku.Family = SkuFamily;
            _expressRouteCircuit.Properties.ServiceProviderProperties = new ExpressRouteCircuitsServiceProviderProperties
            {
                ServiceProviderName = ServiceProvider,
                PeeringLocation = PeeringLocation,
                BandwidthInMbps = BandwidthInMbps,
            };
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ExpressRouteCircuitModelDto>(this);
        }
    }
}
