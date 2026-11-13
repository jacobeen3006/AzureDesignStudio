using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Compute;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Compute
{
    [MapToDto(TypeKey = AdsConstants.VirtualMachineScaleSet)]
    public class VirtualMachineScaleSetModel : AzureNodeBase
    {
        public VirtualMachineScaleSetModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "VM Scale Set";
        private readonly VirtualMachineScaleSets _scaleSet = new()
        {
            Sku = new()
            {
                Name = VmImages.DefaultSize,
                Capacity = 2,
            },
            Properties = new()
            {
                Overprovision = true,
            },
        };
        protected override ResourceBase ArmResource => _scaleSet;
        public override Type? DataFormType => typeof(VirtualMachineScaleSetForm);
        [MapToDto]
        [DisplayName("SKU name")]
        public string SkuName
        {
            get => _scaleSet.Sku.Name;
            set => _scaleSet.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("Capacity")]
        public int Capacity
        {
            get => _scaleSet.Sku.Capacity;
            set => _scaleSet.Sku.Capacity = value;
        }
        [MapToDto]
        [DisplayName("Overprovision")]
        public bool Overprovision
        {
            get => _scaleSet.Properties.Overprovision;
            set => _scaleSet.Properties.Overprovision = value;
        }
        private string _osImage = VmImages.Default;
        private string _adminUsername = VmImages.DefaultAdminUsername;

        [MapToDto, DisplayName("Image")]
        public string OsImage
        {
            get => _osImage;
            set => _osImage = string.IsNullOrWhiteSpace(value) ? _osImage : value.Trim();
        }

        [MapToDto, DisplayName("Admin user")]
        public string AdminUsername
        {
            get => _adminUsername;
            set => _adminUsername = string.IsNullOrWhiteSpace(value) ? _adminUsername : value.Trim();
        }

        /// <summary>The subnet every instance's network interface joins.</summary>
        public Network.SubnetModel? Subnet => Group as Network.SubnetModel;

        public override bool IsValid =>
            Regex.IsMatch(Name, @"^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])?$")
            && Subnet is not null && VmImages.IsValidAdminUsername(AdminUsername);

        // Instances need a network, so the one frame a scale set belongs in is a subnet. It
        // used to be refused everywhere, and exported with no machine profile at all.
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup is Network.SubnetModel)
                return (true, string.Empty);
            if (overlappedGroup != null)
                return (false, $"VM Scale Set cannot be in {((IAzureNode)overlappedGroup).ServiceName}; place it in a subnet.");

            return (true, string.Empty);
        }

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            var subnet = Subnet ?? throw new Exception($"{ServiceName} '{Name}' must be placed in a subnet.");
            var image = VmImages.Resolve(OsImage);

            _scaleSet.Properties.UpgradePolicy = new Dictionary<string, object> { ["mode"] = "Manual" };
            _scaleSet.Properties.VirtualMachineProfile = new Dictionary<string, object>
            {
                ["osProfile"] = new Dictionary<string, object>
                {
                    // Azure appends an instance suffix of up to six characters.
                    ["computerNamePrefix"] = VmImages.ComputerName(Name, image.IsWindows, reserve: 6),
                    ["adminUsername"] = AdminUsername,
                    ["adminPassword"] = $"[parameters('{VmImages.AdminPasswordParameter}')]",
                },
                ["storageProfile"] = new Dictionary<string, object>
                {
                    ["imageReference"] = new Dictionary<string, object>
                    {
                        ["publisher"] = image.Publisher,
                        ["offer"] = image.Offer,
                        ["sku"] = image.Sku,
                        ["version"] = "latest",
                    },
                    ["osDisk"] = new Dictionary<string, object>
                    {
                        ["createOption"] = "FromImage",
                        ["managedDisk"] = new Dictionary<string, object> { ["storageAccountType"] = "StandardSSD_LRS" },
                    },
                },
                ["networkProfile"] = new Dictionary<string, object>
                {
                    ["networkInterfaceConfigurations"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = Name + "-nic",
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["primary"] = true,
                                ["enableAcceleratedNetworking"] = VmImages.UsesAcceleratedNetworking(SkuName),
                                ["ipConfigurations"] = new object[]
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["name"] = "ipconfig1",
                                        ["properties"] = new Dictionary<string, object>
                                        {
                                            ["subnet"] = new Dictionary<string, object> { ["id"] = subnet.ResourceId },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };
            _scaleSet.DependsOn = [subnet.ResourceId];
        }

        public override IDictionary<string, Parameter> GetArmParameters() => VmImages.AdminPasswordParameters();
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<VirtualMachineScaleSetModelDto>(this);
        }
    }
}
