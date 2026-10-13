using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.KeyVault;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.KeyVault
{
    [MapToDto(TypeKey = AdsConstants.KeyVault)]
    public class KeyVaultModel : AzureNodeBase
    {
        public KeyVaultModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Key Vault";
        private readonly Vaults _vault = new()
        {
            Sku = new()
            {
                Family = "A",
                Name = "standard"
            },
            Properties = new()
            {
                TenantId = "[subscription().tenantId]",
                EnableRbacAuthorization = true,
                AccessPolicies = new List<object>(),
            },
        };
        protected override ResourceBase ArmResource => _vault;
        public override Type? DataFormType => typeof(KeyVaultForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string Sku
        {
            get => _vault.Sku.Name;
            set => _vault.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("RBAC authorization")]
        public bool EnableRbacAuthorization
        {
            get => _vault.Properties.EnableRbacAuthorization;
            set => _vault.Properties.EnableRbacAuthorization = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Key Vault cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<KeyVaultModelDto>(this);
        }
    }
}
