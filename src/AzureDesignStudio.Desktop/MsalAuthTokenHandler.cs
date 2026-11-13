namespace AzureDesignStudio.Desktop;

/// <summary>
/// Attaches the server-API access token to gRPC calls on authenticated endpoints
/// ([Authorize] on Design/Deploy services). Requests go anonymous while the
/// user is not signed in, matching the WASM behavior before login. The token comes
/// from the host-wide <see cref="AdsTokenBroker"/> seam (plan 6.2).
/// </summary>
public sealed class MsalAuthTokenHandler(AdsTokenBroker broker) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The bearer token is a credential for the signed-in user. Cleartext is fine on the
        // loopback h2c listener (it never leaves the machine); to any other host over http it
        // would cross the network readable. Such a ServerUrl gets no token - the call then
        // answers "not signed in" instead of leaking one.
        if (MayCarryToken(request.RequestUri))
        {
            var token = await broker.GetServerTokenAsync();
            if (token != null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static bool MayCarryToken(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
