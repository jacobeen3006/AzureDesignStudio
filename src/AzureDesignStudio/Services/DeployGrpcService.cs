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

        public async Task<WhatIfResponse?> WhatIfAsync(string subscriptionId, string rgName, string armTemplate, string parameters)
        {
            var request = new DeploymentRequest
            {
                SubscriptionId = subscriptionId,
                ResourceGroupName = rgName,
                ArmTemplate = armTemplate,
                Parameters = parameters,
            };

            return await _deployClient.WhatIfAsync(request);
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
        /// refused server-side until the Phase 13 gates ship.</summary>
        public async Task<CreateStackResponse?> CreateStackAsync(string subscriptionId, string rgName, string stackName,
            string armTemplate, string parameters, string whatIfToken, string planHash, string missingAction = "Detach")
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
                });
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException)
            {
                // A transport break never proves what ARM did: the server audit carries the truth.
                return null;
            }
        }

        public async Task<DeploymentOperationsResponse?> GetDeploymentOperationsAsync(string subscriptionId, string rgName)
        {
            var request = new DeploymentOperationsRequest
            {
                SubscriptionId = subscriptionId,
                ResourceGroupName = rgName,
            };

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
            Action<string, string> updateStatus)
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
}
