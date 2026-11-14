using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>What a test token carries beyond the default object id and tenant.</summary>
internal sealed record TestClaims(
    Guid? Tenant = null,
    IReadOnlyList<string>? Roles = null,
    bool NoTid = false,
    bool NoOid = false,
    IReadOnlyList<(string Type, string Value)>? Extra = null);

internal static class GrpcTestClients
{
    /// <summary>
    /// A gRPC channel over the TestServer handler. With a <paramref name="user"/> every call
    /// carries the test-user header; <paramref name="withScope"/> false asks the test handler
    /// for a token without the scope claim; <paramref name="claims"/> states tenant, roles and extra claims. Dispose the channel with <c>using</c>.
    /// </summary>
    public static GrpcChannel CreateGrpcChannel(
        this WebApplicationFactory<Program> factory, Guid? user = null, bool withScope = true,
        TestClaims? claims = null)
    {
        var client = factory.CreateDefaultClient(new TestUserHandler(user, withScope, claims));
        return GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = client,
            DisposeHttpClient = true,
        });
    }

    private sealed class TestUserHandler(Guid? user, bool withScope, TestClaims? claims) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (user is { } id)
            {
                request.Headers.Add(TestAuthHandler.UserHeader, id.ToString());
                if (!withScope)
                    request.Headers.Add(TestAuthHandler.NoScopeHeader, "1");
                if (claims is not null)
                {
                    if (claims.Tenant is { } tenant)
                        request.Headers.Add(TestAuthHandler.TenantHeader, tenant.ToString());
                    if (claims.NoTid)
                        request.Headers.Add(TestAuthHandler.NoTidHeader, "1");
                    if (claims.NoOid)
                        request.Headers.Add(TestAuthHandler.NoOidHeader, "1");
                    foreach (var role in claims.Roles ?? [])
                        request.Headers.Add(TestAuthHandler.RoleHeader, role);
                    foreach (var (type, value) in claims.Extra ?? [])
                        request.Headers.Add(TestAuthHandler.ClaimHeader, $"{type}|{value}");
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
