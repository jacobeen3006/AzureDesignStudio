using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Network
{
    [MapToDto(TypeKey = AdsConstants.NetworkSecurityGroup)]
    public class NetworkSecurityGroupModel : AzureNodeBase
    {
        public NetworkSecurityGroupModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Network Security Group";
        private readonly NetworkSecurityGroups _networkSecurityGroup = new()
        {
            Properties = new()
            {
                SecurityRules = new List<object>(),
            },
        };
        protected override ResourceBase ArmResource => _networkSecurityGroup;
        public override Type? DataFormType => typeof(NetworkSecurityGroupForm);

        public override bool IsValid => Regex.IsMatch(Name, @"^[a-zA-Z0-9][a-zA-Z0-9-_.]{0,78}[a-zA-Z0-9_]$");

        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Network Security Group cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");
            return (true, string.Empty);
        }

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<NetworkSecurityGroupModelDto>(this);
        }
    }
}
