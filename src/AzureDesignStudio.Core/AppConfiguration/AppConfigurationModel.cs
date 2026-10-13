using AutoMapper;
using AzureDesignStudio.AzureResources.AppConfiguration;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.AppConfiguration
{
    [MapToDto(TypeKey = AdsConstants.AppConfiguration)]
    public class AppConfigurationModel : AzureNodeBase
    {
        public AppConfigurationModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "App Configuration";
        private readonly ConfigurationStores _configurationStore = new()
        {
            Sku = new()
            {
                Name = "Free",
            },
            Properties = new()
            {
                DisableLocalAuth = false,
            },
        };
        protected override ResourceBase ArmResource => _configurationStore;
        public override Type? DataFormType => typeof(AppConfigurationForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _configurationStore.Sku.Name;
            set => _configurationStore.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("Disable local authentication")]
        public bool DisableLocalAuth
        {
            get => _configurationStore.Properties.DisableLocalAuth;
            set => _configurationStore.Properties.DisableLocalAuth = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z0-9_-]{5,64}$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"App Configuration cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<AppConfigurationModelDto>(this);
        }
    }
}
