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
                var summary = await new WhatIfClient(transport).WhatIfAsync(subscriptionId.ToString(),
                    request.ResourceGroupName, rg.Data.Location, request.ArmTemplate, parametersJson, context.CancellationToken);

                var plan = DeployPlanArtifact.Create(subscriptionId.ToString(), request.ResourceGroupName,
                    request.ArmTemplate, parametersJson);
                var token = _whatIfGate.Issue(subscriptionId.ToString(), request.ResourceGroupName,
                    request.ArmTemplate, parametersJson, summary, plan.CanonicalJson, plan.PlanHash);

                response.StatusCode = StatusCodes.Status200OK;
                response.Status = summary.Status;
                response.Creates = summary.Creates;
                response.Modifies = summary.Modifies;
                response.Deletes = summary.Deletes;
                response.ChangesJson = summary.ToChangesJson();
                response.Caveats = string.Join("\n", WhatIfSummary.Caveats);
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

            if (!_whatIfGate.TryPeekPlan(planHash, out var planJson))
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
            // The apply itself is written durably to AdsPlanApplies *before* any ARM mutation
            // starts (v2 plan [62]/11.3), so consumption of the approval row never erases the
            // "who applied what, when, where" trail.
            _designContext.AdsPlanApplies.Add(new PlanApplyAuditModel
            {
                PlanHash = approval.PlanHash,
                SubscriptionId = subscriptionId.ToString(),
                ResourceGroupName = request.ResourceGroupName,
                DeploymentName = WhatIfClient.DeploymentName,
                ApprovedBy = approval.ApprovedBy,
                ApprovedByUserId = approval.ApprovedByUserId,
                ApprovedAtUtc = approval.ApprovedAtUtc,
                AppliedAtUtc = DateTime.UtcNow,
            });
            _designContext.AdsPlanApprovals.Remove(approval);
            await _designContext.SaveChangesAsync(context.CancellationToken);
            _logger.LogInformation("Plan applied hash={PlanHash} approver={ApprovedBy} user={UserId} target={Subscription}/{ResourceGroup}",
                planHash, approval.ApprovedBy, userIdClaim, subscriptionId, request.ResourceGroupName);

            // Deploy the template
            ArmDeploymentCollection armDeployments = rg.GetArmDeployments();
            string deploymentName = WhatIfClient.DeploymentName;
            var input = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(request.ArmTemplate),
                Parameters = BinaryData.FromString(parametersJson),
            });
            try
            {
                ArmOperation<ArmDeploymentResource> lro = await armDeployments.CreateOrUpdateAsync(WaitUntil.Started, deploymentName, input);

                while(!lro.HasCompleted)
                {
                    response.StatusCode = StatusCodes.Status200OK;
                    response.DeploymentStatus = "processing";
                    await responseStream.WriteAsync(response);
                    await Task.Delay(2000);
                    await lro.UpdateStatusAsync();
                }

                response.StatusCode = StatusCodes.Status200OK;
                response.DeploymentStatus = "completed";
                await responseStream.WriteAsync(response);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "Request Failed Exception");
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.DeploymentStatus = "failed";
                response.ErrorMessage = $"HTTP {ex.Status}: {ex.ErrorCode}";
                await responseStream.WriteAsync(response);
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
