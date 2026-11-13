using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Storage;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Storage
{
    [MapToDto(TypeKey = AdsConstants.StorageAccount)]
    public class StorageAccountModel : AzureNodeBase
    {
        public StorageAccountModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Storage Account";
        public override Type? DataFormType => typeof(StorageAccountForm);
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<StorageAccountModelDto>(this);
        }
        // sku.name and kind are both required by Microsoft.Storage/storageAccounts. They used
        // to start empty with no way to set them (the form was a stub), so a storage account
        // dropped on the canvas could never be deployed.
        private readonly StorageAccounts _storageAccount = new()
        {
            Sku = new() { Name = "Standard_LRS" },
            Kind = "StorageV2",
            Properties = new()
            {
                SupportsHttpsTrafficOnly = true,
                MinimumTlsVersion = "TLS1_2",
            }
        };
        protected override ResourceBase ArmResource => _storageAccount;
        [Required, DisplayName("Sku"), MapToDto]
        public string SkuName
        {
            get => _storageAccount.Sku.Name;
            set
            {
                // Designs saved before the defaults existed carry no value; keep the default.
                if (!string.IsNullOrEmpty(value))
                    _storageAccount.Sku.Name = value;
            }
        }
        [Required, DisplayName("Kind"), MapToDto]
        public string Kind
        {
            get => _storageAccount.Kind;
            set
            {
                if (!string.IsNullOrEmpty(value))
                    _storageAccount.Kind = value;
            }
        }
        /// <summary>BlockBlobStorage and FileStorage are premium-only account kinds.</summary>
        public override bool IsValid =>
            Kind is not ("BlockBlobStorage" or "FileStorage") || SkuName.StartsWith("Premium", StringComparison.Ordinal);

        public string AccountKey
        {
            get
            {
                Regex regex = new(@"[^\[^\]]+"); // Remove '[' and ']' from the resource id. 
                Match match = regex.Match(ResourceId);
                return $"listKeys({match.Value}, '{_storageAccount.ApiVersion}').keys[0].value";
            }
        }
    }
}
