using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.SignalRService;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.SignalRService
{
    [MapToDto(TypeKey = AdsConstants.SignalR)]
    public class SignalRModel : AzureNodeBase
    {
        public SignalRModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "SignalR";
        private readonly SignalR _service = new()
        {
            Sku = new() { Name = "Free_F1", Capacity = 1 },
            Properties = new()
            {
                Features = new()
                {
                    new SignalRFeature { Name = "ServiceMode", Value = "Default" }
                }
            }
        };
        protected override ResourceBase ArmResource => _service;
        public override Type? DataFormType => typeof(SignalRForm);
        [MapToDto]
        [DisplayName("SKU Name")]
        public string SkuName { get => _service.Sku.Name; set => _service.Sku.Name = value; }
        [MapToDto]
        [DisplayName("SKU Capacity")]
        public int SkuCapacity { get => _service.Sku.Capacity; set => _service.Sku.Capacity = value; }
        [MapToDto]
        [DisplayName("Service Mode")]
        public string ServiceMode
        {
            get => _service.Properties.Features.FirstOrDefault(f => f.Name == "ServiceMode")?.Value ?? "Default";
            set
            {
                var feature = _service.Properties.Features.FirstOrDefault(f => f.Name == "ServiceMode");
                if (feature is null)
                {
                    _service.Properties.Features.Add(new SignalRFeature { Name = "ServiceMode", Value = value });
                }
                else
                {
                    feature.Value = value;
                }
            }
        }
        public override bool IsValid => Regex.IsMatch(Name, @"^[a-zA-Z][a-zA-Z0-9-]{1,61}[a-zA-Z0-9]$");
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"SignalR cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper) => mapper.Map<SignalRModelDto>(this);
    }
}
