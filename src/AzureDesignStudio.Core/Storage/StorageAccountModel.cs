﻿using AutoMapper;
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
        private readonly StorageAccounts _storageAccount = new()
        {
            Sku = new(),
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
            set => _storageAccount.Sku.Name = value;
        }
        [Required, DisplayName("Kind"), MapToDto]
        public string Kind
        {
            get => _storageAccount.Kind;
            set => _storageAccount.Kind = value;
        }
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
