using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.DataFactory;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.DataFactory
{
    [MapToDto(TypeKey = AdsConstants.DataFactory)]
    public class DataFactoryModel : AzureNodeBase
    {
        public DataFactoryModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Data Factory";
        private readonly Factories _factory = new()
        {
            Properties = new()
            {
                PublicDataEndpoint = "https://adftel.azureedge.net"
            }
        };
        protected override ResourceBase ArmResource => _factory;
        public override Type? DataFormType => typeof(DataFactoryForm);
        [MapToDto]
        [DisplayName("Public Data Set URI")]
        public string PublicDataEndpoint
        {
            get => _factory.Properties.PublicDataEndpoint;
            set => _factory.Properties.PublicDataEndpoint = value;
        }
        [MapToDto]
        [DisplayName("Version")]
        public string Version => "V2";
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Data Factory cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper) => mapper.Map<DataFactoryModelDto>(this);
    }
}
