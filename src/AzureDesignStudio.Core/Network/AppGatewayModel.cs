using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AzureDesignStudio.Core.Network;

[MapToDto(TypeKey = AdsConstants.AppGateway)]
public class AppGatewayModel : AzureNodeBase
{
    public AppGatewayModel() : base()
    {
        AddAzurePort(PortAlignment.Left);
        AddAzurePort(PortAlignment.Top);
        AddAzurePort(PortAlignment.Right);
        AddAzurePort(PortAlignment.Bottom);
    }

    public override string ServiceName => "Application Gateway";
    public override Type? DataFormType => typeof(AppGatewayForm);
    public override AzureNodeDto GetNodeDto(IMapper mapper)
    {
        return mapper.Map<AppGatewayModelDto>(this);
    }
    public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
    {
        if (overlappedGroup is not SubnetModel)
            return (false, $"{ServiceName} must be deployed in a subnet.");

        return base.IsDrappable(overlappedGroup);
    }
    private List<PublicIpModel> Pips
    {
        get
        {
            var pips = new List<PublicIpModel>();
            foreach (var port in Ports)
            {
                foreach (var link in port.Links)
                {
                    if (link.Source.Model is PortModel sp && sp.Parent is PublicIpModel pip
                        && !pips.Exists(p => p.ResourceId == pip.ResourceId))
                        pips.Add(pip);
                }
            }
            return pips;
        }
    }
    public override bool IsValid
    {
        get => Pips.Count > 0;
    }
    public override bool DataFormNoPadding => true;

    private readonly ApplicationGateways _appGateway = new()
    {
        Properties = new()
        {
            Sku = new()
            {
                Name = "Standard_v2",
                Tier = "Standard_v2",
                Capacity = 2
            },
            AutoscaleConfiguration = new()
            {
                MinCapacity = 0,
                MaxCapacity = 10
            }
        },
    };
    protected override ResourceBase ArmResource => _appGateway;
    [Required, DisplayName("Tier"), MapToDto]
    public string Tier
    {
        get => _appGateway.Properties.Sku.Tier;
        set
        {
            _appGateway.Properties.Sku.Tier = value;
            _appGateway.Properties.Sku.Name = value;
        }
    }
    [DisplayName("Autoscaling"), MapToDto]
    public bool Autoscaling { get; set; } = false;
    [DisplayName("Instance"), MapToDto]
    public int InstanceCount
    {
        get => _appGateway.Properties.Sku.Capacity;
        set => _appGateway.Properties.Sku.Capacity = value;
    }
    [DisplayName("Min Instance"), MapToDto]
    public int MinCapacity
    {
        get => _appGateway.Properties.AutoscaleConfiguration.MinCapacity;
        set => _appGateway.Properties.AutoscaleConfiguration.MinCapacity = value;
    }
    [DisplayName("Max Instance"), MapToDto]
    public int MaxCapacity
    {
        get => _appGateway.Properties.AutoscaleConfiguration.MaxCapacity;
        set => _appGateway.Properties.AutoscaleConfiguration.MaxCapacity = value;
    }
    [MapToDto]
    public string BackendPoolName { get; set; } = null!;
    [MapToDto]
    public string BackendAddressType { get; set; } = "fqdn";
    [MapToDto]
    public string BackendAddress { get; set; } = null!;
}
