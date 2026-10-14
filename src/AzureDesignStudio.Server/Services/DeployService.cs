using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web.Resource;
using AzureDesignStudio.SharedModels.Protos;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Discovery.Arm;
using SubscriptionInfo = AzureDesignStudio.SharedModels.Protos.SubscriptionInfo;
using AzureDesignStudio.Discovery.Iac;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Discovery.ResourceGraph;
using AzureDesignStudio.Server.Models;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Azure.Identity;
using Google.Protobuf.WellKnownTypes;
using Azure.ResourceManager;
using SubscriptionResource = Azure.ResourceManager.Resources.SubscriptionResource;
using Azure;
using Azure.ResourceManager.Resources.Deployments;
using Azure.ResourceManager.Resources.Deployments.Models;
using System.Text.Json;

namespace AzureDesignStudio.Server.Services
{
    [Authorize]
    [RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]
    public class DeployService : Deploy.DeployBase
    {
        private readonly DesignDbContext _designContext;
        private readonly ILogger<DeployService> _logger;
        private readonly ICryptoService _cryptoService;
        private readonly WhatIfTokenGate _whatIfGate;
        private readonly IHttpClientFactory _httpClientFactory;
        public DeployService(DesignDbContext context, ILogger<DeployService> logger, ICryptoService cryptoService,
            WhatIfTokenGate whatIfGate, IHttpClientFactory httpClientFactory)
        {
            _designContext = context;
            _designContext.Database.EnsureCreated();

            _logger = logger;
            _cryptoService = cryptoService;
            _whatIfGate = whatIfGate;
            _httpClientFactory = httpClientFactory;
        }
        public override async Task<SaveSubInfoResponse> SaveSubscriptionInfo(SubscriptionInfo request, ServerCallContext context)
        {
            var response = new SaveSubInfoResponse();
            // Get user id
            var userIdClaim = ServiceTools.GetUserId(context);
            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                _logger.LogWarning("Access denied. userIdClaim is empty.");
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return response;
            }
            var userId = new Guid(userIdClaim);

            if(!ServiceTools.IsValidSubscriptionName(request.SubscriptionName))
            {
                _logger.LogWarning("{SubscriptionName} is not valid.", request.SubscriptionName);
                response.StatusCode = StatusCodes.Status400BadRequest;
                return response;
            }

            try
            {
                var subscription = new AzureSubscriptionModel
                {
                    UserId = userId,
                    SubscriptionId = new Guid(request.SubscriptionId),
                    SubscriptionName = request.SubscriptionName,
                    TenantId = new Guid(request.TenantId),
                    ClientId = new Guid(request.ClientId),
                    ClientSecret = await _cryptoService.Encrypt(request.ClientSecret)
                };
                _designContext.AzureSubscriptions.Add(subscription);
            }
            catch(Exception ex)
            {
                _logger.LogWarning(ex, "Exception occurred.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                return response;
            }

            try
            {
                await _designContext.SaveChangesAsync();
                response.StatusCode = StatusCodes.Status200OK;
            }
            catch (DbUpdateException dbex)
            {
                _logger.LogWarning(dbex, "DbUpdateException occurred.");
                response.StatusCode = StatusCodes.Status400BadRequest;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception occurred.");
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            return response;
        }
        public override Task<LoadSubInfoResponse> LoadSubscriptionInfo(Empty request, ServerCallContext context)
        {
            var response = new LoadSubInfoResponse();

            var userIdClaim = ServiceTools.GetUserId(context);
            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                _logger.LogWarning("Access denied. userIdClaim is empty.");
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.FromResult(response);
            }
            var userId = new Guid(userIdClaim);

            var subResources = _designContext.AzureSubscriptions.Where(s => s.UserId == userId);
            foreach(var subResource in subResources)
            {
                response.SubscriptionData.Add(new SubscriptionRes
                {
                    SubscriptionId = subResource.SubscriptionId.ToString(),
                    SubscriptionName = subResource.SubscriptionName,
                });
            }
            response.StatusCode = StatusCodes.Status200OK;

            return Task.FromResult(response);
        }
        private async Task<AzureSubscriptionModel?> GetSubscriptionInfo(Guid userId, Guid subscriptionId)
        {
            var subModel = _designContext.AzureSubscriptions.Where(s => s.UserId == userId && s.SubscriptionId == subscriptionId)
                .FirstOrDefault();
            if (subModel == null)
                return null;

            subModel.ClientSecret = await _cryptoService.Decrypt(subModel.ClientSecret);
            return subModel;
        }
        private async Task<SubscriptionResource?> GetSubscriptionResource(Guid userId, Guid subscriptionId)
        {
            var subModel = await GetSubscriptionInfo(userId, subscriptionId);
            if (subModel == null)
                return null;

            var azCred = new ClientSecretCredential(subModel.TenantId.ToString(), 
                subModel.ClientId.ToString(), subModel.ClientSecret);
            var armClient = new ArmClient(azCred, subModel.SubscriptionId.ToString());
            var subResource = await armClient.GetDefaultSubscriptionAsync();

            return subResource;
        }

        /// <summary>
        /// What-if preflight [50]: run the ARM prediction against the caller's linked subscription and
        /// issue the short-lived token (plus plan hash) that the approval and the deploy must both present.
        /// </summary>
        public override async Task<WhatIfResponse> WhatIf(DeploymentRequest request, ServerCallContext context)
        {
            var response = new WhatIfResponse();

            if (string.IsNullOrWhiteSpace(request.ArmTemplate))
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "Template is empty.";
                return response;
            }

            if (ArmTemplateValidator.TryFindBlockingErrors(request.ArmTemplate, request.Parameters, out var validationErrors))
            {
                _logger.LogWarning("Template blocked before what-if: {Errors}", validationErrors);
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = validationErrors;
                return response;
            }

            if (DeployScopePlanner.TryStageForApply(request.ArmTemplate, request.SubscriptionId, request.ResourceGroupName,
                    out var stagedPlan, out var scopeErrors))
            {
                _logger.LogWarning("Staged plan blocked before what-if: {Errors}", scopeErrors);
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = scopeErrors;
                return response;
            }

            var userIdClaim = ServiceTools.GetUserId(context);
            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                response.ErrorMessage = "Access denied.";
                return response;
            }
            var userId = new Guid(userIdClaim);

            Guid subscriptionId;
            try
            {
                subscriptionId = new Guid(request.SubscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception occurred.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "Bad request.";
                return response;
            }

            var subModel = await GetSubscriptionInfo(userId, subscriptionId);
            if (subModel == null)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                response.ErrorMessage = "No subscription is found.";
                return response;
            }

            var parametersJson = string.IsNullOrWhiteSpace(request.Parameters) ? "{}" : request.Parameters;

            try
            {
                var credential = new ClientSecretCredential(subModel.TenantId.ToString(),
                    subModel.ClientId.ToString(), subModel.ClientSecret);
                var armClient = new ArmClient(credential, subscriptionId.ToString());
                var subResource = await armClient.GetDefaultSubscriptionAsync();
                var rg = (await subResource.GetResourceGroupAsync(request.ResourceGroupName))?.Value;
                if (rg == null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    response.ErrorMessage = "Cannot find the resource group.";
                    return response;
                }

                var transport = new ArmTransport(_httpClientFactory.CreateClient("arm"), credential,
                    ArmEndpointSet.AzureCloud);
                var preview = StagedPreviewPlanner.Select(stagedPlan, request.ArmTemplate, parametersJson);
                if (preview.Unsupported)
                {
                    _logger.LogWarning("Staged plan cannot be previewed: {Caveat}", preview.Caveat);
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    response.ErrorMessage = preview.Caveat;
                    return response;
                }

                var summary = await new WhatIfClient(transport).WhatIfAsync(subscriptionId.ToString(),
                    request.ResourceGroupName, rg.Data.Location, preview.TemplateJson, preview.ParametersJson, context.CancellationToken);

                var plan = DeployPlanArtifact.Create(subscriptionId.ToString(), request.ResourceGroupName,
                    request.ArmTemplate, parametersJson);
                // Pre-deploy snapshot + inverse plan from the FULL before payloads (v2 11.4):
                // it rides the live preview so approval persists the undo trail; the plan hash
                // keeps its scope + template + parameters identity untouched.
                var revert = RevertPlan.Build(summary, preview.Caveat);
                var token = _whatIfGate.Issue(subscriptionId.ToString(), request.ResourceGroupName,
                    request.ArmTemplate, parametersJson, summary, plan.CanonicalJson, plan.PlanHash, revert.DocumentJson);

                response.StatusCode = StatusCodes.Status200OK;
                response.Status = summary.Status;
                response.Creates = summary.Creates;
                response.Modifies = summary.Modifies;
                response.Deletes = summary.Deletes;
                response.ChangesJson = summary.ToChangesJson();
                var caveats = WhatIfSummary.Caveats.ToList();
                if (preview.Caveat is not null)
                {
                    caveats.Add(preview.Caveat);
                }
                caveats.Add($"Revert plan: {revert.Entries.Count} undo entries recorded ({revert.UnrevertableCount} unrevertable). {RevertPlan.ExecutableNote}");

                response.Caveats = string.Join("\n", caveats);
                response.PlanHash = plan.PlanHash;
                response.WhatIfToken = token;
                return response;
            }
            catch (WhatIfException ex)
            {
                _logger.LogWarning(ex, "What-if failed.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = $"{ex.Code}: {ex.Message}";
                return response;
            }
            catch (ResourceGraphException ex)
            {
                _logger.LogWarning(ex, "What-if ARM call failed.");
                response.StatusCode = ex.StatusCode == 0 ? StatusCodes.Status502BadGateway : ex.StatusCode;
                response.ErrorMessage = $"{ex.Code}: {ex.Message}";
                return response;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "What-if resource lookup failed.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = $"HTTP {ex.Status}: {ex.ErrorCode}";
                return response;
            }
        }

        /// <summary>
        /// Read-only per-resource monitor for the pipeline's deployment on (subscription, RG)
        /// (v2 plan 11.2/[56]): GETs deployment + operations over ArmTransport and folds them
        /// through DeploymentOperationsParser - newest state per resource, unrecognized display
        /// statuses stay Unknown. Reads only: mutates nothing and consumes no what-if token.
        /// </summary>
        public override async Task<DeploymentOperationsResponse> GetDeploymentOperations(DeploymentOperationsRequest request, ServerCallContext context)
        {
            var response = new DeploymentOperationsResponse();

            var userIdClaim = ServiceTools.GetUserId(context);
            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                response.ErrorMessage = "Access denied.";
                return response;
            }
            var userId = new Guid(userIdClaim);

            if (!Guid.TryParse(request.SubscriptionId, out var subscriptionId) || string.IsNullOrWhiteSpace(request.ResourceGroupName))
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "Subscription and resource group are required.";
                return response;
            }

            var subModel = await GetSubscriptionInfo(userId, subscriptionId);
            if (subModel == null)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                response.ErrorMessage = "No subscription is found.";
                return response;
            }

            try
            {
                var credential = new ClientSecretCredential(subModel.TenantId.ToString(),
                    subModel.ClientId.ToString(), subModel.ClientSecret);
                var transport = new ArmTransport(_httpClientFactory.CreateClient("arm"), credential,
                    ArmEndpointSet.AzureCloud);
                var managementEndpoint = transport.Endpoints.ManagementEndpoint;
                var apiVersion = transport.Endpoints.ApiVersion;
                var scopeDiagnostics = new List<ArmDiagnostic>();

                // Staged batches are created at the scope they deploy to: a subscription batch is
                // invisible in the resource-group listing and vice versa, so the family must be
                // collected from both scopes. A scope ARM will not list degrades to a warning.
                async Task<IReadOnlyList<JsonElement>> ListScopeAsync(string deploymentsUrl, string scopeName)
                {
                    try
                    {
                        return await transport.GetListAsync($"{deploymentsUrl}?api-version={apiVersion}", "value", context.CancellationToken);
                    }
                    catch (ResourceGraphException ex)
                    {
                        scopeDiagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Warning, "DeploymentListingUnavailable",
                            $"Deployments at {scopeName} scope could not be listed (HTTP {ex.StatusCode}); the monitor covers the scopes it can read."));
                        return [];
                    }
                }

                var family = StagedDeploymentFamily.Collect(
                    await ListScopeAsync($"{managementEndpoint}/subscriptions/{subscriptionId}/providers/Microsoft.Resources/deployments", "subscription"),
                    await ListScopeAsync($"{managementEndpoint}/subscriptions/{subscriptionId}/resourcegroups/{request.ResourceGroupName}/providers/Microsoft.Resources/deployments", "resource group"));
                if (family.Count == 0)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    response.ErrorMessage = scopeDiagnostics.Count > 0
                        ? "No deployment could be confirmed for this subscription and resource group. "
                          + string.Join(Environment.NewLine, scopeDiagnostics.Select(d => $"{d.Code}: {d.Message}"))
                        : "No deployment is found for this subscription and resource group yet.";
                    return response;
                }

                response.DeploymentStatus = StagedDeploymentFamily.Fold(family);

                var rowDiagnostics = new List<ArmDiagnostic>();
                foreach (var member in family)
                {
                    try
                    {
                        var entries = await transport.GetListAsync(
                            member.OperationsUrl(managementEndpoint, subscriptionId.ToString(), request.ResourceGroupName, apiVersion),
                            "value", context.CancellationToken);
                        var (operations, diagnostics) = DeploymentOperationsParser.ParsePages(entries.Select(entry => entry.GetRawText()));
                        foreach (var operation in operations)
                        {
                            response.Operations.Add(new ResourceOperationRow
                            {
                                ResourceKey = operation.ResourceKey,
                                State = operation.State.ToString(),
                                StatusCode = operation.StatusCode ?? "",
                                TimestampUtc = operation.TimestampUtc?.ToString("O") ?? "",
                            });
                        }

                        rowDiagnostics.AddRange(diagnostics);
                    }
                    catch (ResourceGraphException ex)
                    {
                        rowDiagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Warning, "OperationsUnavailable",
                            $"Operations for deployment '{member.Name}' ({member.Scope} scope) could not be read (HTTP {ex.StatusCode}); the folded status above stays honest."));
                    }
                }

                // Honest degradation: folded rows stay usable while listing and parser warnings are surfaced.
                response.ErrorMessage = string.Join(Environment.NewLine,
                    scopeDiagnostics.Concat(rowDiagnostics).Select(d => $"{d.Code}: {d.Message}"));
                response.StatusCode = StatusCodes.Status200OK;
                return response;
            }
            catch (ResourceGraphException ex)
            {
                _logger.LogWarning(ex, "Deployment operations lookup failed.");
                response.StatusCode = ex.StatusCode == 0 ? StatusCodes.Status502BadGateway : ex.StatusCode;
                response.ErrorMessage = $"{ex.Code}: {ex.Message}";
                return response;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "Deployment operations lookup failed.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = $"HTTP {ex.Status}: {ex.ErrorCode}";
                return response;
            }
        }

        /// <summary>
        /// Records a named approval for a plan hash. The approver principal is taken from the
        /// authenticated claims - the wire carries no approver field, so a caller can never
        /// approve in someone else's name. Only hashes backed by a live what-if preview are approvable.
        /// </summary>
        public override async Task<PlanApprovalResponse> ApprovePlan(PlanApprovalRequest request, ServerCallContext context)
        {
            var response = new PlanApprovalResponse();

            var userIdClaim = ServiceTools.GetUserId(context);
            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                _logger.LogWarning("Access denied. userIdClaim is empty.");
                response.StatusCode = StatusCodes.Status401Unauthorized;
                response.ErrorMessage = "Access denied.";
                return response;
            }

            var planHash = request.PlanHash ?? string.Empty;
            if (!planHash.StartsWith(DeployPlanArtifact.HashPrefix, StringComparison.Ordinal) || planHash.Length != 71)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "PLAN_HASH_INVALID: expected a sha256: plan hash from a What-If result.";
                return response;
            }

            if (!_whatIfGate.TryPeekPlan(planHash, out var planJson, out var revertJson))
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "WHAT_IF_STALE: no live What-If preview exists for this plan; run What-If first.";
                return response;
            }

            var displayName = context.GetHttpContext().User.FindFirst("name")?.Value;
            var principal = string.IsNullOrWhiteSpace(displayName) ? userIdClaim : $"{displayName} <{userIdClaim}>";

            var existing = await _designContext.AdsPlanApprovals.FindAsync([planHash], context.CancellationToken);
            if (existing != null)
                _designContext.AdsPlanApprovals.Remove(existing);

            var approval = new PlanApprovalModel
            {
                PlanHash = planHash,
                PlanJson = planJson,
                Status = PlanApprovalGate.ApprovedStatus,
                ApprovedBy = principal,
                ApprovedByUserId = userIdClaim,
                ApprovedAtUtc = DateTime.UtcNow,
                RevertPlanJson = revertJson,
            };
            _designContext.AdsPlanApprovals.Add(approval);
            await _designContext.SaveChangesAsync(context.CancellationToken);

            _logger.LogInformation("Plan approved hash={PlanHash} by {Principal}", planHash, principal);

            response.StatusCode = StatusCodes.Status200OK;
            response.PlanHash = planHash;
            response.ApprovedBy = principal;
            response.ApprovedAt = approval.ApprovedAtUtc.ToString("O");
            return response;
        }

        public override async Task CreateDeployment(DeploymentRequest request, IServerStreamWriter<DeploymentResponse> responseStream, ServerCallContext context)
        {
            var response = new DeploymentResponse();

            if (string.IsNullOrWhiteSpace(request.ArmTemplate))
            {
                _logger.LogWarning("ArmTemplate is empty");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "Template is empty.";
                await responseStream.WriteAsync(response);
                return;
            }

            if (ArmTemplateValidator.TryFindBlockingErrors(request.ArmTemplate, request.Parameters, out var validationErrors))
            {
                _logger.LogWarning("Template blocked before deploy: {Errors}", validationErrors);
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = validationErrors;
                await responseStream.WriteAsync(response);
                return;
            }

            if (DeployScopePlanner.TryStageForApply(request.ArmTemplate, request.SubscriptionId, request.ResourceGroupName,
                    out var stagedPlan, out var scopeErrors))
            {
                _logger.LogWarning("Staged plan blocked before deploy: {Errors}", scopeErrors);
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = scopeErrors;
                await responseStream.WriteAsync(response);
                return;
            }

            var userIdClaim = ServiceTools.GetUserId(context);
            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                _logger.LogWarning("Access denied. userIdClaim is empty.");
                response.StatusCode = StatusCodes.Status401Unauthorized;
                response.ErrorMessage = "Access denied.";
                await responseStream.WriteAsync(response);
                return;
            }
            var userId = new Guid(userIdClaim);

            Guid subscriptionId;
            try
            {
                subscriptionId = new Guid(request.SubscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception occurred.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "Bad request.";
                await responseStream.WriteAsync(response);
                return;
            }

            var subResource = await GetSubscriptionResource(userId, subscriptionId);
            if (subResource == null)
            {
                _logger.LogWarning("No subscription is found.");
                response.StatusCode = StatusCodes.Status404NotFound;
                response.ErrorMessage = "No subscription is found.";
                await responseStream.WriteAsync(response);
                return;
            }

            var rg = (await subResource.GetResourceGroupAsync(request.ResourceGroupName))?.Value;
            if (rg == null)
            {
                _logger.LogWarning("{ResourceGroup} cannot be found.", request.ResourceGroupName);
                response.StatusCode = StatusCodes.Status404NotFound;
                response.ErrorMessage = "Cannot find the resource group.";
                await responseStream.WriteAsync(response);
                return;
            }

            // Mutation gate [62]: a deploy runs only the exact plan a named principal approved from a
            // fresh what-if. Order matters: the approval checks are read-only, so a denied attempt
            // never burns the single-use what-if token; the token is consumed last.
            var parametersJson = string.IsNullOrWhiteSpace(request.Parameters) ? "{}" : request.Parameters;

            string planHash;
            try
            {
                planHash = DeployPlanArtifact.Create(subscriptionId.ToString(), request.ResourceGroupName,
                    request.ArmTemplate, parametersJson).PlanHash;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
            {
                _logger.LogWarning(ex, "Deploy payload is not canonicalizable.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "Template or parameters are not valid JSON.";
                await responseStream.WriteAsync(response);
                return;
            }

            if (!string.Equals(request.PlanHash, planHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Deploy payload differs from approved plan {PlanHash}", planHash);
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "PLAN_APPROVAL_REQUIRED: the deployed payload differs from the approved plan hash. Run What-If and approve this exact template and parameters.";
                await responseStream.WriteAsync(response);
                return;
            }

            var approval = await _designContext.AdsPlanApprovals.FindAsync([planHash], context.CancellationToken);
            if (approval == null)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = "PLAN_NOT_APPROVED: this plan has no approval. Approve the What-If result before deploying.";
                await responseStream.WriteAsync(response);
                return;
            }

            if (!string.Equals(approval.ApprovedByUserId, userIdClaim, StringComparison.Ordinal))
            {
                _logger.LogWarning("Plan {PlanHash} approved by {Approver} but deployed by a different principal", planHash, approval.ApprovedBy);
                response.StatusCode = StatusCodes.Status403Forbidden;
                response.ErrorMessage = "PLAN_APPROVER_MISMATCH: only the principal that approved this plan may deploy it.";
                await responseStream.WriteAsync(response);
                return;
            }

            var planCheck = PlanApprovalGate.Evaluate(approval.Status, approval.PlanJson, approval.PlanHash, approval.ApprovedBy);
            if (!planCheck.Cleared)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ErrorMessage = $"PLAN_{planCheck.Reason}: {planCheck.Detail}";
                await responseStream.WriteAsync(response);
                return;
            }

            var whatIfCheck = _whatIfGate.ValidateAndConsume(request.WhatIfToken, subscriptionId.ToString(),
                request.ResourceGroupName, request.ArmTemplate, parametersJson);
            if (!whatIfCheck.IsOk)
            {
                _logger.LogWarning("Deploy blocked by what-if gate: {Code}", whatIfCheck.Code);
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.DeploymentStatus = "failed";
                response.ErrorMessage = $"{whatIfCheck.Code}: {whatIfCheck.Detail}";
                await responseStream.WriteAsync(response);
                return;
            }

            // Consume the approval: one apply per approval, re-deploy needs a fresh review.
            // The apply trail (v2 plan [62]/11.3) is the append-only AdsPlanApplyEvents stream:
            // Started - canonical plan body + hash of the consumed preview token, written before
            // any ARM mutation - then exactly one true terminal event per applyId from ARM's own
            // state. A staged apply runs one applyId per batch, so every deployment's trail stands
            // alone. Transport failure is never a provisioning outcome: see the polling loop.
            var previewEvidence = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.WhatIfToken)));

            // Deploy the template. Transport and provisioning are separate concerns: the client
            // stream is best-effort, the ARM LRO is the only source of terminal truth.
            var batches = stagedPlan.Batches;
            if (batches.Count > 1)
            {
                await RunStagedApplyAsync(batches, subResource.GetArmDeployments(), rg.GetArmDeployments(),
                    approval, subscriptionId.ToString(), request.ResourceGroupName, parametersJson,
                    previewEvidence, context.CancellationToken, response, responseStream);
                return;
            }

            await ExecuteBatchAsync(rg.GetArmDeployments(), WhatIfClient.DeploymentName, request.ArmTemplate,
                parametersJson, approval, subscriptionId.ToString(), request.ResourceGroupName,
                previewEvidence, batchLabel: null, successStatus: "completed", consumesApproval: true,
                cancellationToken: context.CancellationToken, response, responseStream);
        }

        /// <summary>Prefix for client-facing messages when one batch of a staged run ended the plan.</summary>
        private static string Context(string? batchLabel) => batchLabel is null ? "" : $"{batchLabel}: ";

        /// <summary>
        /// Submits one ARM deployment - the single-scope apply or one staged batch - polls its LRO to
        /// a true terminal state, and carries the audit trail: one Started before any ARM mutation,
        /// one terminal event per applyId, durable before any client write. Returns ARM's own outcome
        /// so the staged runner can decide whether the plan may continue.
        /// </summary>
        private async Task<StagedBatchOutcome> ExecuteBatchAsync(
            ArmDeploymentCollection deployments, string deploymentName, string templateJson, string parametersJson,
            PlanApprovalModel approval, string subscriptionId, string resourceGroupName, string previewEvidence,
            string? batchLabel, string successStatus, bool consumesApproval, CancellationToken cancellationToken,
            DeploymentResponse response, IServerStreamWriter<DeploymentResponse> responseStream)
        {
            var applyId = Guid.NewGuid();
            _designContext.AdsPlanApplyEvents.Add(NewApplyEvent(applyId, approval, subscriptionId,
                resourceGroupName, PlanApplyEventModel.Started, previewEvidence, batchLabel, deploymentName));
            if (consumesApproval)
            {
                _designContext.AdsPlanApprovals.Remove(approval);
            }

            await _designContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Plan apply started hash={PlanHash} approver={ApprovedBy} target={Subscription}/{ResourceGroup} deployment={DeploymentName} applyId={ApplyId}",
                approval.PlanHash, approval.ApprovedBy, subscriptionId, resourceGroupName, deploymentName, applyId);

            var input = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(templateJson),
                Parameters = BinaryData.FromString(parametersJson),
            });

            ArmOperation<ArmDeploymentResource> lro;
            try
            {
                lro = await deployments.CreateOrUpdateAsync(WaitUntil.Started, deploymentName, input);
            }
            catch (RequestFailedException ex)
            {
                // Only ARM's own 4xx answer proves the submission was never accepted;
                // transport failures, 408 and 5xx may still have landed at ARM.
                var rejected = PlanApplyEventModel.ProvesSubmissionRejected(ex.Status);
                // The terminal event lands first - the client may already be gone.
                await RecordApplyEventAsync(applyId, approval, subscriptionId, resourceGroupName,
                    rejected ? PlanApplyEventModel.Failed : PlanApplyEventModel.OutcomeUnknown,
                    rejected ? $"submission refused: HTTP {ex.Status}: {ex.ErrorCode}"
                             : $"submission outcome unconfirmable: HTTP {ex.Status}: {ex.ErrorCode}",
                    deploymentName);
                if (rejected)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    response.DeploymentStatus = "failed";
                    response.ErrorMessage = $"{Context(batchLabel)}HTTP {ex.Status}: {ex.ErrorCode}";
                }
                else
                {
                    // "failed" here would be a second lie: ARM's answer was ambiguous.
                    response.StatusCode = StatusCodes.Status202Accepted;
                    response.DeploymentStatus = "unknown";
                    response.ErrorMessage = $"{Context(batchLabel)}Outcome unconfirmable (HTTP {ex.Status}: {ex.ErrorCode}). The deployment may still be running - verify its state in Azure (audit apply {applyId}).";
                }

                try { await responseStream.WriteAsync(response); }
                catch (Exception wex)
                {
                    _logger.LogWarning(wex, "Deploy submission-failure write failed for apply {ApplyId}; the outcome is already recorded durably", applyId);
                }

                return rejected
                    ? new StagedBatchOutcome(StagedBatchResult.Failed, response.ErrorMessage)
                    : new StagedBatchOutcome(StagedBatchResult.OutcomeUnknown, response.ErrorMessage);
            }
            catch (Exception ex)
            {
                // The submission may still have reached ARM; the outcome is unknown, not failed.
                await RecordApplyEventAsync(applyId, approval, subscriptionId, resourceGroupName,
                    PlanApplyEventModel.OutcomeUnknown, $"submission outcome unconfirmable: {ex.GetType().Name}: {ex.Message}", deploymentName);
                // Rethrowing would only reach a gRPC status the client maps to a generic error;
                // the drawer awaits this stream and must hear the honest state instead.
                _logger.LogError(ex, "Deployment submission failed unconfirmably for apply {ApplyId}", applyId);
                try
                {
                    response.StatusCode = StatusCodes.Status202Accepted;
                    response.DeploymentStatus = "unknown";
                    response.ErrorMessage = $"{Context(batchLabel)}Submission outcome unconfirmable ({ex.GetType().Name}). The deployment may still have been accepted - verify its state in Azure (audit apply {applyId}).";
                    await responseStream.WriteAsync(response);
                }
                catch (Exception wex)
                {
                    _logger.LogWarning(wex, "Deploy unknown-outcome write failed for apply {ApplyId}; the outcome is already recorded durably", applyId);
                }

                return new StagedBatchOutcome(StagedBatchResult.OutcomeUnknown, response.ErrorMessage);
            }

            var clientGone = false;
            var consecutivePollFailures = 0;
            while (!lro.HasCompleted)
            {
                if (!clientGone)
                {
                    try
                    {
                        response.StatusCode = StatusCodes.Status200OK;
                        response.DeploymentStatus = "processing";
                        await responseStream.WriteAsync(response);
                    }
                    catch (Exception ex)
                    {
                        // A dead stream says nothing about provisioning: the LRO keeps running and
                        // ARM keeps being polled for the true outcome. This is not a Failed event.
                        clientGone = true;
                        _logger.LogWarning(ex, "Deploy stream write failed for apply {ApplyId}; continuing ARM polling without a client", applyId);
                        await RecordApplyEventAsync(applyId, approval, subscriptionId, resourceGroupName,
                            PlanApplyEventModel.TransportLost, $"client stream lost mid-deployment ({ex.GetType().Name}); ARM polling continues for the true outcome", deploymentName);
                    }
                }

                await Task.Delay(2000);
                try
                {
                    await lro.UpdateStatusAsync();
                    consecutivePollFailures = 0;
                }
                catch (RequestFailedException ex)
                {
                    // A status-query failure is not a deployment failure; tolerate transient ARM
                    // unavailability before declaring the outcome unconfirmable.
                    _logger.LogWarning(ex, "Provisioning status query failed for apply {ApplyId}", applyId);
                    if (++consecutivePollFailures >= MaxConsecutivePollFailures)
                    {
                        await RecordApplyEventAsync(applyId, approval, subscriptionId, resourceGroupName,
                            PlanApplyEventModel.OutcomeUnknown, $"provisioning state unconfirmable after {consecutivePollFailures} consecutive status-query failures: HTTP {ex.Status}: {ex.ErrorCode}", deploymentName);
                        try
                        {
                            response.StatusCode = StatusCodes.Status202Accepted;
                            response.DeploymentStatus = "unknown";
                            response.ErrorMessage = $"{Context(batchLabel)}Provisioning state unconfirmable after {consecutivePollFailures} consecutive status-query failures (HTTP {ex.Status}). The deployment may still be running - verify its state in Azure (audit apply {applyId}).";
                            await responseStream.WriteAsync(response);
                        }
                        catch (Exception wex)
                        {
                            _logger.LogWarning(wex, "Deploy unknown-outcome write failed for apply {ApplyId}; the outcome is already recorded durably", applyId);
                        }

                        return new StagedBatchOutcome(StagedBatchResult.OutcomeUnknown, response.ErrorMessage);
                    }
                }
            }

            var provisioningState = lro.Value?.Data?.Properties?.ProvisioningState?.ToString();
            var succeeded = string.Equals(provisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase);
            // Truth lands first, even when nobody is listening anymore: the durable terminal
            // event is recorded from ARM's state alone, never gated on a client write.
            await RecordApplyEventAsync(applyId, approval, subscriptionId, resourceGroupName,
                succeeded ? PlanApplyEventModel.Completed : PlanApplyEventModel.Failed,
                succeeded ? $"provisioningState={provisioningState}" : $"Deployment finished with provisioningState={provisioningState ?? "Unknown"}.",
                deploymentName);

            if (!clientGone)
            {
                try
                {
                    if (succeeded)
                    {
                        response.StatusCode = StatusCodes.Status200OK;
                        response.DeploymentStatus = successStatus;
                    }
                    else
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        response.DeploymentStatus = "failed";
                        response.ErrorMessage = $"{Context(batchLabel)}Deployment finished with provisioningState={provisioningState ?? "Unknown"}.";
                    }

                    await responseStream.WriteAsync(response);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Deploy final write failed for apply {ApplyId}; the true outcome is already recorded durably", applyId);
                }
            }

            return succeeded
                ? new StagedBatchOutcome(StagedBatchResult.Completed)
                : new StagedBatchOutcome(StagedBatchResult.Failed, $"provisioningState={provisioningState ?? "Unknown"}");
        }

        /// <summary>
        /// Staged apply (v2 plan 11.2): deploys the approved plan's scope batches in planner order -
        /// the subscription-scope batch under the approved subscription, the resource-group batch into
        /// the approved group - stopping at the first batch that fails or whose outcome ARM cannot
        /// confirm. Each batch is its own ARM deployment with its own audit trail; the client stream
        /// sees batch markers between submissions and one plan-level terminal after the last batch.
        /// </summary>
        private async Task RunStagedApplyAsync(
            IReadOnlyList<DeployScopeBatch> batches, ArmDeploymentCollection subscriptionDeployments,
            ArmDeploymentCollection resourceGroupDeployments, PlanApprovalModel approval, string subscriptionId,
            string resourceGroupName, string parametersJson, string previewEvidence, CancellationToken cancellationToken,
            DeploymentResponse response, IServerStreamWriter<DeploymentResponse> responseStream)
        {
            var total = batches.Count;
            var result = await StagedBatchRunner.RunAsync(batches,
                async (batch, index) => await ExecuteBatchAsync(
                    batch.Kind == DeployScopeKind.Subscription ? subscriptionDeployments : resourceGroupDeployments,
                    $"{WhatIfClient.DeploymentName}-b{index + 1}",
                    batch.TemplateJson,
                    DeployScopePlanner.ParametersForBatch(batch.TemplateJson, parametersJson),
                    approval, subscriptionId, resourceGroupName, previewEvidence,
                    batchLabel: $"Batch {index + 1}/{total} ({batch.Kind})",
                    successStatus: "batch-completed",
                    consumesApproval: index == 0,
                    cancellationToken, response, responseStream),
                onBatchStarted: async (batch, index) =>
                {
                    try
                    {
                        response.StatusCode = StatusCodes.Status200OK;
                        response.DeploymentStatus = "batch-started";
                        response.ErrorMessage = $"Batch {index + 1}/{total} ({batch.Kind})";
                        await responseStream.WriteAsync(response);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Batch marker write failed; the client stream is best-effort");
                    }
                });

            if (result.Result != StagedBatchResult.Completed)
            {
                // ExecuteBatchAsync already wrote the honest terminal for the batch that ended the plan.
                _logger.LogWarning("Staged apply stopped at batch {FailedBatchIndex}/{Total}: {Result} {Detail}",
                    result.FailedBatchIndex + 1, total, result.Result, result.Detail);
                return;
            }

            try
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.DeploymentStatus = "completed";
                response.ErrorMessage = $"Staged apply completed: {total} scope batches.";
                await responseStream.WriteAsync(response);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staged apply final write failed; every batch outcome is already recorded durably");
            }
        }
        /// <summary>Consecutive ARM status-query failures tolerated before the outcome is declared unconfirmable.</summary>
        private const int MaxConsecutivePollFailures = 5;

        private static PlanApplyEventModel NewApplyEvent(Guid applyId, PlanApprovalModel approval, string subscriptionId,
            string resourceGroupName, string eventType, string? previewEvidence, string? detail, string? deploymentName = null) => new()
        {
            ApplyId = applyId,
            PlanHash = approval.PlanHash,
            PlanJson = approval.PlanJson,
            SubscriptionId = subscriptionId,
            ResourceGroupName = resourceGroupName,
            DeploymentName = deploymentName ?? WhatIfClient.DeploymentName,
            ApprovedBy = approval.ApprovedBy,
            ApprovedByUserId = approval.ApprovedByUserId,
            ApprovedAtUtc = approval.ApprovedAtUtc,
            PreviewEvidence = previewEvidence,
            RevertPlanHash = RevertPlan.HashOf(approval.RevertPlanJson),
            EventType = eventType,
            Detail = PlanApplyEventModel.Truncate(detail),
            TimestampUtc = DateTime.UtcNow,
        };

        private async Task RecordApplyEventAsync(Guid applyId, PlanApprovalModel approval, string subscriptionId,
            string resourceGroupName, string eventType, string? detail, string? deploymentName = null)
        {
            try
            {
                _designContext.AdsPlanApplyEvents.Add(NewApplyEvent(applyId, approval, subscriptionId,
                    resourceGroupName, eventType, null, detail, deploymentName));
                // The terminal event must land even when the caller's token is already cancelled
                // (client disconnect); audit failures are logged loudly but never mask the outcome.
                await _designContext.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "AUDIT WRITE FAILED: apply {ApplyId} event {EventType} plan {PlanHash} outcome not recorded durably",
                    applyId, eventType, approval.PlanHash);
            }
        }

        public override async Task<GetRgsResponse> GetResourceGroups(GetRgsRequest request, ServerCallContext context)
        {
            var response = new GetRgsResponse();

            var userIdClaim = ServiceTools.GetUserId(context);
            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                _logger.LogWarning("Access denied. userIdClaim is empty.");
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return response;
            }
            var userId = new Guid(userIdClaim);

            Guid subscriptionId;
            try
            {
                subscriptionId = new Guid(request.SubscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception occurred.");
                response.StatusCode = StatusCodes.Status400BadRequest;
                return response;
            }

            var subResource = await GetSubscriptionResource(userId, subscriptionId);
            if (subResource == null)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return response;
            }

            await foreach (var rg in subResource.GetResourceGroups())
            {
                response.ResourceGroupName.Add(rg.Data.Name);
            }
            response.StatusCode = StatusCodes.Status200OK;

            return response;
        }
    }
}
