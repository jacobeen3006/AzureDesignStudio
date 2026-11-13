using AntDesign;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using System.ComponentModel.DataAnnotations;

namespace AzureDesignStudio.Components.MenuDrawer
{
    internal class DesignName
    {
        [Required]
        public string Name { get; set; } = null!;
    }
    public partial class SaveDrawerTemplate
    {
        int _savedDesignCount = -1;
        DesignName _designName = null!;
        Form<DesignName> _form = null!;
        private readonly FormValidationRule[] designNameRules = new FormValidationRule[]{
            new FormValidationRule{ Pattern = "^[a-zA-Z0-9\\s]+$", Message = "'Design name' can only have alphanumeric characters." } ,
            new FormValidationRule{ Min=1, Max=200 }
        };

        internal record DesignRecord(string Key, string DesignName);

        List<DesignRecord> _allDesignRecords = [];
        bool _loadingDesigns = false;

        private const int MaxDesigns = 10;

        // The limit is on how many designs a user holds, so it stops new names only. Saving
        // changes to a design that already exists is always allowed (the server agrees); the
        // button used to lock for both, leaving a user at the limit unable to save their work.
        private bool SaveBlockedByQuota => _savedDesignCount >= MaxDesigns
            && !_allDesignRecords.Any(r => string.Equals(r.DesignName, _designName.Name?.Trim(), StringComparison.OrdinalIgnoreCase));

        protected override async Task OnInitializedAsync()
        {
            _loadingDesigns = true;
            _designName = new()
            {
                Name = adsContext.CurrentDesignName
            };
            try
            {
                var authState = await authenticationStateTask;
                var user = authState.User;
                if (user.Identity?.IsAuthenticated ?? false)
                    await RefreshSavedDesigns();
            }
            finally
            {
                _loadingDesigns = false;
            }

            await base.OnInitializedAsync();
        }

        /// <summary>Reloads the list and the count beside it. A list that cannot be read keeps
        /// what is on screen and says why, instead of showing an empty table as if nothing
        /// had ever been saved.</summary>
        private async Task RefreshSavedDesigns()
        {
            var (status, savedDesigns) = await _designService.GetAllSavedDesign();
            if (status != 200 || savedDesigns == null)
            {
                _messageService.Error($"The saved designs could not be listed: {Services.DesignGrpcService.Describe(status)}.");
                return;
            }

            _savedDesignCount = savedDesigns.Count;
            _allDesignRecords = new List<DesignRecord>();
            for (int i = 0; i < savedDesigns.Count; i++)
            {
                _allDesignRecords.Add(new DesignRecord(i.ToString(), savedDesigns[i]));
            }
        }

        async Task SaveButtonClick()
        {
            if (!_form.Validate())
                return;

            adsContext.CurrentDesignName = _designName.Name;
            DrawerRef<string> dr = (FeedbackRef as DrawerRef<string>)!;
            await dr!.CloseAsync($"save:{adsContext.CurrentDesignName}");
        }

        private async Task BeginLogin()
        {
            DrawerRef<string> dr = (FeedbackRef as DrawerRef<string>)!;
            await dr!.CloseAsync(string.Empty);

            if (_hostAuth.SignIn is { } signIn)
            {
                await signIn();
                return;
            }
            _navManager.NavigateToLogin("authentication/login");
        }

        async Task LoadButtonClick()
        {
            var designName = _selectedRows?.FirstOrDefault()?.DesignName;
            if (string.IsNullOrEmpty(designName))
                return;

            DrawerRef<string> dr = (FeedbackRef as DrawerRef<string>)!;
            await dr!.CloseAsync($"load:{designName}");
        }

        async Task HandleDelete()
        {
            var designName = _selectedRows?.FirstOrDefault()?.DesignName;
            if (string.IsNullOrEmpty(designName))
                return;

            var status = await _designService.DeleteDesign(designName);
            if (status != 200)
            {
                _messageService.Error($"\"{designName}\" was not deleted: {Services.DesignGrpcService.Describe(status)}.");
                return;
            }

            // The deleted row must not stay selected: Load and Delete act on the selection.
            _selectedRows = [];
            _loadingDesigns = true;
            try
            {
                await RefreshSavedDesigns();
            }
            finally
            {
                _loadingDesigns = false;
            }
        }
    }
}
