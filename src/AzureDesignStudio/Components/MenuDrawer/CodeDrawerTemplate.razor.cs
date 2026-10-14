using AntDesign;
using AzureDesignStudio.Models;
using AzureDesignStudio.SharedModels.Protos;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Text;

namespace AzureDesignStudio.Components.MenuDrawer
{
    internal record DeployStepStatus
    {
        public int CurrentStep { get; set; }
        public string Status { get; set; } = "starting";
        public string ErrorMessage { get; set; } = default!;
    }
    internal sealed record WhatIfChangeRow(string ChangeType, string? ResourceId, int Delta,
        string? Before = null, string? After = null, bool BeforeTruncated = false, bool AfterTruncated = false);

    public partial class CodeDrawerTemplate
    {
        private static AntDesign.StepsStatus GetStepsStatus(string status) => status switch
        {
            "started" or "process" => AntDesign.StepsStatus.Process,
            "finish" => AntDesign.StepsStatus.Finish,
            "error" => AntDesign.StepsStatus.Error,
            _ => AntDesign.StepsStatus.Wait
        };
        private CodeDrawerContent _drawerContent = null!;
        private string _codeClass = "line-numbers language-json";
        private const string upperRight = "position:absolute;top:90px;right:41px;z-index:10";
        private string _buttonStyle = upperRight;
        private bool _showDeployButton = false;
        private bool _showDeployParams = false;
        private DeploymentParameters _deployParams = null!;
        private Form<DeploymentParameters> _paramsForm = null!;
        private SubscriptionRes? _linkedSubscription = null;
        private IList<string> _resourceGroupNames = null!;
        private bool _paramFormLoading = true;
        private bool _showDeployStatus = false;
        private DeployStepStatus _stepsStatus = new();
        private bool _showWhatIfReview = false;
        private bool _whatIfLoading = false;
        private WhatIfResponse? _whatIfResult = null;
        private string? _whatIfError = null;
        private IReadOnlyList<WhatIfChangeRow> _whatIfChanges = [];
        private Dictionary<string, string> _guardrailFlags = new(StringComparer.OrdinalIgnoreCase);
        private bool _deployAsStack = false;
        private string _stackName = "ads-stack";
        private string? _approverName = null;
        private IReadOnlyList<ResourceOperationRow> _monitorRows = [];
        private string _monitorStatus = "";
        private string _monitorNotice = "";
        private bool _monitorLoading;
        private CancellationTokenSource? _monitorCts;
        private string? _retryTemplate;

        #region Button style and download
        protected override async Task OnInitializedAsync()
        {
            _drawerContent = Options;
            _codeClass = _drawerContent.Type switch
            {
                CodeDrawerContentType.Bicep => "line-numbers language-bicep",
                CodeDrawerContentType.Json => "line-numbers language-json",
                _ => "line-numbers language-json"
            };

            if (_drawerContent.Type == CodeDrawerContentType.Json)
            {
                var authState = await authenticationStateTask;
                var user = authState.User;
                if (user.Identity?.IsAuthenticated ?? false)
                {
                    _approverName = user.FindFirst("name")?.Value;
                    var subscriptions = await _deployService.GetLinkedSubscriptions();
                    if (subscriptions?.Count > 0)
                    {
                        _showDeployButton = true;
                        _linkedSubscription = subscriptions[0];
                    }
                }
            }

            await base.OnInitializedAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            await base.OnAfterRenderAsync(firstRender);

            await JS.InvokeVoidAsync("Prism.highlightAll");
        }

        void HandleAffixChange(bool status)
        {
            if (status)
            {
                _buttonStyle = "float:right;";
            }
            else
            {
                _buttonStyle = upperRight;
            }
        }

        async Task HandleDownload()
        {
            var filename = _drawerContent.Type switch
            {
                CodeDrawerContentType.Json => "azure-design-studio.json",
                CodeDrawerContentType.Bicep => "azure-design-studio.bicep",
                CodeDrawerContentType.AdsJson => "azure-design-studio.ads.json",
                _ => "azure-design-studio.json"
            };

            await DownloadFile(_drawerContent.Content, filename, "application/json");
        }

        // According to: https://docs.microsoft.com/en-us/aspnet/core/blazor/file-downloads?view=aspnetcore-6.0
        private async Task DownloadFile(string content, string filename, string contentType)
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            using var streamRef = new DotNetStreamReference(stream);
            await JS.InvokeVoidAsync("downloadFileFromStream", filename, contentType, streamRef);
        }
        #endregion

        async Task HandleDeploy()
        {
            if (_linkedSubscription == null)
                return;

            if (_stepsStatus.Status == "starting")
            {
                _paramFormLoading = true;
                _deployParams = new DeploymentParameters();
                _showDeployParams = true;

                _resourceGroupNames = await _deployService.GetResourceGroups(_linkedSubscription.SubscriptionId);

                _paramFormLoading = false;
            }
            else
            {
                _showDeployStatus = true;
            }
        }
        private void ParamsFormCancel(MouseEventArgs e)
        {
            _showDeployParams = false;
            _retryTemplate = null;
        }
        private void ParamsFormOk(MouseEventArgs e)
        {
            _paramsForm.Submit();
        }
        private async Task ParamsFormFinish(EditContext editContext)
        {
            _showDeployParams = false;

            try
            {
                System.Text.Json.JsonDocument.Parse(_deployParams.ParametersJson);
            }
            catch (System.Text.Json.JsonException)
            {
                _messageService.Error("Parameters must be valid JSON.");
                return;
            }

            _whatIfResult = null;
            _whatIfError = null;
            _whatIfChanges = [];
            _guardrailFlags = new(StringComparer.OrdinalIgnoreCase);
            _whatIfLoading = true;
            _showWhatIfReview = true;

            try
            {
                var response = await _deployService.WhatIfAsync(_linkedSubscription!.SubscriptionId,
                    _deployParams.ResourceGroup, EffectiveTemplate, _deployParams.ParametersJson);
                if (response is null || response.StatusCode != 200)
                    _whatIfError = response?.ErrorMessage ?? "What-If is not available right now.";
                else
                {
                    _whatIfResult = response;
                    _whatIfChanges = ParseWhatIfChanges(response.ChangesJson);
                    _guardrailFlags = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var flag in response.GuardrailFlags)
                        _guardrailFlags[flag.ResourceId] = flag.Reason;
                }
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                _whatIfError = "The What-If call failed; nothing was deployed.";
            }
            finally
            {
                _whatIfLoading = false;
            }
        }

        private static IReadOnlyList<WhatIfChangeRow> ParseWhatIfChanges(string changesJson)
        {
            if (string.IsNullOrWhiteSpace(changesJson))
                return [];
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<WhatIfChangeRow>>(changesJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        private bool WhatIfDeployAllowed => _whatIfResult is not null && _whatIfResult.Deletes == 0 && !_whatIfResult.GuardrailBlocked;

        /// <summary>Plan touches do-not-touch resources: server issued no token, approval is moot.</summary>
        private bool GuardrailBlocked => _whatIfResult?.GuardrailBlocked ?? false;

        private async Task ApproveAndDeployAsync(MouseEventArgs e)
        {
            if (_whatIfResult is null || !WhatIfDeployAllowed)
                return;
            if (_deployAsStack && string.IsNullOrWhiteSpace(_stackName))
            {
                _whatIfError = "A deployment stack needs a name.";
                return;
            }

            var result = _whatIfResult;
            var approval = await _deployService.ApprovePlanAsync(result.PlanHash);
            if (approval is null || approval.StatusCode != 200)
            {
                _whatIfError = approval?.ErrorMessage ?? "Approval failed; nothing was deployed.";
                _whatIfResult = null;
                return;
            }

            _showWhatIfReview = false;
            _whatIfResult = null;
            if (_deployAsStack)
                await RunStackDeployment(result);
            else
                await RunDeployment(result.WhatIfToken, result.PlanHash);
        }

        /// <summary>Applies the approved plan as a deployment stack. The wrapper swallows transport
        /// breaks into a null response, so the outcomes here are exhaustive: confirmed success, an
        /// ARM-confirmed refusal, or OutcomeUnknown - a break is never rendered as a failure.</summary>
        private async Task RunStackDeployment(SharedModels.Protos.WhatIfResponse result)
        {
            _stepsStatus = new() { Status = "started" };
            _showDeployStatus = true;

            var response = await _deployService.CreateStackAsync(_linkedSubscription!.SubscriptionId,
                _deployParams.ResourceGroup, _stackName.Trim(), EffectiveTemplate,
                _deployParams.ParametersJson, result.WhatIfToken, result.PlanHash);

            _stepsStatus = response switch
            {
                null => new() { CurrentStep = 1, Status = "unknown", ErrorMessage = "The stack apply could not be confirmed; verify the stack in Azure before retrying." },
                { StatusCode: 200 } => new() { CurrentStep = 2, Status = "finish" },
                { StatusCode: 502 } => new() { CurrentStep = 1, Status = "unknown", ErrorMessage = response.ErrorMessage },
                _ => new() { CurrentStep = 1, Status = "error", ErrorMessage = response.ErrorMessage },
            };
        }

        private void CancelWhatIf(MouseEventArgs e)
        {
            _showWhatIfReview = false;
            _retryTemplate = null;
            _whatIfResult = null;
            _whatIfError = null;
        }

        private async Task RunDeployment(string whatIfToken, string planHash)
        {
            _stepsStatus = new() { Status = "started" };
            _showDeployStatus = true;
            StartMonitor();

            await _deployService.CreateDeployment(_linkedSubscription!.SubscriptionId,
                _deployParams.ResourceGroup, EffectiveTemplate, _deployParams.ParametersJson,
                whatIfToken, planHash,
                async (deploymentStatus, errorMessage) =>
                {
                    var stateHasChanged = false;

                    var step = deploymentStatus switch
                    {
                        "started" => 0,
                        "processing" => 1,
                        "batch-started" or "batch-completed" => 1,
                        "completed" => 2,
                        _ => _stepsStatus.CurrentStep
                    };
                    if (step != _stepsStatus.CurrentStep)
                    {
                        _stepsStatus.CurrentStep = step;
                        stateHasChanged = true;
                    }
                    if (deploymentStatus == "error" && deploymentStatus != _stepsStatus.Status)
                    {
                        _stepsStatus.Status = "error";
                        _stepsStatus.ErrorMessage = errorMessage;
                        stateHasChanged = true;
                    }
                    else if ((deploymentStatus == "processing" || deploymentStatus == "batch-started" || deploymentStatus == "batch-completed") && _stepsStatus.Status != "process")
                    {
                        _stepsStatus.Status = "process";
                        stateHasChanged = true;
                    }
                    else if (deploymentStatus == "completed")
                    {
                        _stepsStatus.Status = "finish";
                        stateHasChanged = true;
                    }

                    else if (deploymentStatus == "unknown")
                    {
                        _stepsStatus.CurrentStep = 1;
                        _stepsStatus.Status = "unknown";
                        _stepsStatus.ErrorMessage = errorMessage;
                        stateHasChanged = true;
                    }
                    if (stateHasChanged)
                        await InvokeAsync(StateHasChanged);
            });
            _retryTemplate = null;
        }
        private void CloseDeployStatus(MouseEventArgs e)
        {
            _showDeployStatus = false;
            _retryTemplate = null;
            StopMonitor();
            if (_stepsStatus.Status is "error" or "finish" or "unknown")
            {
                _stepsStatus = new();
            }
        }

        // Targeted retry [56]: the monitor's failed rows narrow the template server-side; the
        // subset then runs the ordinary what-if -> approval -> apply gate under its own plan hash.
        private string EffectiveTemplate => _retryTemplate ?? _drawerContent.Content;

        private bool HasRetryableFailures => _monitorRows.Any(r => r.State is "Failed" or "Canceled");

        // A staged apply can show a failed row from batch 1 while batch 2 still runs: retry only
        // unlocks once the deployment itself is terminal, so it can never race a concurrent apply.
        private bool CanRetryNow => HasRetryableFailures
            && _monitorStatus is "Succeeded" or "Failed" or "Canceled";

        private async Task RetryFailedAsync()
        {
            var keys = _monitorRows.Where(r => r.State is "Failed" or "Canceled")
                .Select(r => r.ResourceKey).ToList();
            if (!CanRetryNow || _linkedSubscription is null)
                return;

            SharedModels.Protos.PrepareRetryResponse? response = null;
            try
            {
                response = await _deployService.PrepareRetryAsync(_drawerContent.Content, keys);
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                response = null;
            }

            if (response is null || response.StatusCode != 200 || string.IsNullOrEmpty(response.FilteredTemplateJson))
            {
                _messageService.Error(response?.ErrorMessage ?? "The retry subset could not be prepared; nothing was deployed.");
                return;
            }

            _retryTemplate = response.FilteredTemplateJson;
            var notes = response.Warnings.Count > 0 ? " " + string.Join(" ", response.Warnings) : "";
            _monitorNotice = $"Retry subset prepared for {response.MatchedKeys.Count} failed resource(s): "
                + $"{string.Join(", ", response.MatchedKeys)}.{notes} "
                + "Confirm the resource group and parameters to preview and deploy only this subset.";
            _stepsStatus = new();

            // The monitored deployment's resource group and parameters are kept verbatim: the
            // subset retried against a different target or parameterization would not be a retry.
            // Any change is an explicit edit in the form.
            _showDeployParams = true;
        }

        // Monitor [56]: read-only per-resource rows polled while the deploy modal is open.
        // Stops on modal close and on dispose; never issues a mutating call.
        private static string StateColor(string state) => state switch
        {
            "Succeeded" => "green",
            "Failed" or "Canceled" => "red",
            "Running" or "Accepted" or "Accepting" or "Created" or "Started" => "blue",
            _ => "default",
        };

        private void StartMonitor()
        {
            StopMonitor();
            _monitorCts = new CancellationTokenSource();
            _ = MonitorLoopAsync(_monitorCts.Token);
        }

        private void StopMonitor()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
            _monitorRows = [];
            _monitorStatus = "";
            _monitorNotice = "";
            _monitorLoading = false;
        }

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshMonitorCoreAsync();
                if (ct.IsCancellationRequested)
                    return;
                var terminal = _stepsStatus.Status is "error" or "finish" or "unknown"
                    || _monitorStatus is "Succeeded" or "Failed" or "Canceled";
                try
                {
                    await Task.Delay(terminal ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public Task RefreshMonitorAsync() => RefreshMonitorCoreAsync();

        private async Task RefreshMonitorCoreAsync()
        {
            if (_linkedSubscription is null || _deployParams is null)
                return;

            _monitorLoading = true;
            try
            {
                var response = await _deployService.GetDeploymentOperationsAsync(_linkedSubscription.SubscriptionId,
                    _deployParams.ResourceGroup);
                if (response is null || response.StatusCode == 404)
                {
                    _monitorStatus = "";
                    _monitorRows = [];
                    _monitorNotice = response?.ErrorMessage ?? "The deployment has not started yet.";
                }
                else if (response.StatusCode != 200)
                {
                    _monitorNotice = response.ErrorMessage;
                }
                else
                {
                    _monitorStatus = response.DeploymentStatus;
                    _monitorRows = response.Operations;
                    // Parser warnings ride in error_message; the folded rows stay usable.
                    _monitorNotice = response.ErrorMessage;
                }
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                _monitorNotice = "The monitor call failed; the deployment itself is unaffected.";
            }
            finally
            {
                _monitorLoading = false;
            }

            await InvokeAsync(StateHasChanged);
        }

        public new void Dispose()
        {
            StopMonitor();
            base.Dispose();
        }
    }
}
