using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Search;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Search
{
    [MapToDto(TypeKey = AdsConstants.AiSearch)]
    public class AiSearchModel : AzureNodeBase
    {
        public AiSearchModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Azure AI Search";
        private readonly SearchServices _searchService = new()
        {
            Sku = new() { Name = "basic" },
            Properties = new()
            {
                ReplicaCount = 1,
                PartitionCount = 1
            }
        };
        protected override ResourceBase ArmResource => _searchService;
        public override Type? DataFormType => typeof(AiSearchForm);
        [MapToDto]
        [DisplayName("SKU Name")]
        public string SkuName { get => _searchService.Sku.Name; set => _searchService.Sku.Name = value; }
        [MapToDto]
        [DisplayName("Replica Count")]
        public int ReplicaCount { get => _searchService.Properties.ReplicaCount; set => _searchService.Properties.ReplicaCount = value; }
        [MapToDto]
        [DisplayName("Partition Count")]
        public int PartitionCount { get => _searchService.Properties.PartitionCount; set => _searchService.Properties.PartitionCount = value; }
        public override bool IsValid => Regex.IsMatch(Name, @"^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$");
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Azure AI Search cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper) => mapper.Map<AiSearchModelDto>(this);
    }
}
