using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Logic;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.Logic
{
    [MapToDto(TypeKey = AdsConstants.LogicApps)]
    public class LogicAppsModel : AzureNodeBase
    {
        public LogicAppsModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Logic App";
        private readonly Workflows _workflow = new()
        {
            Properties = new()
            {
                State = "Enabled",
            },
        };
        protected override ResourceBase ArmResource => _workflow;
        public override Type? DataFormType => typeof(LogicAppsForm);
        [MapToDto]
        [DisplayName("State")]
        public string State
        {
            get => _workflow.Properties.State;
            set => _workflow.Properties.State = value;
        }
        [MapToDto]
        [DisplayName("Integration account")]
        public string IntegrationAccount
        {
            get => _workflow.Properties.IntegrationAccount;
            set => _workflow.Properties.IntegrationAccount = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z][a-zA-Z0-9-]{0,63}$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Logic App cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<LogicAppsModelDto>(this);
        }
    }
}
