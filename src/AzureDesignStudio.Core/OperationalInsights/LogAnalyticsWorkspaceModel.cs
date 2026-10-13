using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.OperationalInsights;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams.Core.Models;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Core.OperationalInsights
{
    [MapToDto(TypeKey = AdsConstants.LogAnalyticsWorkspace)]
    public class LogAnalyticsWorkspaceModel : AzureNodeBase
    {
        public LogAnalyticsWorkspaceModel() : base()
        {
            AddAzurePort(PortAlignment.Left);
            AddAzurePort(PortAlignment.Top);
            AddAzurePort(PortAlignment.Right);
            AddAzurePort(PortAlignment.Bottom);
        }

        public override string ServiceName => "Log Analytics Workspace";
        private readonly Workspaces _workspace = new()
        {
            Sku = new()
            {
                Name = "PerGB2018",
            },
            Properties = new()
            {
                RetentionInDays = 30,
                EnableInteractiveQuery = true,
            },
        };
        protected override ResourceBase ArmResource => _workspace;
        public override Type? DataFormType => typeof(LogAnalyticsWorkspaceForm);
        [MapToDto]
        [DisplayName("SKU")]
        public string SkuName
        {
            get => _workspace.Sku.Name;
            set => _workspace.Sku.Name = value;
        }
        [MapToDto]
        [DisplayName("Retention (days)")]
        public int RetentionInDays
        {
            get => _workspace.Properties.RetentionInDays;
            set => _workspace.Properties.RetentionInDays = value;
        }
        [MapToDto]
        [DisplayName("Interactive query")]
        public bool EnableInteractiveQuery
        {
            get => _workspace.Properties.EnableInteractiveQuery;
            set => _workspace.Properties.EnableInteractiveQuery = value;
        }
        public override bool IsValid
        {
            get => Regex.IsMatch(Name, @"^[a-zA-Z][a-zA-Z0-9-]{0,61}[a-zA-Z0-9]$");
        }
        public override (bool result, string message) IsDrappable(GroupModel overlappedGroup)
        {
            if (overlappedGroup != null)
                return (false, $"Log Analytics Workspace cannot be in {((IAzureNode)overlappedGroup).ServiceName}.");

            return (true, string.Empty);
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<LogAnalyticsWorkspaceModelDto>(this);
        }
    }
}
