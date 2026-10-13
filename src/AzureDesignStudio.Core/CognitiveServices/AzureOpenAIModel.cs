using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.CognitiveServices;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.CognitiveServices
{
    [MapToDto(TypeKey = AdsConstants.AzureOpenAI)]
    public class AzureOpenAIModel : AzureNodeBase
    {
        public AzureOpenAIModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Azure OpenAI";
        private readonly CognitiveServicesAccounts _account = new()
        {
            Kind = "OpenAI",
            Sku = new()
            {
                Name = "S0",
                Tier = "Standard"
            },
            Properties = new()
            {
                PublicNetworkAccess = "Enabled",
            },
        };
        protected override ResourceBase ArmResource => _account;
        public override Type? DataFormType => typeof(AzureOpenAIForm);
        [MapToDto]
        [DisplayName("Kind")]
        public string Kind
        {
            get => _account.Kind;
            set => _account.Kind = value;
        }
        [MapToDto]
        [DisplayName("Subdomain")]
        public string CustomSubDomainName
        {
            get => _account.Properties.CustomSubDomainName;
            set => _account.Properties.CustomSubDomainName = value;
        }
        [MapToDto]
        [DisplayName("Network access")]
        public string PublicNetworkAccess
        {
            get => _account.Properties.PublicNetworkAccess;
            set => _account.Properties.PublicNetworkAccess = value;
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Azure OpenAI cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<AzureOpenAIModelDto>(this);
        }
        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            // The subdomain is globally unique and defaults to the resource name.
            if (string.IsNullOrEmpty(_account.Properties.CustomSubDomainName))
            {
                _account.Properties.CustomSubDomainName = Name;
            }
        }
    }
}
