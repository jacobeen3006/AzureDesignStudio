using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.ContainerInstance;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.ContainerInstance
{
    [MapToDto(TypeKey = AdsConstants.AzureContainerInstance)]
    public class ContainerInstanceModel : AzureNodeBase
    {
        public ContainerInstanceModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Container Instance";
        private readonly ContainerGroups _group = new()
        {
            Properties = new()
            {
                OsType = "Linux",
                Cpu = 1.0,
                MemoryInGb = 1.5,
                Containers = new List<object>(),
            },
        };
        protected override ResourceBase ArmResource => _group;
        public override Type? DataFormType => typeof(ContainerInstanceForm);
        [MapToDto]
        [DisplayName("OS type")]
        public string OsType
        {
            get => _group.Properties.OsType;
            set => _group.Properties.OsType = value;
        }
        [MapToDto]
        [DisplayName("CPU")]
        public string Cpu
        {
            get => _group.Properties.Cpu.ToString(CultureInfo.InvariantCulture);
            set
            {
                // Unparseable or absent input keeps the current value instead of throwing.
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu))
                    _group.Properties.Cpu = cpu;
            }
        }
        [MapToDto]
        [DisplayName("Memory (GB)")]
        public string MemoryInGb
        {
            get => _group.Properties.MemoryInGb.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var memory))
                    _group.Properties.MemoryInGb = memory;
            }
        }
        public const string DefaultImage = "mcr.microsoft.com/azuredocs/aci-helloworld:latest";
        private string _image = DefaultImage;

        [MapToDto]
        [DisplayName("Image")]
        public string Image
        {
            get => _image;
            set => _image = string.IsNullOrWhiteSpace(value) ? _image : value.Trim();
        }

        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])?$");
        }

        // A container group is its containers: the export used to write an empty list, with
        // the CPU and memory on the group, where the API has no such members.
        protected override void PopulateArmAttributes()
        {
            base.PopulateArmAttributes();

            _group.Properties.RestartPolicy = "Always";
            _group.Properties.Containers =
            [
                new Dictionary<string, object>
                {
                    ["name"] = Name,
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["image"] = Image,
                        ["resources"] = new Dictionary<string, object>
                        {
                            ["requests"] = new Dictionary<string, object>
                            {
                                ["cpu"] = _group.Properties.Cpu,
                                ["memoryInGB"] = _group.Properties.MemoryInGb,
                            },
                        },
                    },
                },
            ];
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Container Instance cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<ContainerInstanceModelDto>(this);
        }
    }
}
