using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Compute;
using AzureDesignStudio.AzureResources.Network;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;

namespace AzureDesignStudio.Core.Compute
{
    [MapToDto(TypeKey = AdsConstants.VirtualMachine)]
    public class VirtualMachineModel : AzureNodeBase
    {
        public VirtualMachineModel()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }
        public override string ServiceName => "Virtual Machine";
        public override Type? DataFormType => typeof(VirtualMachineForm);
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<VirtualMachineModelDto>(this);
        }

        private readonly VirtualMachines _vm = new();
        private readonly NetworkInterfaces _nic = new();
        protected override ResourceBase ArmResource => _vm;

        private string _vmSize = VmImages.DefaultSize;
        private string _osImage = VmImages.Default;
        private string _adminUsername = VmImages.DefaultAdminUsername;

        // Setters ignore an empty value: a design saved before these existed has none, and
        // must load with the defaults rather than with blanks.
        [MapToDto, DisplayName("Size")]
        public string VmSize
        {
            get => _vmSize;
            set => _vmSize = string.IsNullOrWhiteSpace(value) ? _vmSize : value.Trim();
        }

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

        /// <summary>The subnet the machine's network interface joins: the one it sits in, or
        /// the one its availability set sits in.</summary>
        public SubnetModel? Subnet => Group as SubnetModel ?? (Group as AvailabilitySetModel)?.Group as SubnetModel;

        /// <summary>A machine with no network cannot be created, so one outside a subnet is
        /// not exportable. It used to export as a name and nothing else.</summary>
        public override bool IsValid => Subnet is not null && VmImages.IsValidAdminUsername(AdminUsername);

        private string NicName => Name + "-nic";
        private string NicId => $"[resourceId('{_nic.Type}', '{NicName}')]";

        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            var subnet = Subnet ?? throw new Exception($"{ServiceName} '{Name}' must be placed in a subnet.");
            var image = VmImages.Resolve(OsImage);

            _nic.Name = NicName;
            _nic.Location = Location;
            _nic.Properties = new NetworkInterfacePropertiesFormat
            {
                EnableAcceleratedNetworking = VmImages.UsesAcceleratedNetworking(VmSize),
                IpConfigurations =
                [
                    new NetworkInterfaceIPConfiguration
                    {
                        Name = "ipconfig1",
                        Properties = new NetworkInterfaceIPConfigurationPropertiesFormat
                        {
                            PrivateIPAllocationMethod = "Dynamic",
                            Subnet = new Subnet { Id = subnet.ResourceId },
                        },
                    },
                ],
            };
            _nic.DependsOn = [subnet.ResourceId];

            _vm.Properties = new VirtualMachineProperties
            {
                HardwareProfile = new HardwareProfile { VmSize = VmSize },
                StorageProfile = new StorageProfile
                {
                    ImageReference = new ImageReference
                    {
                        Publisher = image.Publisher,
                        Offer = image.Offer,
                        Sku = image.Sku,
                        Version = "latest",
                    },
                    OsDisk = new OSDisk
                    {
                        CreateOption = "FromImage",
                        ManagedDisk = new ManagedDiskParameters { StorageAccountType = "StandardSSD_LRS" },
                    },
                },
                OsProfile = new OSProfile
                {
                    ComputerName = VmImages.ComputerName(Name, image.IsWindows),
                    AdminUsername = AdminUsername,
                    AdminPassword = $"[parameters('{VmImages.AdminPasswordParameter}')]",
                },
                NetworkProfile = new NetworkProfile
                {
                    NetworkInterfaces = [new NetworkInterfaceReference { Id = NicId }],
                },
            };
            _vm.DependsOn = [NicId];

            if (Group is AvailabilitySetModel availabilitySet)
            {
                _vm.Properties.AvailabilitySet = new AzureResources.Compute.SubResource { Id = availabilitySet.ResourceId };
                _vm.DependsOn.Add(availabilitySet.ResourceId);
            }
        }

        public override IList<ResourceBase> GetArmResources()
        {
            var machine = base.GetArmResources();
            return [_nic, .. machine];
        }

        public override IDictionary<string, Parameter> GetArmParameters() => VmImages.AdminPasswordParameters();
    }
}
