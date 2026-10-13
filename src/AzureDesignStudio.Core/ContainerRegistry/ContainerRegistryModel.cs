using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.ContainerRegistry;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.ContainerRegistry
{
    [MapToDto(TypeKey = AdsConstants.ContainerRegistry)]
    public class ContainerRegistryModel : AzureNodeBase
    {
        public ContainerRegistryModel() : base()
        {
            AddPort(PortAlignment.Left);
            AddPort(PortAlignment.Top);
            AddPort(PortAlignment.Right);
            AddPort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Container Registry";
        public override Type? DataFormType => typeof(ContainerRegistryForm);
        private readonly Registries _registry = new()
        {
            Sku = new()
            {
                Name = "Basic",
            },
            Properties = new()
            {
                AdminUserEnabled = false,
            },
        };
        protected override ResourceBase ArmResource => _registry;
        // Registry names must be 5-50 alphanumeric characters.
        public override bool IsValid => Regex.IsMatch(Name, "^[a-zA-Z0-9]{5,50}$");
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _registry.Sku.Name;
            set => _registry.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("Admin user")]
        public bool AdminUserEnabled
        {
            get => _registry.Properties.AdminUserEnabled;
            set => _registry.Properties.AdminUserEnabled = value;
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ContainerRegistryModelDto>(this);
        }
    }
}
