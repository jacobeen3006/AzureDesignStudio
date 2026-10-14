using AzureDesignStudio.Models;

namespace AzureDesignStudio.Components.MenuDrawer
{
    /// <summary>
    /// Editor for the server-side do-not-touch guardrail list (v2 plan 13.1/[36-final]).
    /// The list is stored and enforced server-side; this drawer only reads and replaces it -
    /// validity is decided by the server, never by client-side JSON guessing.
    /// </summary>
    public partial class GuardrailsDrawerTemplate
    {
        private string _json = "";
        private string _updatedBy = "";
        private string _updatedAt = "";
        private string? _error;
        private bool _busy;

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            _busy = true;
            try
            {
                var response = await _deployService.GetGuardrailPolicyAsync();
                if (response is null || response.StatusCode != 200)
                    _error = response?.ErrorMessage ?? "The guardrail policy is not available right now.";
                else
                {
                    _error = null;
                    _json = response.Json;
                    _updatedBy = response.UpdatedBy;
                    _updatedAt = FormatUtc(response.UpdatedAt);
                }
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                _error = "The guardrail policy call failed.";
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task SaveAsync()
        {
            _busy = true;
            try
            {
                var response = await _deployService.SetGuardrailPolicyAsync(_json);
                if (response is null || response.StatusCode != 200)
                {
                    // The server refused; the stored list is untouched and the editor keeps the draft.
                    _error = response?.ErrorMessage ?? "Saving the guardrail policy failed; the stored list is unchanged.";
                    return;
                }

                _error = null;
                _json = response.Json;
                _updatedBy = response.UpdatedBy;
                _updatedAt = FormatUtc(response.UpdatedAt);
                _messageService.Success("Do-not-touch list saved - new reviews and every queued plan are judged against it.");
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                _error = "The guardrail policy save failed; the stored list is unchanged.";
            }
            finally
            {
                _busy = false;
            }
        }

        private static string FormatUtc(string roundTrip) =>
            DateTime.TryParse(roundTrip, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc)
                ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : roundTrip;
    }
}
