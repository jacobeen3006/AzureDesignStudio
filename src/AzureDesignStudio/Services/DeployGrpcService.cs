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

        public async Task<IList<SubscriptionRes>?> GetLinkedSubscriptions()
        {
            IList<SubscriptionRes>? linkedSubscriptions = null;

            var response = await _deployClient.LoadSubscriptionInfoAsync(new Empty());
            if (response.StatusCode == 200)
            {
                linkedSubscriptions = response.SubscriptionData?.ToList();
            }

            return linkedSubscriptions;
        }

        public async Task<int> LinkAzureSubscription(SubscriptionInfo subscriptionInfo)
        {
            var response = await _deployClient.SaveSubscriptionInfoAsync(subscriptionInfo);

            return response.StatusCode;
        }

        public async Task<IList<string>> GetResourceGroups(string subscriptionId)
        {
            var response = await _deployClient.GetResourceGroupsAsync(
                new GetRgsRequest 
                { 
                    SubscriptionId = subscriptionId
                });

            return response.ResourceGroupName.ToList();
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

        public async Task<PlanApprovalResponse?> ApprovePlanAsync(string planHash)
        {
            return await _deployClient.ApprovePlanAsync(new PlanApprovalRequest { PlanHash = planHash });
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

        /// <summary>Deploys the reviewed payload as a deployment-stack (v2 12.3): same what-if token
        /// and plan hash the review produced; the unmanage policy stays Detach-flavored - Delete is
        /// refused server-side until the Phase 13 gates ship. <paramref name="bypassOutOfSync"/> is
        /// the review-gated out-of-sync override, only ever sent after the review screen showed
        /// the managed list and the user ticked the box.</summary>
        public async Task<CreateStackResponse?> CreateStackAsync(string subscriptionId, string rgName, string stackName,
            string armTemplate, string parameters, string whatIfToken, string planHash, string missingAction = "Detach",
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
                    MissingAction = missingAction,
                    BypassOutOfSync = bypassOutOfSync,
                });
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                // A transport break never proves what ARM did: the server audit carries the truth.
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
            Action<string, string> updateStatus,
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

            using var deploymentResponse = _deployClient.CreateDeployment(request);
            var responseStream = deploymentResponse.ResponseStream;
            while (await responseStream.MoveNext(default))
            {
                var response = responseStream.Current;
                if (response.DeploymentStatus == "unknown")
                {
                    // The server could not confirm ARM's outcome: not a failure - the truth
                    // lives in Azure until verified. Surface it as such, not via the error gate.
                    updateStatus("unknown", response.ErrorMessage);
                    return;
                }
                if (response.StatusCode != 200)
                {
                    // Something bad happening
                    updateStatus("error", response.ErrorMessage);
                    return;
                }

                updateStatus(response.DeploymentStatus, response.ErrorMessage);
            }
        }
    }

    /// <summary>One stack probe's answer (v2 12.3): null StackProbe from the wrapper means
    /// "unknown", this record only ever carries a 200 - an honest exists / managed / diff.</summary>
    public sealed record StackProbe(bool Exists, IReadOnlyList<string> ManagedIds, IReadOnlyList<string> Diagnostics,
        int AlreadyManagedCount, IReadOnlyList<string> TakingOver, IReadOnlyList<string> Releasing,
        IReadOnlyList<string> PlannedDiagnostics);
}
