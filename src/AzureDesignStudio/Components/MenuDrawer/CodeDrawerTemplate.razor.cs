using AntDesign;
using AzureDesignStudio.Models;
using AzureDesignStudio.Services;
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

    /// <summary>One row of the deployment-stacks what-if (<c>StackWhatIfResponse.ChangesJson</c>).</summary>
    internal sealed record StackChangeRow(string ChangeType, string? ResourceId, string? Type,
        string? ManagementBefore = null, string? ManagementAfter = null, string? DenyBefore = null, string? DenyAfter = null,
        bool Potential = false, string? UnsupportedReason = null);

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
        private IList<SubscriptionRes> _subscriptions = [];
        private IList<string> _resourceGroupNames = [];

        /// <summary>The subscription the deployment goes to. The form used to take the first
        /// one in the list, with no way to choose; now every linked subscription and every one
        /// the signed-in user can see (desktop) is offered. Changing it reloads the resource groups.</summary>
        private string SelectedSubscriptionId
        {
            get => _linkedSubscription?.SubscriptionId ?? string.Empty;
            set
            {
                var chosen = _subscriptions.FirstOrDefault(s => s.SubscriptionId == value);
                if (chosen is null || ReferenceEquals(chosen, _linkedSubscription))
                    return;
                _linkedSubscription = chosen;
                if (_deployParams is not null)
                    _deployParams.ResourceGroup = null!;
                _ = LoadResourceGroupsAsync();
            }
        }

        private async Task LoadResourceGroupsAsync()
        {
            if (_linkedSubscription is null)
                return;
            _paramFormLoading = true;
            StateHasChanged();
            var names = await _deployService.GetResourceGroups(_linkedSubscription.SubscriptionId);
            _resourceGroupNames = names ?? [];
            _paramFormLoading = false;
            if (names is null)
                _messageService.Error("The resource groups could not be listed: the server is not reachable, or Azure refused the subscription's credential.");
            StateHasChanged();
        }
        private bool _paramFormLoading = true;
        private bool _showDeployStatus = false;
        private DeployStepStatus _stepsStatus = new();
        private bool _showWhatIfReview = false;
        private bool _whatIfLoading = false;
        private WhatIfResponse? _whatIfResult = null;
        private string? _whatIfError = null;
        private IReadOnlyList<WhatIfChangeRow> _whatIfChanges = [];
        private Dictionary<string, string> _guardrailFlags = new(StringComparer.OrdinalIgnoreCase);
        // Azure Policy preflight (advisory): the review's findings, Deny first, then Audit, then
        // the rest; and the ids that would be denied, for the per-row tag (ids case-insensitive).
        private IReadOnlyList<PolicyFlag> _policyFlags = [];
        private HashSet<string> _policyDenied = new(StringComparer.OrdinalIgnoreCase);
        // Quota preflight (advisory): the findings that need a look - Exceeds and SkuUnavailable,
        // then NearLimit, then Unknown (and any status this client does not know) - and the Ok
        // ones, which the review shows collapsed.
        private IReadOnlyList<QuotaFlag> _quotaAttention = [];
        private IReadOnlyList<QuotaFlag> _quotaOk = [];
        private bool _deployAsStack = false;
        private string _stackName = "ads-stack";
        // v2 12.3 closer: the review-screen stack probe (null = availability unknown), whether a
        // lookup has run at all for the current name/scope, and the review-gated bypass flag.
        private StackProbe? _stackProbe;
        private bool _stackProbed;
        private bool _stackChecking;
        private bool _bypassOutOfSync;
        // The stack path is gated on the deployment-stacks what-if, not the plain one: its own token,
        // bound to the exact stack body (template, parameters, policy, bypass flag, stack name).
        private SharedModels.Protos.StackWhatIfResponse? _stackWhatIf;
        private IReadOnlyList<StackChangeRow> _stackChanges = [];
        private bool _stackWhatIfLoading;
        private string? _stackWhatIfError;
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
                        _subscriptions = subscriptions;
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
                _deployParams = new DeploymentParameters();
                _showDeployParams = true;
                await LoadResourceGroupsAsync();
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
            // Target-aware required-ness, mirroring the server's TryBuildTargetScope: the form
            // never sends a half-named identity, so a bad combination is a toast, not a round-trip.
            if (ScopeTarget == DeployTarget.ResourceGroup && string.IsNullOrWhiteSpace(_deployParams.ResourceGroup))
            {
                _messageService.Error("Choose a resource group for the resource-group target.");
                return;
            }
            if (ScopeTarget == DeployTarget.ManagementGroup && string.IsNullOrWhiteSpace(_deployParams.ManagementGroupId))
            {
                _messageService.Error("A management-group target needs a management group id.");
                return;
            }
            if (ScopeTarget != DeployTarget.ResourceGroup && string.IsNullOrWhiteSpace(_deployParams.Location))
            {
                _messageService.Error("A non-resource-group target needs a deployment location (e.g. westeurope).");
                return;
            }

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
            _policyFlags = [];
            _policyDenied = new(StringComparer.OrdinalIgnoreCase);
            _quotaAttention = [];
            _quotaOk = [];
            // A new preview means new bytes and maybe a new scope: the old probe proves nothing.
            _stackProbe = null;
            _stackProbed = false;
            _bypassOutOfSync = false;
            ClearStackWhatIf();
            _whatIfLoading = true;
            _showWhatIfReview = true;

            try
            {
                var response = await _deployService.WhatIfAsync(_linkedSubscription!.SubscriptionId,
                    ScopeResourceGroup, EffectiveTemplate, _deployParams.ParametersJson,
                    ScopeTarget, ScopeManagementGroupId, ScopeLocation);
                if (response is null || response.StatusCode != 200)
                    _whatIfError = response?.ErrorMessage ?? "What-If is not available right now.";
                else
                {
                    _whatIfResult = response;
                    _whatIfChanges = ParseWhatIfChanges(response.ChangesJson);
                    _guardrailFlags = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var flag in response.GuardrailFlags)
                        _guardrailFlags[flag.ResourceId] = flag.Reason;
                    _policyFlags = [.. response.PolicyFlags.OrderBy(f => PolicyRank(f.Verdict))];
                    _policyDenied = new(response.PolicyFlags.Where(f => PolicyRank(f.Verdict) == 0).Select(f => f.ResourceId),
                        StringComparer.OrdinalIgnoreCase);
                    _quotaAttention = [.. response.QuotaFlags.Where(f => QuotaRank(f.Status) < QuotaOkRank).OrderBy(f => QuotaRank(f.Status))];
                    _quotaOk = [.. response.QuotaFlags.Where(f => QuotaRank(f.Status) == QuotaOkRank)];
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

            // The stack review must speak about the bytes just previewed: re-probe the
            // managed-vs-planned diff whenever stack mode survives a re-preview.
            if (_deployAsStack)
                await ProbeStackAsync();
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

        /// <summary>Deny 0, Audit 1, anything else 2 - the order and the tag of a policy finding.</summary>
        private static int PolicyRank(string verdict) =>
            string.Equals(verdict, "Deny", StringComparison.OrdinalIgnoreCase) ? 0
            : string.Equals(verdict, "Audit", StringComparison.OrdinalIgnoreCase) ? 1
            : 2;

        private static string PolicyTagColor(string verdict) => PolicyRank(verdict) switch
        {
            0 => "red",
            1 => "orange",
            _ => "default",
        };

        private static string PolicyTagText(string verdict) => PolicyRank(verdict) switch
        {
            0 => "Would be denied",
            1 => "Audit",
            _ => "Check",
        };

        private const int QuotaOkRank = 3;

        /// <summary>Exceeds and SkuUnavailable 0, NearLimit 1, Ok 3, anything else (Unknown, or a
        /// status this client does not know) 2 - never Ok by default.</summary>
        private static int QuotaRank(string status) =>
            string.Equals(status, "Exceeds", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "SkuUnavailable", StringComparison.OrdinalIgnoreCase) ? 0
            : string.Equals(status, "NearLimit", StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(status, "Ok", StringComparison.OrdinalIgnoreCase) ? QuotaOkRank
            : 2;

        private static string QuotaTagColor(string status) => QuotaRank(status) switch
        {
            0 => "red",
            1 => "orange",
            _ => "default",
        };

        private static string QuotaTagText(string status) =>
            string.Equals(status, "SkuUnavailable", StringComparison.OrdinalIgnoreCase) ? "Size unavailable"
            : QuotaRank(status) switch
            {
                0 => "Exceeds",
                1 => "Near limit",
                _ => "Unknown",
            };

        /// <summary>"{DisplayName} in {location}: {current} used + {requested} requested of {limit}";
        /// a size that cannot be deployed shows its detail instead (it names the location), and so
        /// does an Unknown check, whose numbers were not (all) read.</summary>
        private static string QuotaLine(QuotaFlag flag)
        {
            if (flag.Detail.Length > 0 && string.Equals(flag.Status, "SkuUnavailable", StringComparison.OrdinalIgnoreCase))
                return $"{flag.DisplayName}: {flag.Detail}";
            if (flag.Detail.Length > 0 && QuotaRank(flag.Status) == 2)
                return $"{flag.DisplayName} in {flag.Location}: {flag.Detail}";
            return $"{flag.DisplayName} in {flag.Location}: {flag.Current} used + {flag.Requested} requested of {flag.Limit}";
        }

        private string QuotaRoomSummary => _quotaOk.Count == 1 ? "1 quota has room" : $"{_quotaOk.Count} quotas have room";

        /// <summary>The name an ARM id ends with: a resource's name, a policy assignment's name.</summary>
        private static string LastSegment(string id)
        {
            var trimmed = id.TrimEnd('/');
            return trimmed[(trimmed.LastIndexOf('/') + 1)..];
        }

        private bool WhatIfDeployAllowed => _whatIfResult is not null && _whatIfResult.Deletes == 0 && !_whatIfResult.GuardrailBlocked;

        /// <summary>A stack deploys only from a stack what-if that earned a token: no deletes, no guardrail
        /// block and no indeterminacy (the server issues no token for those).</summary>
        private bool StackDeployAllowed => _stackWhatIf is { StatusCode: 200 } r && r.WhatIfToken.Length > 0
            && r.Deletes == 0 && !r.GuardrailBlocked && r.IndeterminacyReason.Length == 0;

        private bool ReviewDeployAllowed => _deployAsStack
            ? !_stackWhatIfLoading && WhatIfDeployAllowed && StackDeployAllowed
            : WhatIfDeployAllowed;

        /// <summary>The bypass flag lands in the hashed stack body, so the preview and the apply send the same value.</summary>
        private bool EffectiveBypass => _bypassOutOfSync && _stackProbe?.Exists == true;

        /// <summary>Plan touches do-not-touch resources: server issued no token, approval is moot.</summary>
        private bool GuardrailBlocked => (_whatIfResult?.GuardrailBlocked ?? false)
            || (_deployAsStack && (_stackWhatIf?.GuardrailBlocked ?? false));

        private async Task ApproveAndDeployAsync(MouseEventArgs e)
        {
            if (_whatIfResult is null || !WhatIfDeployAllowed)
                return;
            if (_deployAsStack && string.IsNullOrWhiteSpace(_stackName))
            {
                _whatIfError = "A deployment stack needs a name.";
                return;
            }
            // v2 12.3 stacks are resource-group deployments: CreateStack has no target fields, so
            // a stack approval under another scope would preview one identity and submit another.
            if (_deployAsStack && ScopeTarget != DeployTarget.ResourceGroup)
            {
                _whatIfError = "Deployment stacks deploy at resource-group scope only - switch the target back to Resource group to use stack mode.";
                return;
            }
            if (_deployAsStack && !StackDeployAllowed)
            {
                _whatIfError = _stackWhatIf?.IndeterminacyReason is { Length: > 0 } reason
                    ? $"The stack what-if cannot be trusted: {reason}"
                    : "Run the stack what-if (set the stack name) before approving a stack deployment.";
                return;
            }
            var result = _whatIfResult;
            var approval = await _deployService.ApprovePlanAsync(_deployAsStack ? _stackWhatIf!.PlanHash : result.PlanHash);
            if (approval is null || approval.StatusCode != 200)
            {
                _whatIfError = approval?.ErrorMessage ?? "Approval failed; nothing was deployed.";
                _whatIfResult = null;
                return;
            }

            _showWhatIfReview = false;
            _whatIfResult = null;
            if (_deployAsStack)
                await RunStackDeployment(_stackWhatIf!);
            else
                await RunDeployment(result.WhatIfToken, result.PlanHash);
        }

        /// <summary>Applies the approved plan as a deployment stack. The wrapper swallows transport
        /// breaks into a null response, so the outcomes here are exhaustive: confirmed success, an
        /// ARM-confirmed refusal, or OutcomeUnknown - a break is never rendered as a failure.</summary>
        private async Task RunStackDeployment(SharedModels.Protos.StackWhatIfResponse result)
        {
            _stepsStatus = new() { Status = "started" };
            _showDeployStatus = true;

            var response = await _deployService.CreateStackAsync(_linkedSubscription!.SubscriptionId,
                _deployParams.ResourceGroup, _stackName.Trim(), EffectiveTemplate,
                _deployParams.ParametersJson, result.WhatIfToken, result.PlanHash,
                bypassOutOfSync: EffectiveBypass);

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

        // ---- v2 12.3 closer [61]: stack-mode probe. A null probe means "unknown" and renders
        // as such - never as "new". The server computes the managed-vs-planned diff so the
        // shared WASM+desktop drawer stays free of the Discovery engine dependency. ----
        private async Task OnStackModeChangedAsync()
        {
            _stackProbe = null;
            _stackProbed = false;
            _bypassOutOfSync = false;
            await ProbeStackAsync();
        }

        private async Task OnStackNameChangedAsync()
        {
            _stackProbe = null;
            _stackProbed = false;
            _bypassOutOfSync = false;
            await ProbeStackAsync();
        }

        private void ClearStackWhatIf()
        {
            _stackWhatIf = null;
            _stackChanges = [];
            _stackWhatIfError = null;
            _stackWhatIfLoading = false;
        }

        /// <summary>Runs the deployment-stacks what-if for the stack name and policy now on screen. Any
        /// earlier token is dropped first: a token for other bytes proves nothing about these.</summary>
        private async Task RunStackWhatIfAsync()
        {
            ClearStackWhatIf();
            var name = (_stackName ?? "").Trim();
            if (!_deployAsStack || ScopeTarget != DeployTarget.ResourceGroup || _linkedSubscription is null
                || _deployParams is null || name.Length == 0)
                return;
            _stackWhatIfLoading = true;
            StateHasChanged();
            try
            {
                var response = await _deployService.StackWhatIfAsync(_linkedSubscription.SubscriptionId,
                    _deployParams.ResourceGroup, name, EffectiveTemplate, _deployParams.ParametersJson,
                    bypassOutOfSync: EffectiveBypass);
                if (response is null || response.StatusCode != 200)
                    _stackWhatIfError = response?.ErrorMessage is { Length: > 0 } message
                        ? message
                        : "The stack What-If is not available right now.";
                else
                {
                    _stackWhatIf = response;
                    _stackChanges = ParseStackChanges(response.ChangesJson);
                    // The stack review shows protected resources the way the plain review does.
                    foreach (var flag in response.GuardrailFlags)
                        _guardrailFlags[flag.ResourceId] = flag.Reason;
                }
            }
            finally
            {
                _stackWhatIfLoading = false;
            }
        }

        private static IReadOnlyList<StackChangeRow> ParseStackChanges(string changesJson)
        {
            if (string.IsNullOrWhiteSpace(changesJson))
                return [];
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<StackChangeRow>>(changesJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        private async Task ProbeStackAsync()
        {
            var name = (_stackName ?? "").Trim();
            if (!_deployAsStack || ScopeTarget != DeployTarget.ResourceGroup || _linkedSubscription is null
                || _deployParams is null || name.Length == 0)
            {
                ClearStackWhatIf();
                return;
            }
            _stackChecking = true;
            StateHasChanged();
            _stackProbe = await _deployService.GetStackAsync(_linkedSubscription.SubscriptionId,
                _deployParams.ResourceGroup, name, EffectiveTemplate);
            _stackProbed = true;
            _stackChecking = false;
            await RunStackWhatIfAsync();
        }

        private async Task RunDeployment(string whatIfToken, string planHash)
        {
            _stepsStatus = new() { Status = "started" };
            _showDeployStatus = true;
            StartMonitor();

            await _deployService.CreateDeployment(_linkedSubscription!.SubscriptionId,
                ScopeResourceGroup, EffectiveTemplate, _deployParams.ParametersJson,
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

        // Sanitized scope fields for the wire (v2 11.2): the server binds identity from the target
        // kind, so fields irrelevant to the chosen target stay empty; the preview, the plan hash,
        // the deploy and the monitor all read these same accessors, so the approved scope can
        // never drift between the steps of one gate.
        private DeployTarget ScopeTarget => _deployParams?.Target ?? DeployTarget.ResourceGroup;
        private string ScopeResourceGroup => ScopeTarget == DeployTarget.ResourceGroup ? _deployParams!.ResourceGroup : "";
        private string ScopeManagementGroupId => ScopeTarget == DeployTarget.ManagementGroup ? _deployParams!.ManagementGroupId.Trim() : "";
        private string ScopeLocation => ScopeTarget != DeployTarget.ResourceGroup ? _deployParams!.Location.Trim() : "";
        private string TargetLabel => ScopeTarget switch
        {
            DeployTarget.Subscription => $"subscription {_linkedSubscription?.SubscriptionName ?? _linkedSubscription?.SubscriptionId}",
            DeployTarget.ManagementGroup => $"management group {_deployParams!.ManagementGroupId.Trim()}",
            DeployTarget.Tenant => "tenant root",
            _ => $"resource group {_deployParams!.ResourceGroup}",
        };

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
                + "Confirm the target and parameters to preview and deploy only this subset.";
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
                    ScopeResourceGroup, ScopeTarget, ScopeManagementGroupId);
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
