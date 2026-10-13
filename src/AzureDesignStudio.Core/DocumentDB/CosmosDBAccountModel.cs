using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.DocumentDB;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.DocumentDB
{
    [MapToDto(TypeKey = AdsConstants.CosmosDBAccount)]
    public class CosmosDBAccountModel : AzureNodeBase
    {
        public CosmosDBAccountModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Azure Cosmos DB Account";
        private readonly DatabaseAccounts _account = new()
        {
            Kind = "GlobalDocumentDB",
            Properties = new()
            {
                ConsistencyPolicy = new() { DefaultConsistencyLevel = "Session" },
                EnableMultipleWriteLocations = false
            }
        };
        protected override ResourceBase ArmResource => _account;
        public override Type? DataFormType => typeof(CosmosDBAccountForm);
        [MapToDto]
        [DisplayName("Kind")]
        public string Kind { get => _account.Kind; set => _account.Kind = value; }
        [MapToDto]
        [DisplayName("Default Consistency Level")]
        public string DefaultConsistencyLevel
        {
            get => _account.Properties.ConsistencyPolicy.DefaultConsistencyLevel;
            set => _account.Properties.ConsistencyPolicy.DefaultConsistencyLevel = value;
        }
        [MapToDto]
        [DisplayName("Multiple Write Locations")]
        public bool EnableMultipleWriteLocations
        {
            get => _account.Properties.EnableMultipleWriteLocations;
            set => _account.Properties.EnableMultipleWriteLocations = value;
        }
        public override bool IsValid => Regex.IsMatch(Name, @"^[a-z0-9][a-z0-9-]{2,43}[a-z0-9]$");
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Azure Cosmos DB Account cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper) => mapper.Map<CosmosDBAccountModelDto>(this);
    }
}
