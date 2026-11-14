using System.Security.Claims;
using System.Threading.Channels;
using Grpc.Core;

namespace AzureDesignStudio.Services.InProcess
{
    /// <summary>
    /// A gRPC <see cref="CallInvoker"/> that calls the service classes directly, in this
    /// process, instead of over a network. Generated clients (<c>Deploy.DeployClient</c> and the
    /// rest) are built on it unchanged, so every caller written against the server works the
    /// same in the desktop app - which has no server.
    ///
    /// Each call gets its own dependency-injection scope (a fresh DbContext, as a request would
    /// on the server), a <see cref="ServerCallContext"/> carrying the caller, and the same
    /// failure semantics as the wire: an exception the service lets escape reaches the client
    /// as <see cref="RpcException"/> with <see cref="StatusCode.Unknown"/>.
    /// </summary>
    public sealed class InProcessCallInvoker : CallInvoker
    {
        private readonly IServiceProvider _services;
        private readonly Func<CancellationToken, ValueTask<ClaimsPrincipal>> _caller;
        private readonly Dictionary<string, Func<IServiceProvider, MethodCapture>> _binders = new(StringComparer.Ordinal);

        /// <param name="services">Root provider; the service classes are resolved from a scope of it.</param>
        /// <param name="caller">The identity each call runs as (the signed-in user).</param>
        public InProcessCallInvoker(IServiceProvider services, Func<CancellationToken, ValueTask<ClaimsPrincipal>> caller)
        {
            _services = services;
            _caller = caller;
        }

        /// <summary>Registers one gRPC service: its full name (<c>Deploy.Descriptor.FullName</c>)
        /// and the generated <c>BindService</c>. <typeparamref name="TService"/> is resolved
        /// from DI per call.</summary>
        public InProcessCallInvoker Add<TService>(string serviceName, Action<ServiceBinderBase, TService> bindService)
            where TService : class
        {
            _binders[serviceName] = scope =>
            {
                var capture = new MethodCapture();
                bindService(capture, scope.GetRequiredService<TService>());
                return capture;
            };
            return this;
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            AsyncUnaryCall(method, host, options, request).ResponseAsync.GetAwaiter().GetResult();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var status = Status.DefaultSuccess;
            var response = Run();
            return new AsyncUnaryCall<TResponse>(response, Task.FromResult(new Metadata()), () => status, () => [], () => { });

            async Task<TResponse> Run()
            {
                try
                {
                    await using var scope = _services.CreateAsyncScope();
                    var handler = Bind(scope.ServiceProvider, method).Unary<TRequest, TResponse>(method.FullName);
                    var context = await CreateContextAsync(method, options);
                    return await handler(request, context);
                }
                catch (Exception e)
                {
                    var failure = AsRpc(e, options.CancellationToken);
                    status = failure.Status;
                    throw failure;
                }
            }
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
            var channel = Channel.CreateUnbounded<TResponse>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var status = Status.DefaultSuccess;

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = _services.CreateAsyncScope();
                    var handler = Bind(scope.ServiceProvider, method).ServerStreaming<TRequest, TResponse>(method.FullName);
                    var context = await CreateContextAsync(method, options.WithCancellationToken(cancellation.Token));
                    await handler(request, new ChannelStreamWriter<TResponse>(channel.Writer), context);
                    channel.Writer.TryComplete();
                }
                catch (Exception e)
                {
                    var failure = AsRpc(e, cancellation.Token);
                    status = failure.Status;
                    channel.Writer.TryComplete(failure);
                }
            });

            return new AsyncServerStreamingCall<TResponse>(new ChannelStreamReader<TResponse>(channel.Reader),
                Task.FromResult(new Metadata()), () => status, () => [], () => cancellation.Cancel());
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new RpcException(new Status(StatusCode.Unimplemented, "Client streaming is not used by these services."));

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new RpcException(new Status(StatusCode.Unimplemented, "Duplex streaming is not used by these services."));

        private MethodCapture Bind<TRequest, TResponse>(IServiceProvider scope, Method<TRequest, TResponse> method)
            where TRequest : class where TResponse : class =>
            _binders.TryGetValue(method.ServiceName, out var bind)
                ? bind(scope)
                : throw new RpcException(new Status(StatusCode.Unimplemented, $"Service '{method.ServiceName}' is not available in this app."));

        private async Task<ServerCallContext> CreateContextAsync<TRequest, TResponse>(Method<TRequest, TResponse> method, CallOptions options)
            where TRequest : class where TResponse : class
        {
            var caller = await _caller(options.CancellationToken);
            return new InProcessServerCallContext(method.FullName, caller, options);
        }

        // The wire's behaviour: RpcExceptions pass through, a cancelled call is Cancelled, any
        // other exception is Unknown - the client never sees the handler's exception type.
        private static RpcException AsRpc(Exception e, CancellationToken cancellation) => e switch
        {
            RpcException rpc => rpc,
            OperationCanceledException when cancellation.IsCancellationRequested => new RpcException(new Status(StatusCode.Cancelled, "The call was cancelled.")),
            _ => new RpcException(new Status(StatusCode.Unknown, "Exception was thrown by handler. " + e.Message, e)),
        };

        /// <summary>Collects the handlers a generated <c>BindService</c> registers.</summary>
        private sealed class MethodCapture : ServiceBinderBase
        {
            private readonly Dictionary<string, object> _handlers = new(StringComparer.Ordinal);

            public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, UnaryServerMethod<TRequest, TResponse>? handler) =>
                _handlers[method.FullName] = handler!;

            public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ServerStreamingServerMethod<TRequest, TResponse>? handler) =>
                _handlers[method.FullName] = handler!;

            public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ClientStreamingServerMethod<TRequest, TResponse>? handler) { }

            public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, DuplexStreamingServerMethod<TRequest, TResponse>? handler) { }

            public UnaryServerMethod<TRequest, TResponse> Unary<TRequest, TResponse>(string fullName)
                where TRequest : class where TResponse : class =>
                _handlers.TryGetValue(fullName, out var h) && h is UnaryServerMethod<TRequest, TResponse> unary
                    ? unary
                    : throw new RpcException(new Status(StatusCode.Unimplemented, $"Method '{fullName}' is not available in this app."));

            public ServerStreamingServerMethod<TRequest, TResponse> ServerStreaming<TRequest, TResponse>(string fullName)
                where TRequest : class where TResponse : class =>
                _handlers.TryGetValue(fullName, out var h) && h is ServerStreamingServerMethod<TRequest, TResponse> streaming
                    ? streaming
                    : throw new RpcException(new Status(StatusCode.Unimplemented, $"Method '{fullName}' is not available in this app."));
        }

        private sealed class ChannelStreamWriter<T>(ChannelWriter<T> writer) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions { get; set; }

            public Task WriteAsync(T message) => writer.WriteAsync(message).AsTask();
        }

        private sealed class ChannelStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
        {
            public T Current { get; private set; } = default!;

            public async Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                // A stream that ended with an error rethrows it here, as the wire does.
                if (!await reader.WaitToReadAsync(cancellationToken))
                {
                    await reader.Completion;
                    return false;
                }

                if (!reader.TryRead(out var item))
                    return await MoveNext(cancellationToken);
                Current = item;
                return true;
            }
        }
    }

    /// <summary>The <see cref="ServerCallContext"/> of an in-process call: the caller in
    /// <see cref="ServiceTools.UserStateKey"/>, the client's cancellation and deadline. The
    /// in-process invoker exists only in the single-user Desktop host, so this is the one
    /// place that sets <see cref="ServiceTools.SingleUserHostKey"/>; the server never does.</summary>
    public sealed class InProcessServerCallContext : ServerCallContext
    {
        private readonly Dictionary<object, object> _userState = new();
        private readonly CallOptions _options;
        private readonly string _method;
        private readonly Metadata _responseTrailers = [];

        public InProcessServerCallContext(string method, ClaimsPrincipal caller, CallOptions options = default)
        {
            _method = method;
            _options = options;
            _userState[ServiceTools.UserStateKey] = caller;
            _userState[ServiceTools.SingleUserHostKey] = true;
        }

        protected override string MethodCore => _method;
        protected override string HostCore => "in-process";
        protected override string PeerCore => "in-process";
        protected override DateTime DeadlineCore => _options.Deadline ?? DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => _options.Headers ?? [];
        protected override CancellationToken CancellationTokenCore => _options.CancellationToken;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);
        protected override IDictionary<object, object> UserStateCore => _userState;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException("In-process calls do not propagate to child calls.");

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
