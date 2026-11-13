using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

// The client's side of the same gRPC boundary: the wrappers every drawer calls. They live
// in the client assembly, which this project already reaches through the Server; a client
// test project of its own would add nothing these tests need.

internal static class FakeCalls
{
    public static AsyncUnaryCall<T> Unary<T>(Func<T> respond)
    {
        Task<T> response;
        try
        {
            response = Task.FromResult(respond());
        }
        catch (Exception e)
        {
            response = Task.FromException<T>(e);
        }

        return new AsyncUnaryCall<T>(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    public static RpcException Rpc(StatusCode code) => new(new Status(code, code.ToString()));
}

internal sealed class FixedClientFactory(object client) : GrpcClientFactory
{
    public override TClient CreateClient<TClient>(string name) => (TClient)client;
}

internal sealed class FakeDesignClient : Design.DesignClient
{
    /// <summary>What the server holds.</summary>
    public Dictionary<string, string> Stored { get; } = new();

    /// <summary>When set, every call fails with it (the server is down, the token is gone).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Per-design refusal: status the server answers a save with instead of storing.</summary>
    public Dictionary<string, int> Refuse { get; } = new();

    /// <summary>Per-design transport fault on save.</summary>
    public Dictionary<string, Exception> Throw { get; } = new();

    public List<string> SaveCalls { get; } = [];

    private T Guard<T>(Func<T> body) => Failure is null ? body() : throw Failure;

    public override AsyncUnaryCall<SaveDesignResponse> SaveAsync(SaveDesignRequest request, CallOptions options) =>
        FakeCalls.Unary(() => Guard(() =>
        {
            SaveCalls.Add(request.Name);
            if (Throw.TryGetValue(request.Name, out var fault))
                throw fault;
            if (Refuse.TryGetValue(request.Name, out var status))
                return new SaveDesignResponse { StatusCode = status };
            Stored[request.Name] = request.Data;
            return new SaveDesignResponse { StatusCode = 200 };
        }));

    public override AsyncUnaryCall<GetSavedDesignResponse> GetSavedAsync(Empty request, CallOptions options) =>
        FakeCalls.Unary(() => Guard(() =>
        {
            var response = new GetSavedDesignResponse { StatusCode = 200 };
            response.Names.AddRange(Stored.Keys);
            return response;
        }));

    public override AsyncUnaryCall<LoadDesignResponse> LoadAsync(LoadDesignRequest request, CallOptions options) =>
        FakeCalls.Unary(() => Guard(() => Stored.TryGetValue(request.Name, out var data)
            ? new LoadDesignResponse { StatusCode = 200, Data = data }
            : new LoadDesignResponse { StatusCode = 404 }));

    public override AsyncUnaryCall<DeleteDesignResponse> DeleteAsync(DeleteDesignRequest request, CallOptions options) =>
        FakeCalls.Unary(() => Guard(() => new DeleteDesignResponse { StatusCode = Stored.Remove(request.Name) ? 200 : 404 }));
}

internal sealed class MemoryOfflineStore : IDesignOfflineStore
{
    public Dictionary<string, string> Pending { get; } = new();

    public Task SaveAsync(string name, string data)
    {
        Pending[name] = data;
        return Task.CompletedTask;
    }

    public Task<string?> TryLoadAsync(string name) => Task.FromResult(Pending.GetValueOrDefault(name));

    public Task<IList<string>> ListAsync() => Task.FromResult<IList<string>>([.. Pending.Keys.Order()]);

    public Task DeleteAsync(string name)
    {
        Pending.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IList<(string Name, string Data)>> ListPendingAsync() =>
        Task.FromResult<IList<(string Name, string Data)>>([.. Pending.Select(p => (p.Key, p.Value))]);

    public Task ClearPendingAsync(string name) => DeleteAsync(name);
}

public class DesignGrpcServiceTests
{
    private readonly FakeDesignClient _server = new();
    private readonly MemoryOfflineStore _store = new();

    /// <summary>The Desktop app: saves fall back to the local workspace.</summary>
    private DesignGrpcService Desktop => new(new FixedClientFactory(_server), [_store]);

    /// <summary>The web client: there is nowhere to fall back to.</summary>
    private DesignGrpcService Web => new(new FixedClientFactory(_server), []);

    // --- web client: a failed call is an answer, never an exception ---

    [Theory]
    [InlineData(StatusCode.Unavailable, 503)]
    [InlineData(StatusCode.Internal, 503)]
    [InlineData(StatusCode.Unauthenticated, 401)]
    [InlineData(StatusCode.PermissionDenied, 403)]
    [InlineData(StatusCode.ResourceExhausted, 413)] // a design over the server's message limit
    public async Task WebClientTurnsAFailedCallIntoAStatus(StatusCode failure, int expected)
    {
        // These used to escape into the drawers and leave them loading for good.
        _server.Failure = FakeCalls.Rpc(failure);
        var service = Web;

        Assert.Equal(expected, await service.SaveDesign("net", "{}"));
        Assert.Equal((expected, (IList<string>?)null), await service.GetAllSavedDesign());
        Assert.Equal((expected, (string?)null), await service.LoadDesign("net"));
        Assert.Equal(expected, await service.DeleteDesign("net"));
        Assert.False(service.LastCallWasOffline);
    }

    [Fact]
    public async Task WebClientSurvivesAPlainNetworkFailure()
    {
        _server.Failure = new HttpRequestException("connection refused");

        Assert.Equal(503, await Web.SaveDesign("net", "{}"));
    }

    [Fact]
    public void EveryFailureStatusHasWords()
    {
        foreach (var status in new[] { 401, 403, 404, 413, 503, 507 })
            Assert.DoesNotContain(status.ToString(), DesignGrpcService.Describe(status));

        Assert.Contains("418", DesignGrpcService.Describe(418)); // unknown ones still say what came back
    }

    // --- Desktop: nothing is lost while the server cannot take it ---

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unauthenticated)] // signed out is not a reason to lose the work
    public async Task SaveTheServerCannotTakeIsKeptLocally(StatusCode failure)
    {
        _server.Failure = FakeCalls.Rpc(failure);
        var service = Desktop;

        Assert.Equal(200, await service.SaveDesign("net", """{"v":1}"""));

        Assert.True(service.LastCallWasOffline);
        Assert.Equal("""{"v":1}""", _store.Pending["net"]);
        Assert.Empty(_server.Stored);
    }

    [Fact]
    public async Task PendingSavesAreSentOnceTheServerIsBack()
    {
        _server.Failure = FakeCalls.Rpc(StatusCode.Unavailable);
        await Desktop.SaveDesign("first", "1");
        await Desktop.SaveDesign("second", "2");
        _server.Failure = null;

        await Desktop.GetAllSavedDesign(); // any successful call flushes

        Assert.Equal(new Dictionary<string, string> { ["first"] = "1", ["second"] = "2" }, _server.Stored);
        Assert.Empty(_store.Pending);
    }

    [Fact]
    public async Task RefusedSaveKeepsTheOnlyCopy()
    {
        // The server reports a refusal in the response, not as an exception. The local copy
        // used to be cleared for any response at all - a hard delete of the only copy.
        _store.Pending["net"] = "local edits";
        _server.Refuse["net"] = 507;

        var status = await Desktop.SaveDesign("net", "local edits");

        Assert.Equal(507, status);
        Assert.Equal("local edits", _store.Pending["net"]);
    }

    [Fact]
    public async Task RefusedDesignStaysPendingWhileTheOthersSync()
    {
        _store.Pending["too many"] = "a";
        _store.Pending["fine"] = "b";
        _server.Refuse["too many"] = 507;

        await Desktop.GetAllSavedDesign();

        Assert.Equal("b", _server.Stored["fine"]);
        Assert.Equal(new[] { "too many" }, _store.Pending.Keys);
    }

    [Fact]
    public async Task OneDesignTheServerFaultsOnDoesNotStopTheFlushOrFailTheCall()
    {
        // A fault while flushing used to throw out of the save or list that started the
        // flush - a call that had itself already succeeded.
        _store.Pending["broken"] = "a";
        _store.Pending["fine"] = "b";
        _server.Throw["broken"] = FakeCalls.Rpc(StatusCode.Internal);

        var (status, names) = await Desktop.GetAllSavedDesign();

        Assert.Equal(200, status);
        Assert.NotNull(names);
        Assert.Equal("b", _server.Stored["fine"]);
        Assert.Equal(new[] { "broken" }, _store.Pending.Keys);
    }

    [Fact]
    public async Task FlushStopsQuietlyWhenTheServerGoesAwayAgain()
    {
        _store.Pending["a"] = "1";
        _store.Pending["b"] = "2";
        _server.Throw["a"] = FakeCalls.Rpc(StatusCode.Unavailable);
        _server.Throw["b"] = FakeCalls.Rpc(StatusCode.Unavailable);

        var (status, _) = await Desktop.GetAllSavedDesign();

        Assert.Equal(200, status);
        Assert.Equal(2, _store.Pending.Count);
        Assert.Single(_server.SaveCalls); // no point trying the rest
    }

    // --- Desktop: list, load, delete ---

    [Fact]
    public async Task ListShowsServerAndLocalDesignsOnce()
    {
        _server.Stored["on server"] = "s";
        _server.Stored["both"] = "s";
        _store.Pending["both"] = "l";
        _server.Refuse["both"] = 507; // stays local through the flush

        var (status, names) = await Desktop.GetAllSavedDesign();

        Assert.Equal(200, status);
        Assert.Equal(new[] { "both", "on server" }, names!.Order());
    }

    [Fact]
    public async Task OfflineListIsTheLocalOne()
    {
        _store.Pending["local"] = "l";
        _server.Failure = FakeCalls.Rpc(StatusCode.Unavailable);
        var service = Desktop;

        var (status, names) = await service.GetAllSavedDesign();

        Assert.Equal(200, status);
        Assert.Equal(new[] { "local" }, names);
        Assert.True(service.LastCallWasOffline);
    }

    [Fact]
    public async Task UnsyncedLocalEditsWinOverTheServerCopy()
    {
        _server.Stored["net"] = "old";
        _store.Pending["net"] = "newer, not synced yet";

        Assert.Equal((200, "newer, not synced yet"), await Desktop.LoadDesign("net"));
    }

    [Fact]
    public async Task LoadingOfflineWhatIsNotHereAnswersNotFound()
    {
        _server.Failure = FakeCalls.Rpc(StatusCode.Unavailable);

        Assert.Equal((404, (string?)null), await Desktop.LoadDesign("never saved"));
    }

    [Fact]
    public async Task DeletingADesignThatExistsOnlyLocallySucceeds()
    {
        _store.Pending["draft"] = "never synced";

        Assert.Equal(200, await Desktop.DeleteDesign("draft"));
        Assert.Empty(_store.Pending);
    }

    [Fact]
    public async Task DeletingRemovesBothCopies()
    {
        _server.Stored["net"] = "s";
        _store.Pending["net"] = "l";

        Assert.Equal(200, await Desktop.DeleteDesign("net"));

        Assert.Empty(_server.Stored);
        Assert.Empty(_store.Pending);
    }
}

public class DeployGrpcServiceTests
{
    private sealed class FakeDeployClient : Deploy.DeployClient
    {
        public Exception? Failure { get; set; }
        public int Status { get; set; } = 200;
        public List<DeploymentResponse> Stream { get; } = [];
        public Exception? StreamFault { get; set; }

        private T Guard<T>(Func<T> body) => Failure is null ? body() : throw Failure;

        public override AsyncUnaryCall<LoadSubInfoResponse> LoadSubscriptionInfoAsync(Empty request, CallOptions options) =>
            FakeCalls.Unary(() => Guard(() =>
            {
                var response = new LoadSubInfoResponse { StatusCode = Status };
                if (Status == 200)
                    response.SubscriptionData.Add(new SubscriptionRes { SubscriptionId = "s1", SubscriptionName = "Production" });
                return response;
            }));

        public override AsyncUnaryCall<SaveSubInfoResponse> SaveSubscriptionInfoAsync(SubscriptionInfo request, CallOptions options) =>
            FakeCalls.Unary(() => Guard(() => new SaveSubInfoResponse { StatusCode = Status }));

        public override AsyncUnaryCall<GetRgsResponse> GetResourceGroupsAsync(GetRgsRequest request, CallOptions options) =>
            FakeCalls.Unary(() => Guard(() => new GetRgsResponse { StatusCode = Status }));

        public override AsyncUnaryCall<PlanApprovalResponse> ApprovePlanAsync(PlanApprovalRequest request, CallOptions options) =>
            FakeCalls.Unary(() => Guard(() => new PlanApprovalResponse { StatusCode = Status }));

        public override AsyncServerStreamingCall<DeploymentResponse> CreateDeployment(DeploymentRequest request, CallOptions options) =>
            Failure is null
                ? new AsyncServerStreamingCall<DeploymentResponse>(new Reader(Stream, StreamFault),
                    Task.FromResult(new Metadata()), () => Grpc.Core.Status.DefaultSuccess, () => [], () => { })
                : throw Failure;

        private sealed class Reader(IReadOnlyList<DeploymentResponse> items, Exception? fault) : IAsyncStreamReader<DeploymentResponse>
        {
            private int _next;

            public DeploymentResponse Current { get; private set; } = new();

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                if (_next < items.Count)
                {
                    Current = items[_next++];
                    return Task.FromResult(true);
                }

                return fault is null ? Task.FromResult(false) : Task.FromException<bool>(fault);
            }
        }
    }

    private readonly FakeDeployClient _server = new();

    private DeployGrpcService Service => new(new FixedClientFactory(_server));

    private async Task<List<(string Status, string Message)>> Deploy()
    {
        var seen = new List<(string, string)>();
        await Service.CreateDeployment("s1", "rg", "{}", "{}", "token", "hash", (status, message) =>
        {
            seen.Add((status, message));
            return Task.CompletedTask;
        });
        return seen;
    }

    // --- "could not ask" is not "nothing there" ---

    [Fact]
    public async Task LinkedSubscriptionsAreListed()
    {
        var service = Service;

        var linked = await service.GetLinkedSubscriptions();

        Assert.Equal("Production", Assert.Single(linked!).SubscriptionName);
        Assert.False(service.LinkedSubscriptionsUnknown);
    }

    [Fact]
    public async Task UnreachableServerLeavesLinkedSubscriptionsUnknown()
    {
        // Read as "none linked", this offered the link form - a client secret - again.
        _server.Failure = FakeCalls.Rpc(StatusCode.Unavailable);
        var service = Service;

        Assert.Null(await service.GetLinkedSubscriptions());
        Assert.True(service.LinkedSubscriptionsUnknown);
    }

    [Fact]
    public async Task RefusedLookupLeavesLinkedSubscriptionsUnknown()
    {
        _server.Status = 401;
        var service = Service;

        Assert.Null(await service.GetLinkedSubscriptions());
        Assert.True(service.LinkedSubscriptionsUnknown);
    }

    [Fact]
    public async Task ResourceGroupsThatCouldNotBeListedAreNotAnEmptyList()
    {
        Assert.Empty((await Service.GetResourceGroups("s1"))!); // a subscription that really has none

        _server.Status = 502;
        Assert.Null(await Service.GetResourceGroups("s1"));

        _server.Failure = FakeCalls.Rpc(StatusCode.Unavailable);
        Assert.Null(await Service.GetResourceGroups("s1"));
    }

    [Fact]
    public async Task FailedCallsAnswerInsteadOfThrowing()
    {
        _server.Failure = FakeCalls.Rpc(StatusCode.Unavailable);

        Assert.Equal(503, await Service.LinkAzureSubscription(new SubscriptionInfo()));
        Assert.Null(await Service.ApprovePlanAsync("sha256:abc"));
    }

    // --- deployment stream ---

    [Fact]
    public async Task DeploymentProgressIsRelayedInOrder()
    {
        _server.Stream.Add(new DeploymentResponse { StatusCode = 200, DeploymentStatus = "started" });
        _server.Stream.Add(new DeploymentResponse { StatusCode = 200, DeploymentStatus = "processing" });
        _server.Stream.Add(new DeploymentResponse { StatusCode = 200, DeploymentStatus = "completed" });

        Assert.Equal(new[] { "started", "processing", "completed" }, (await Deploy()).Select(s => s.Status));
    }

    [Fact]
    public async Task ServerErrorEndsTheStreamAsAnError()
    {
        _server.Stream.Add(new DeploymentResponse { StatusCode = 200, DeploymentStatus = "started" });
        _server.Stream.Add(new DeploymentResponse { StatusCode = 400, ErrorMessage = "InvalidTemplate" });
        _server.Stream.Add(new DeploymentResponse { StatusCode = 200, DeploymentStatus = "completed" });

        Assert.Equal(new[] { ("started", ""), ("error", "InvalidTemplate") }, await Deploy());
    }

    [Fact]
    public async Task ConnectionLostMidDeploymentIsUnknownNotFailed()
    {
        // The deployment is running in Azure; only the stream died. Reported as a failure,
        // it invited a second deployment on top of the first; left alone, the dialog sat
        // on "started" for ever.
        _server.Stream.Add(new DeploymentResponse { StatusCode = 200, DeploymentStatus = "started" });
        _server.StreamFault = FakeCalls.Rpc(StatusCode.Unavailable);

        var seen = await Deploy();

        Assert.Equal(new[] { "started", "unknown" }, seen.Select(s => s.Status));
        Assert.Contains("may still be in progress", seen[^1].Message);
    }

    [Fact]
    public async Task DeploymentThatNeverStartedIsAnError()
    {
        _server.StreamFault = FakeCalls.Rpc(StatusCode.Unavailable);

        var only = Assert.Single(await Deploy());

        Assert.Equal("error", only.Status);
        Assert.Contains("could not be started", only.Message);
    }

    [Fact]
    public async Task ServerReportedUnknownOutcomeIsRelayedAsUnknown()
    {
        _server.Stream.Add(new DeploymentResponse { StatusCode = 502, DeploymentStatus = "unknown", ErrorMessage = "verify in Azure" });

        Assert.Equal(new[] { ("unknown", "verify in Azure") }, await Deploy());
    }
}
