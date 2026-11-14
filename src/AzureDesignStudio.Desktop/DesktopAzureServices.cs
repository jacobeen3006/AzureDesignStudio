using System.Reflection;
using Azure.Core;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.InProcess;
using Grpc.Core;
using Grpc.Net.ClientFactory;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// The signed-in user as the default Azure identity of the in-process deploy service: a
/// subscription without a linked service principal is deployed to as this user, and every
/// subscription they can see is offered alongside the linked ones.
/// </summary>
public sealed class DesktopAmbientCredential(AdsTokenBroker broker, TenantInventory inventory) : IAmbientAzureCredential
{
    public TokenCredential Credential => broker;

    public async Task<IReadOnlyList<AmbientSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken) =>
        (await inventory.ListSubscriptionsAsync(cancellationToken))
            .Select(s => new AmbientSubscription(s.SubscriptionId, s.DisplayName))
            .ToList();
}

/// <summary>
/// Keeps a linked service principal's client secret in the OS secret store (Windows Credential
/// Manager, macOS Keychain). What the deploy service stores in its database is only the name of
/// that entry, so the database file never holds a secret, and removing the entry revokes the
/// link on this machine.
/// </summary>
public sealed class CredentialStoreSecretProtector : ICryptoService
{
    private const string Prefix = "credman:";

    public Task<string> Encrypt(string plainText)
    {
        var name = "LinkedServicePrincipal-" + Guid.NewGuid().ToString("N");
        SecretStore.Save(name, "azure-design-studio", plainText);
        return Task.FromResult(Prefix + name);
    }

    public Task<string> Decrypt(string encryptedText)
    {
        if (!encryptedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException($"The stored value is not a {SecretStore.Name} reference.");

        return Task.FromResult(SecretStore.TryGet(encryptedText[Prefix.Length..])
            ?? throw new InvalidOperationException($"The linked secret is no longer in the {SecretStore.Name}; link the subscription again."));
    }
}

/// <summary>Hands out gRPC clients built on the in-process invoker, so the shared client code
/// (which asks a <see cref="GrpcClientFactory"/> for named clients) reaches the services in this
/// process. The client name does not matter: there is one destination.</summary>
public sealed class InProcessGrpcClientFactory(InProcessCallInvoker invoker) : GrpcClientFactory
{
    public override TClient CreateClient<TClient>(string name) =>
        (TClient)(Activator.CreateInstance(typeof(TClient), BindingFlags.Public | BindingFlags.Instance, null, [(CallInvoker)invoker], null)
            ?? throw new InvalidOperationException($"{typeof(TClient).Name} has no CallInvoker constructor."));
}
