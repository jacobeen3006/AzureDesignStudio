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
            Sku = new()
            {
                Name = "Standard50Mbps",
                Tier = "Standard",
            },
            Properties = new()
            {
                ServiceProvider = "Equinix",
            },
        };
        protected override ResourceBase ArmResource => _expressRouteCircuit;
        public override Type? DataFormType => typeof(ExpressRouteCircuitForm);

        [MapToDto]
        [DisplayName("Service provider")]
        public string ServiceProvider
        {
            get => _expressRouteCircuit.Properties.ServiceProvider;
            set => _expressRouteCircuit.Properties.ServiceProvider = value;
        }

        [MapToDto]
        [DisplayName("SKU name")]
        public string SkuName
        {
            get => _expressRouteCircuit.Sku.Name;
            set => _expressRouteCircuit.Sku.Name = value;
        }

        [MapToDto]
        [DisplayName("SKU tier")]
        public string SkuTier
        {
            get => _expressRouteCircuit.Sku.Tier;
            set => _expressRouteCircuit.Sku.Tier = value;
        }

        public override bool IsValid => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-_.]{0,78}[a-zA-Z0-9_]$");

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"ExpressRoute Circuit cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ExpressRouteCircuitModelDto>(this);
        }
    }
}
