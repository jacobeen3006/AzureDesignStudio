using System.Net;
using System.Security.Claims;
using AzureDesignStudio.Server.Models;
using AzureDesignStudio.Server.Services;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Server.Tests;

/// <summary>
/// A <see cref="ServerCallContext"/> for calling a gRPC service class directly. The services
/// read the caller from <c>context.GetHttpContext().User</c>; outside a real request that
/// extension looks the HttpContext up in <c>UserState["__HttpContext"]</c>.
/// </summary>
internal sealed class TestServerCallContext : ServerCallContext
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private readonly Dictionary<object, object> _userState = new();
    private readonly Metadata _requestHeaders = [];
    private readonly Metadata _responseTrailers = [];

    private TestServerCallContext(ClaimsPrincipal user) =>
        _userState["__HttpContext"] = new DefaultHttpContext { User = user };

    /// <summary>A signed-in caller with the object id claim as Microsoft.Identity.Web maps it.</summary>
    public static ServerCallContext ForUser(Guid userId, string? displayName = null)
    {
        var claims = new List<Claim> { new(ObjectIdClaim, userId.ToString()) };
        if (displayName is not null)
            claims.Add(new Claim("name", displayName));
        return WithClaims([.. claims]);
    }

    public static ServerCallContext WithClaims(params Claim[] claims) =>
        new TestServerCallContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

    /// <summary>A token that carries no object id at all.</summary>
    public static ServerCallContext WithoutIdentity() => WithClaims();

    protected override string MethodCore => "test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:0";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(null, []);
    protected override IDictionary<object, object> UserStateCore => _userState;

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}

internal static class TestData
{
    /// <summary>A private in-memory database - the provider the server itself runs on in Debug.</summary>
    public static DesignDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DesignDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}

/// <summary>Stands in for Key Vault: reversible, and able to fail the two ways the real one does.</summary>
internal sealed class FakeCryptoService : ICryptoService
{
    public Exception? EncryptFailure { get; set; }
    public Exception? DecryptFailure { get; set; }

    public Task<string> Encrypt(string plainText) =>
        EncryptFailure is null ? Task.FromResult("enc:" + plainText) : Task.FromException<string>(EncryptFailure);

    public Task<string> Decrypt(string encryptedText) =>
        DecryptFailure is null ? Task.FromResult(encryptedText["enc:".Length..]) : Task.FromException<string>(DecryptFailure);
}

/// <summary>Answers HTTP calls from a script and records what was asked for.</summary>
internal sealed class ScriptedHttp(Func<Uri, string?> respond) : HttpMessageHandler, IHttpClientFactory
{
    public List<Uri> Requests { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var body = respond(request.RequestUri!);
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
