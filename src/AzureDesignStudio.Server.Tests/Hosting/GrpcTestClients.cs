using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureDesignStudio.Server.Tests.Hosting;

internal static class GrpcTestClients
{
    /// <summary>
    /// A gRPC channel over the TestServer handler. With a <paramref name="user"/> every call
    /// carries the test-user header; <paramref name="withScope"/> false asks the test handler
    /// for a token without the scope claim. Dispose the channel with <c>using</c>.
    /// </summary>
    public static GrpcChannel CreateGrpcChannel(
        this WebApplicationFactory<Program> factory, Guid? user = null, bool withScope = true)
    {
        var client = factory.CreateDefaultClient(new TestUserHandler(user, withScope));
        return GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = client,
            DisposeHttpClient = true,
        });
    }

    private sealed class TestUserHandler(Guid? user, bool withScope) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (user is { } id)
            {
                request.Headers.Add(TestAuthHandler.UserHeader, id.ToString());
                if (!withScope)
                    request.Headers.Add(TestAuthHandler.NoScopeHeader, "1");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
