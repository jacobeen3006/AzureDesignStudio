using AzureDesignStudio.SharedModels.Protos;
using Grpc.Net.ClientFactory;
using Google.Protobuf.WellKnownTypes;

namespace AzureDesignStudio.Services
{
    public class DeployGrpcService
    {
        private readonly Deploy.DeployClient _deployClient = null!;
        private readonly GrpcClientFactory _clientFactory = null!;
        public DeployGrpcService(GrpcClientFactory grpcClientFactory)
        {
            _clientFactory = grpcClientFactory;
            _deployClient = _clientFactory.CreateClient<Deploy.DeployClient>("DeployClientWithAuth");
        }

        // A call that never produced an answer: server down, no token, refused at the transport.
        // The drawers await these from OnInitializedAsync and click handlers, where an escaping
        // exception leaves a spinner on screen for good; the wrappers answer instead.
        private static bool IsCallFailure(Exception e) =>
            e is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException;

        /// <summary>True when the last <see cref="GetLinkedSubscriptions"/> could not be answered -
        /// its null then means "unknown", not "none linked".</summary>
        public bool LinkedSubscriptionsUnknown { get; private set; }

        public async Task<IList<SubscriptionRes>?> GetLinkedSubscriptions()
        {
            try
            {
                var response = await _deployClient.LoadSubscriptionInfoAsync(new Empty());
                LinkedSubscriptionsUnknown = response.StatusCode != 200;
                return response.StatusCode == 200 ? response.SubscriptionData?.ToList() : null;
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                LinkedSubscriptionsUnknown = true;
                return null;
            }
        }

        /// <returns>The server's status, or 503 when the server could not be reached.</returns>
        public async Task<int> LinkAzureSubscription(SubscriptionInfo subscriptionInfo)
        {
            try
            {
                var response = await _deployClient.SaveSubscriptionInfoAsync(subscriptionInfo);
                return response.StatusCode;
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                return 503;
            }
        }

        /// <returns>The resource group names, or null when they could not be listed (the server
        /// could not be reached, or Azure refused the linked credential). An empty list is a
        /// subscription that really has none.</returns>
        public async Task<IList<string>?> GetResourceGroups(string subscriptionId)
        {
            try
            {
                var response = await _deployClient.GetResourceGroupsAsync(
                    new GetRgsRequest
                    {
                        SubscriptionId = subscriptionId
                    });

                return response.StatusCode == 200 ? response.ResourceGroupName.ToList() : null;
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                return null;
            }
        }

        public async Task<WhatIfResponse?> WhatIfAsync(string subscriptionId, string rgName, string armTemplate, string parameters,
            DeployTarget target = DeployTarget.ResourceGroup, string managementGroupId = "", string location = "")
        {
            var request = new DeploymentRequest
            {
                SubscriptionId = subscriptionId,
                ResourceGroupName = rgName,
                ArmTemplate = armTemplate,
                Parameters = parameters,
            };
            ApplyTarget(request, target, managementGroupId, location);

            return await _deployClient.WhatIfAsync(request);
        }

        /// <summary>Fills the v2 11.2 target fields. The subscription id always travels - it names
        /// the credential even for management-group and tenant targets; scope-irrelevant fields
        /// stay empty so the server never sees a half-named identity.</summary>
        private static void ApplyTarget(DeploymentRequest request, DeployTarget target, string managementGroupId, string location)
        {
            request.Target = target;
            if (target == DeployTarget.ManagementGroup)
                request.ManagementGroupId = managementGroupId;
            if (target != DeployTarget.ResourceGroup)
                request.Location = location;
        }

        /// <summary>Null when the approval could not be recorded. Nothing has been deployed at
        /// that point - approval precedes the apply - so the caller can say so.</summary>
        public async Task<PlanApprovalResponse?> ApprovePlanAsync(string planHash)
        {
            try
            {
                return await _deployClient.ApprovePlanAsync(new PlanApprovalRequest { PlanHash = planHash });
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                return null;
            }
        }

        /// <summary>Reads the server-side do-not-touch list (v2 13.1). Empty json = no policy.</summary>
        public async Task<GuardrailPolicyResponse?> GetGuardrailPolicyAsync()
        {
            return await _deployClient.GetGuardrailPolicyAsync(new Empty());
        }

        /// <summary>Replaces the server-side do-not-touch list; non-200 carries the rejection reason.</summary>
        public async Task<GuardrailPolicyResponse?> SetGuardrailPolicyAsync(string json)
        {
            return await _deployClient.SetGuardrailPolicyAsync(new GuardrailPolicyRequest { Json = json });
        }

        /// <summary>Deploys the reviewed payload as a deployment-stack (v2 12.3): the stack what-if token
        /// (<see cref="StackWhatIfAsync"/>) and plan hash the review produced; resources the stack stops managing are detached
        /// (kept in Azure) - delete is refused server-side until the Phase 13 gates ship. <paramref name="bypassOutOfSync"/> is
        /// the review-gated out-of-sync override, only ever sent after the review screen showed
        /// the managed list and the user ticked the box.</summary>
        public async Task<CreateStackResponse?> CreateStackAsync(string subscriptionId, string rgName, string stackName,
            string armTemplate, string parameters, string whatIfToken, string planHash, string resourcesAction = "detach",
            bool bypassOutOfSync = false)
        {
            try
            {
                return await _deployClient.CreateStackAsync(new CreateStackRequest
                {
                    SubscriptionId = subscriptionId,
                    ResourceGroupName = rgName,
                    StackName = stackName,
                    ArmTemplate = armTemplate,
                    Parameters = parameters,
                    WhatIfToken = whatIfToken,
                    PlanHash = planHash,
                    ResourcesAction = resourcesAction,
                    BypassOutOfSync = bypassOutOfSync,
                });
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                // A transport break never proves what ARM did: the server audit carries the truth.
                return null;
            }
        }

        /// <summary>Deployment-stacks what-if: the preview whose token (and plan hash, which the approval
        /// binds) <see cref="CreateStackAsync"/> needs. Same stack fields as the create call; nothing is
        /// deployed. Null on a transport break - the review then has no token and cannot deploy.</summary>
        public async Task<StackWhatIfResponse?> StackWhatIfAsync(string subscriptionId, string rgName, string stackName,
            string armTemplate, string parameters, string resourcesAction = "detach", bool bypassOutOfSync = false)
        {
            try
            {
                return await _deployClient.StackWhatIfAsync(new StackWhatIfRequest
                {
                    SubscriptionId = subscriptionId,
                    ResourceGroupName = rgName,
                    StackName = stackName,
                    ArmTemplate = armTemplate,
                    Parameters = parameters,
                    ResourcesAction = resourcesAction,
                    BypassOutOfSync = bypassOutOfSync,
                });
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                return null;
            }
        }

        /// <summary>Read-only stack probe for the review screen (v2 12.3 closer, [61]): exists,
        /// managed list, and the server-computed managed-vs-planned diff for the exact template
        /// about to be approved. Transport break or refusal is null - availability is then
        /// UNKNOWN and must never be rendered as "new".</summary>
        public async Task<StackProbe?> GetStackAsync(string subscriptionId, string rgName, string stackName, string plannedTemplateJson)
        {
            try
            {
                var response = await _deployClient.GetStackAsync(new GetStackRequest
                {
                    SubscriptionId = subscriptionId,
                    ResourceGroupName = rgName,
                    StackName = stackName,
                    PlannedTemplateJson = plannedTemplateJson,
                });
                if (response.StatusCode != 200)
                    return null;
                return new StackProbe(response.Exists,
                    response.ManagedResourceIds, response.Diagnostics,
                    response.AlreadyManagedCount, response.TakingOver, response.Releasing,
                    response.PlannedDiagnostics);
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                // Same swallow convention as CreateStackAsync: a break proves nothing about ARM.
                return null;
            }
        }

        public async Task<DeploymentOperationsResponse?> GetDeploymentOperationsAsync(string subscriptionId, string rgName,
            DeployTarget target = DeployTarget.ResourceGroup, string managementGroupId = "")
        {
            var request = new DeploymentOperationsRequest
            {
                SubscriptionId = subscriptionId,
                ResourceGroupName = rgName,
                Target = target,
            };
            if (target == DeployTarget.ManagementGroup)
                request.ManagementGroupId = managementGroupId;

            return await _deployClient.GetDeploymentOperationsAsync(request);
        }

        public async Task<PrepareRetryResponse?> PrepareRetryAsync(string armTemplate, IEnumerable<string> failedResourceKeys)
        {
            var request = new PrepareRetryRequest { TemplateJson = armTemplate };
            request.ResourceKeys.AddRange(failedResourceKeys);

            return await _deployClient.PrepareRetryAsync(request);
        }

        public async Task CreateDeployment(string subscriptionId, string rgName, string armTemplate, string parameters,
            string whatIfToken, string planHash,
            Func<string, string, Task> updateStatus,
            DeployTarget target = DeployTarget.ResourceGroup, string managementGroupId = "", string location = "")
        {
            var request = new DeploymentRequest
            {
                SubscriptionId = subscriptionId,
                ResourceGroupName = rgName,
                ArmTemplate = armTemplate,
                Parameters = parameters,
                WhatIfToken = whatIfToken,
                PlanHash = planHash,
            };
            ApplyTarget(request, target, managementGroupId, location);

            // The callback is awaited: as an Action it was handed an async lambda, i.e. an
            // async void whose exceptions nobody could observe.
            var submitted = false;
            try
            {
                using var deploymentResponse = _deployClient.CreateDeployment(request);
                var responseStream = deploymentResponse.ResponseStream;
                while (await responseStream.MoveNext(default))
                {
                    submitted = true;
                    var response = responseStream.Current;
                    if (response.DeploymentStatus == "unknown")
                    {
                        // The server could not confirm ARM's outcome: not a failure - the truth
                        // lives in Azure until verified. Surface it as such, not via the error gate.
                        await updateStatus("unknown", response.ErrorMessage);
                        return;
                    }
                    if (response.StatusCode != 200)
                    {
                        // Something bad happening
                        await updateStatus("error", response.ErrorMessage);
                        return;
                    }

                    await updateStatus(response.DeploymentStatus, response.ErrorMessage);
                }
            }
            catch (Exception e) when (e is Grpc.Core.RpcException or HttpRequestException)
            {
                // Before the first message nothing reached ARM as far as this client knows.
                // After it, the deployment is running and only the stream died: its outcome
                // is unknown, never "failed", and never a status dialog stuck on "started".
                await updateStatus(submitted ? "unknown" : "error", submitted
                    ? "The connection to the server was lost while the deployment was running. It may still be in progress - check its state in Azure."
                    : "The deployment could not be started: the server is not reachable or refused the request.");
            }
        }
    }

    /// <summary>One stack probe's answer (v2 12.3): null StackProbe from the wrapper means
    /// "unknown", this record only ever carries a 200 - an honest exists / managed / diff.</summary>
    public sealed record StackProbe(bool Exists, IReadOnlyList<string> ManagedIds, IReadOnlyList<string> Diagnostics,
        int AlreadyManagedCount, IReadOnlyList<string> TakingOver, IReadOnlyList<string> Releasing,
        IReadOnlyList<string> PlannedDiagnostics);
}
