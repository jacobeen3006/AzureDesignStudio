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
    private PublicIpModel? PublicIp => LinkedNodes.OfType<PublicIpModel>().FirstOrDefault();
    private SubnetModel? Subnet => Group as SubnetModel;

    /// <summary>A gateway is built on a subnet of its own and answers on a public address.
    /// The link to the address may have been drawn from either end.</summary>
    public override bool IsValid => PublicIp is not null && Subnet is not null;
    public override bool DataFormNoPadding => true;

    private readonly ApplicationGateways _appGateway = new()
    {
        Properties = new()
        {
            Sku = new()
            {
                Name = "Standard_v2",
                Tier = "Standard_v2",
            },
        },
    };
    protected override ResourceBase ArmResource => _appGateway;

    private int _instanceCount = 2;
    private int _minCapacity;
    private int _maxCapacity = 10;

    [Required, DisplayName("Tier"), MapToDto]
    public string Tier
    {
        get => _appGateway.Properties.Sku.Tier;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            _appGateway.Properties.Sku.Tier = value;
            _appGateway.Properties.Sku.Name = value;
        }
    }
    [DisplayName("Autoscaling"), MapToDto]
    public bool Autoscaling { get; set; } = false;
    [DisplayName("Instance"), MapToDto]
    public int InstanceCount
    {
        get => _instanceCount;
        set => _instanceCount = value;
    }
    [DisplayName("Min Instance"), MapToDto]
    public int MinCapacity
    {
        get => _minCapacity;
        set => _minCapacity = value;
    }
    [DisplayName("Max Instance"), MapToDto]
    public int MaxCapacity
    {
        get => _maxCapacity;
        set => _maxCapacity = value;
    }
    [MapToDto]
    public string BackendPoolName { get; set; } = null!;
    [MapToDto]
    public string BackendAddressType { get; set; } = "fqdn";
    [MapToDto]
    public string BackendAddress { get; set; } = null!;

    private const string GatewayIpConfigName = "gateway-ip";
    private const string FrontendName = "public-frontend";
    private const string FrontendPortName = "port-80";
    private const string HttpSettingsName = "http-settings";
    private const string ListenerName = "http-listener";
    private const string RuleName = "routing-rule";

    private string ChildId(string kind, string name) =>
        $"[resourceId('Microsoft.Network/applicationGateways/{kind}', '{Name}', '{name}')]";

    /// <summary>
    /// The export used to stop at the SKU, and wrote both a fixed capacity and an autoscale
    /// range - which Azure refuses outright. A gateway is seven collections that refer to one
    /// another; this writes the smallest set that forwards HTTP on port 80 to the backend pool.
    /// </summary>
    protected override void PopulateArmAttributes()
    {
        base.PopulateArmAttributes();

        var subnet = Subnet ?? throw new Exception($"{ServiceName} '{Name}' must be placed in a subnet.");
        var address = PublicIp ?? throw new Exception($"{ServiceName} '{Name}' needs a public IP linked to it.");
        var properties = _appGateway.Properties;

        // One or the other, never both.
        if (Autoscaling)
        {
            var min = Math.Clamp(_minCapacity, 0, 125);
            properties.Sku.Capacity = 0;
            properties.AutoscaleConfiguration = new ApplicationGatewayAutoscaleConfiguration
            {
                MinCapacity = min,
                MaxCapacity = Math.Clamp(Math.Max(_maxCapacity, Math.Max(min, 2)), 2, 125),
            };
        }
        else
        {
            properties.Sku.Capacity = Math.Clamp(_instanceCount, 1, 125);
            properties.AutoscaleConfiguration = null;
        }

        // The WAF tier is refused without a firewall configuration.
        properties.WebApplicationFirewallConfiguration = Tier == "WAF_v2"
            ? new ApplicationGatewayWebApplicationFirewallConfiguration
            {
                Enabled = true,
                FirewallMode = "Detection",
                RuleSetType = "OWASP",
                RuleSetVersion = "3.2",
            }
            : null;

        var poolName = string.IsNullOrWhiteSpace(BackendPoolName) ? "backend-pool" : BackendPoolName.Trim();
        var backends = (BackendAddress ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => BackendAddressType == "ip"
                ? new ApplicationGatewayBackendAddress { IpAddress = a }
                : new ApplicationGatewayBackendAddress { Fqdn = a })
            .ToList();

        properties.GatewayIPConfigurations =
        [
            new ApplicationGatewayIPConfiguration
            {
                Name = GatewayIpConfigName,
                Properties = new() { Subnet = new SubResource { Id = subnet.ResourceId } },
            },
        ];
        properties.FrontendIPConfigurations =
        [
            new ApplicationGatewayFrontendIPConfiguration
            {
                Name = FrontendName,
                Properties = new() { PublicIPAddress = new SubResource { Id = address.ResourceId } },
            },
        ];
        properties.FrontendPorts =
        [
            new ApplicationGatewayFrontendPort { Name = FrontendPortName, Properties = new() { Port = 80 } },
        ];
        properties.BackendAddressPools =
        [
            new ApplicationGatewayBackendAddressPool { Name = poolName, Properties = new() { BackendAddresses = backends } },
        ];
        properties.BackendHttpSettingsCollection =
        [
            new ApplicationGatewayBackendHttpSettings
            {
                Name = HttpSettingsName,
                Properties = new() { Port = 80, Protocol = "Http", CookieBasedAffinity = "Disabled", RequestTimeout = 20 },
            },
        ];
        properties.HttpListeners =
        [
            new ApplicationGatewayHttpListener
            {
                Name = ListenerName,
                Properties = new()
                {
                    FrontendIPConfiguration = new SubResource { Id = ChildId("frontendIPConfigurations", FrontendName) },
                    FrontendPort = new SubResource { Id = ChildId("frontendPorts", FrontendPortName) },
                    Protocol = "Http",
                },
            },
        ];
        properties.RequestRoutingRules =
        [
            new ApplicationGatewayRequestRoutingRule
            {
                Name = RuleName,
                Properties = new()
                {
                    RuleType = "Basic",
                    Priority = 100,
                    HttpListener = new SubResource { Id = ChildId("httpListeners", ListenerName) },
                    BackendAddressPool = new SubResource { Id = ChildId("backendAddressPools", poolName) },
                    BackendHttpSettings = new SubResource { Id = ChildId("backendHttpSettingsCollection", HttpSettingsName) },
                },
            },
        ];

        _appGateway.DependsOn = [subnet.ResourceId, address.ResourceId];
    }
}
