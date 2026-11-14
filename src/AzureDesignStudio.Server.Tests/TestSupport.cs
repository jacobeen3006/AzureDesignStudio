using System.Net;
using System.Security.Claims;
using System.Text;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.Services;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Server.Tests;

/// <summary>Fixed tenants for tests. Consumer is the tenant every personal Microsoft account shares.</summary>
internal static class TestTenants
{
    public static readonly Guid Contoso = new("c0a70c0a-0000-4000-8000-000000000001");
    public static readonly Guid Fabrikam = new("fab71ca0-0000-4000-8000-000000000002");
    public static readonly Guid Consumer = new("9188040d-6c67-4c5b-b112-36a304b66dad");
}

/// <summary>
/// A <see cref="ServerCallContext"/> for calling a gRPC service class directly. The services
/// read the caller from <c>UserState[ServiceTools.UserStateKey]</c>, where the server's
/// interceptor (or the desktop's in-process invoker) puts it.
/// </summary>
internal sealed class TestServerCallContext : ServerCallContext
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    private readonly Dictionary<object, object> _userState = new();
    private readonly Metadata _requestHeaders = [];
    private readonly Metadata _responseTrailers = [];

    private TestServerCallContext(ClaimsPrincipal user) =>
        _userState[AzureDesignStudio.Services.ServiceTools.UserStateKey] = user;

    /// <summary>A signed-in caller with the object id and tenant claims as Microsoft.Identity.Web
    /// maps them, optionally with roles and the single-user host marker. The tenant defaults to
    /// <see cref="TestTenants.Contoso"/>.</summary>
    public static ServerCallContext ForUser(
        Guid userId, string? displayName = null, Guid? tenantId = null,
        IEnumerable<string>? roles = null, bool singleUserHost = false)
    {
        var claims = new List<Claim>
        {
            new(ObjectIdClaim, userId.ToString()),
            new(TenantIdClaim, (tenantId ?? TestTenants.Contoso).ToString()),
        };
        if (displayName is not null)
            claims.Add(new Claim("name", displayName));
        foreach (var role in roles ?? [])
            claims.Add(new Claim(ClaimTypes.Role, role));
        var context = new TestServerCallContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        if (singleUserHost)
            context._userState[AzureDesignStudio.Services.ServiceTools.SingleUserHostKey] = true;
        return context;
    }

    /// <summary>A personal Microsoft account: the shared personal-account tenant.</summary>
    public static ServerCallContext ForPersonalAccount(Guid userId, string? displayName = null) =>
        ForUser(userId, displayName, TestTenants.Consumer);

    /// <summary>The Desktop host's caller: marked as the single-user host.</summary>
    public static ServerCallContext ForSingleUserHost(Guid userId, string? displayName = null) =>
        ForUser(userId, displayName, singleUserHost: true);

    /// <summary>A token with an object id but no tenant claim.</summary>
    public static ServerCallContext WithoutTenant(Guid userId, string? displayName = null)
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
    public static DesignDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<DesignDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);
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

/// <summary>One request a <see cref="RouteTableHttp"/> saw: the query keeps its leading '?'
/// (empty when there is none) and the body is empty for a request without content.</summary>
internal sealed record RecordedRequest(string Method, string Path, string Query, string Body);

/// <summary>What a route answers: a status and a JSON body.</summary>
internal sealed record ScriptedAnswer(HttpStatusCode Status, string Body = "");

/// <summary>
/// Scripted HTTP as a route table, for RPCs that call ARM through the factory's HttpClient.
/// A route is a method plus an exact path (no query), matched case-insensitively - ARM ids
/// come as both /resourceGroups/ and /resourcegroups/. The route added last for a method and
/// path wins, so a test can override the defaults its class scripted. A route answers a status
/// and body, or waits on a task first while honouring the request's cancellation token (a hang
/// that only a deadline or the caller can end). Every request is recorded - method, path,
/// query, body - whether or not a route matched it; an unrouted one answers 501, which no ARM
/// call reads as "not found".
/// </summary>
internal sealed class RouteTableHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly object _lock = new();
    private readonly List<(HttpMethod Method, string Path, Func<RecordedRequest, CancellationToken, Task<ScriptedAnswer>> Respond)> _routes = [];
    private readonly List<RecordedRequest> _requests = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    /// <summary>Every request so far, in the order they arrived.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    /// <summary>The requests made to one method and path (case-insensitive).</summary>
    public IReadOnlyList<RecordedRequest> To(HttpMethod method, string path) =>
        [.. Requests.Where(r => r.Method == method.Method && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))];

    public RouteTableHttp Answer(HttpMethod method, string path, HttpStatusCode status, string body = "") =>
        Answer(method, path, (_, _) => Task.FromResult(new ScriptedAnswer(status, body)));

    /// <summary>A route that waits for <paramref name="release"/> before it answers; cancelling the
    /// request's token ends the wait with an <see cref="OperationCanceledException"/>.</summary>
    public RouteTableHttp Hold(HttpMethod method, string path, Task release,
        HttpStatusCode status = HttpStatusCode.OK, string body = "") =>
        Answer(method, path, async (_, cancellationToken) =>
        {
            await release.WaitAsync(cancellationToken);
            return new ScriptedAnswer(status, body);
        });

    public RouteTableHttp Answer(HttpMethod method, string path, Func<RecordedRequest, CancellationToken, Task<ScriptedAnswer>> respond)
    {
        lock (_lock)
            _routes.Add((method, path, respond));
        return this;
    }

    /// <summary>An ARM error body: <c>{"error":{"code":…,"message":…}}</c>.</summary>
    public static string ArmError(string code, string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { error = new { code, message } });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method.Method, uri.AbsolutePath, uri.Query, body);
        Func<RecordedRequest, CancellationToken, Task<ScriptedAnswer>>? respond;
        lock (_lock)
        {
            _requests.Add(recorded);
            respond = _routes.LastOrDefault(r => r.Method == request.Method
                && string.Equals(r.Path, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase)).Respond;
        }

        var answer = respond is null
            ? new ScriptedAnswer(HttpStatusCode.NotImplemented, ArmError("NoScriptedRoute", $"No route for {request.Method} {uri.AbsolutePath}."))
            : await respond(recorded, cancellationToken);
        return new HttpResponseMessage(answer.Status)
        {
            RequestMessage = request,
            Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
        };
    }
}
