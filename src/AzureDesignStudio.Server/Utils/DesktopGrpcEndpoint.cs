using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace AzureDesignStudio.Server.Utils;

/// <summary>
/// The Desktop host talks native gRPC over cleartext HTTP/2 (h2c). ALPN needs TLS, so a
/// cleartext endpoint cannot serve HTTP/1.1 and HTTP/2 on one port; Desktop gets its own
/// loopback listener.
///
/// Kestrel treats any endpoint defined in code as the complete endpoint set and drops the
/// URL-configured ones ("Overriding address(es) ..."). Calling <c>ListenLocalhost</c> on
/// its own therefore leaves the server reachable only on the loopback h2c port: no browser
/// endpoint, nothing on the container port. <see cref="Add"/> re-binds the hosting URLs
/// next to the listener so both stay up.
/// </summary>
public static class DesktopGrpcEndpoint
{
    public const string PortKey = "DesktopGrpc:Port";
    public const int DefaultDevelopmentPort = 5204;

    /// <summary>The loopback h2c port, or 0 when the listener is off. On by default in
    /// Development only; a deployed server has no Desktop client on its loopback
    /// interface. <c>DesktopGrpc:Port</c> overrides either way (0 disables).</summary>
    public static int ResolvePort(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[PortKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!int.TryParse(configured, out var port) || port is < 0 or > 65535)
                throw new InvalidOperationException(
                    $"{PortKey} must be a TCP port between 0 and 65535 (0 disables the listener); got '{configured}'.");
            return port;
        }

        return environment.IsDevelopment() ? DefaultDevelopmentPort : 0;
    }

    public static void Add(KestrelServerOptions options, IConfiguration configuration, int port)
    {
        var addresses = HostingAddresses(configuration);

        foreach (var address in addresses)
        {
            void Configure(ListenOptions listen)
            {
                if (string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    listen.UseHttps();
            }

            if (address.IsUnixPipe)
                options.ListenUnixSocket(address.UnixPipePath, Configure);
            else if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                options.ListenLocalhost(address.Port, Configure);
            else if (IPAddress.TryParse(address.Host, out var ip))
                options.Listen(ip, address.Port, Configure);
            else
                options.ListenAnyIP(address.Port, Configure);
        }

        // A hosting URL already on this port wins; binding it twice would fail startup.
        if (addresses.All(a => a.IsUnixPipe || a.Port != port))
            options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http2);
    }

    /// <summary>The addresses the host would have bound had no endpoint been defined in
    /// code: <c>urls</c> (ASPNETCORE_URLS, --urls, launch profile), else the
    /// <c>http_ports</c>/<c>https_ports</c> shorthand the container images use, else
    /// Kestrel's default.</summary>
    internal static IReadOnlyList<BindingAddress> HostingAddresses(IConfiguration configuration)
    {
        var urls = Split(configuration[WebHostDefaults.ServerUrlsKey]).ToList();
        if (urls.Count == 0)
        {
            urls.AddRange(Split(configuration[WebHostDefaults.HttpPortsKey]).Select(p => $"http://*:{p}"));
            urls.AddRange(Split(configuration[WebHostDefaults.HttpsPortsKey]).Select(p => $"https://*:{p}"));
        }

        if (urls.Count == 0)
            urls.Add("http://localhost:5000");

        return urls.Select(BindingAddress.Parse).ToList();
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
