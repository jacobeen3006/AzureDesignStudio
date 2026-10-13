using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Web;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Web
{
    [MapToDto(TypeKey = AdsConstants.StaticWebApp)]
    public class StaticWebAppModel : AzureNodeBase
    {
        public StaticWebAppModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Static Web App";
        private readonly StaticSites _staticSite = new()
        {
            Sku = new()
            {
                Name = "Free",
            },
            Properties = new()
            {
                SkuSizeCap = "100",
            },
        };
        protected override ResourceBase ArmResource => _staticSite;
        public override Type? DataFormType => typeof(StaticWebAppForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _staticSite.Sku.Name;
            set => _staticSite.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("SKU size cap (GB)")]
        public string SkuSizeCap
        {
            get => _staticSite.Properties.SkuSizeCap;
            set => _staticSite.Properties.SkuSizeCap = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z][a-zA-Z0-9-]{0,99}$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Static Web App cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<StaticWebAppModelDto>(this);
        }
    }
}
