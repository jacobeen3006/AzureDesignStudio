using Azure.Core;

namespace AzureDesignStudio.Services
{
    /// <summary>A subscription the ambient identity can see.</summary>
    public sealed record AmbientSubscription(string SubscriptionId, string DisplayName);

    /// <summary>
    /// The identity of the person using the app, for hosts that have one - the desktop app,
    /// where the user is signed in to Azure on their own machine. When a host registers this,
    /// a subscription without a linked service principal is deployed to as that user, and
    /// every subscription the user can see is offered. The server registers none: there,
    /// only linked subscriptions exist, as before.
    /// </summary>
    public interface IAmbientAzureCredential
    {
        TokenCredential Credential { get; }

        /// <summary>The subscriptions the identity can see; empty when it cannot sign in silently.</summary>
        Task<IReadOnlyList<AmbientSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken);
    }
}
