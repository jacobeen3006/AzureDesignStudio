using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.RecoveryServices;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.RecoveryServices
{
    [MapToDto(TypeKey = AdsConstants.RecoveryServicesVault)]
    public class RecoveryServicesVaultModel : AzureNodeBase
    {
        public RecoveryServicesVaultModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Recovery Services Vault";
        private readonly Vaults _vault = new()
        {
            Properties = new()
            {
                SkuName = "RS0",
                Redundancy = "GRS",
            },
        };
        protected override ResourceBase ArmResource => _vault;
        public override Type? DataFormType => typeof(RecoveryServicesVaultForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string SkuName
        {
            get => _vault.Properties.SkuName;
            set => _vault.Properties.SkuName = value;
        }
        [MapToDto]
        [DisplayName("Redundancy")]
        public string Redundancy
        {
            get => _vault.Properties.Redundancy;
            set => _vault.Properties.Redundancy = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Recovery Services Vault cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<RecoveryServicesVaultModelDto>(this);
        }
    }
}
