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
    [MapToDto(TypeKey = AdsConstants.DnsZone)]
    public class DnsZoneModel : AzureNodeBase
    {
        public DnsZoneModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "DNS Zone";
        private readonly DnsZones _dnsZone = new()
        {
            Properties = new()
            {
                MaxNumberOfRecordSets = 5000,
            },
        };
        protected override ResourceBase ArmResource => _dnsZone;
        public override Type? DataFormType => typeof(DnsZoneForm);

        // DNS zones are global resources: no location is deployed.
        public override bool UseResourceGroupLocation { get => false; set { } }
        public override string Location { get => string.Empty; set { } }

        // The zone name must be a valid domain name, e.g. contoso.com.
        private static readonly Regex DomainNameRegex =
            new(@"^[a-zA-Z0-9][a-zA-Z0-9-\.]*\.[a-zA-Z]{2,}$", RegexOptions.Compiled);
        public override bool IsValid => DomainNameRegex.IsMatch(Name);

        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<DnsZoneModelDto>(this);
        }
    }
}
