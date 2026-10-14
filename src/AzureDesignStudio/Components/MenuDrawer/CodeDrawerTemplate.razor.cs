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
        private string? _approverName = null;

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
            _whatIfLoading = true;
            _showWhatIfReview = true;

            try
            {
                var response = await _deployService.WhatIfAsync(_linkedSubscription!.SubscriptionId,
                    _deployParams.ResourceGroup, _drawerContent.Content, _deployParams.ParametersJson);
                if (response is null || response.StatusCode != 200)
                    _whatIfError = response?.ErrorMessage ?? "What-If is not available right now.";
                else
                {
                    _whatIfResult = response;
                    _whatIfChanges = ParseWhatIfChanges(response.ChangesJson);
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

        private bool WhatIfDeployAllowed => _whatIfResult is not null && _whatIfResult.Deletes == 0;

        private async Task ApproveAndDeployAsync(MouseEventArgs e)
        {
            if (_whatIfResult is null || !WhatIfDeployAllowed)
                return;

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
            await RunDeployment(result.WhatIfToken, result.PlanHash);
        }

        private void CancelWhatIf(MouseEventArgs e)
        {
            _showWhatIfReview = false;
            _whatIfResult = null;
            _whatIfError = null;
        }

        private async Task RunDeployment(string whatIfToken, string planHash)
        {
            _stepsStatus.Status = "started";
            _showDeployStatus = true;

            await _deployService.CreateDeployment(_linkedSubscription!.SubscriptionId,
                _deployParams.ResourceGroup, _drawerContent.Content, _deployParams.ParametersJson,
                whatIfToken, planHash,
                async (deploymentStatus, errorMessage) =>
                {
                    var stateHasChanged = false;

                    var step = deploymentStatus switch
                    {
                        "started" => 0,
                        "processing" => 1,
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
                    else if (deploymentStatus == "processing" && _stepsStatus.Status != "process")
                    {
                        _stepsStatus.Status = "process";
                        stateHasChanged = true;
                    }
                    else if (deploymentStatus == "completed")
                    {
                        _stepsStatus.Status = "finish";
                        stateHasChanged = true;
                    }

                    if (stateHasChanged)
                        await InvokeAsync(StateHasChanged);
                });
        }
        private void CloseDeployStatus(MouseEventArgs e)
        {
            _showDeployStatus = false;
            if (_stepsStatus.Status == "error" || _stepsStatus.Status == "finish")
            {
                _stepsStatus = new();
            }
        }
    }
}
