using AzureDesignStudio.SharedModels.Protos;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using AzureDesignStudio.Core.Common;
using System.Net;

namespace AzureDesignStudio.Services
{
    public class DesignGrpcService
    {
        private readonly Design.DesignClient _designClient = null!;
        private readonly GrpcClientFactory _clientFactory = null!;
        private readonly IDesignOfflineStore? _offlineStore;

        /// <summary>True when the last save/load/delete was served by the offline cache (desktop).</summary>
        public bool LastCallWasOffline { get; private set; }

        public DesignGrpcService(GrpcClientFactory grpcClientFactory, IEnumerable<IDesignOfflineStore> offlineStores)
        {
            _clientFactory = grpcClientFactory;
            _designClient = _clientFactory.CreateClient<Design.DesignClient>("DesignClientWithAuth");
            _offlineStore = offlineStores.FirstOrDefault();
        }

        private static bool IsTransportFailure(Exception e) =>
            e is RpcException { StatusCode: StatusCode.Unavailable } || e is HttpRequestException;

        // A save the server cannot take right now: transport down, or the caller is not
        // signed in. Both keep the design in the offline store; the flush retries once
        // connectivity or sign-in returns, so work is never lost to a 401.
        private static bool IsDeferredSaveFailure(Exception e) =>
            IsTransportFailure(e) || e is RpcException { StatusCode: StatusCode.Unauthenticated };

        private static bool IsCallFailure(Exception e) => e is RpcException or HttpRequestException;

        /// <summary>The HTTP-style status the callers already branch on, for a call that ended
        /// in an exception instead of a response. Without an offline store (the web client)
        /// these used to escape into the drawers and leave them loading for good.</summary>
        internal static int StatusOf(Exception e) => e switch
        {
            RpcException { StatusCode: StatusCode.Unauthenticated } => (int)HttpStatusCode.Unauthorized,
            RpcException { StatusCode: StatusCode.PermissionDenied } => (int)HttpStatusCode.Forbidden,
            // A message over the server's size limit - a very large design.
            RpcException { StatusCode: StatusCode.ResourceExhausted } => (int)HttpStatusCode.RequestEntityTooLarge,
            _ => (int)HttpStatusCode.ServiceUnavailable,
        };

        /// <summary>What a failed save, load or delete means, in words.</summary>
        public static string Describe(int statusCode) => statusCode switch
        {
            (int)HttpStatusCode.Unauthorized => "you are not signed in, or the sign-in has expired",
            (int)HttpStatusCode.Forbidden => "this account is not allowed to do that",
            (int)HttpStatusCode.NotFound => "the design was not found",
            (int)HttpStatusCode.RequestEntityTooLarge => "the design is too large for the server to accept",
            (int)HttpStatusCode.ServiceUnavailable => "the server could not be reached",
            (int)HttpStatusCode.InsufficientStorage => "the limit of saved designs is reached - delete one, or save under an existing name",
            _ => $"the server answered {statusCode}",
        };

        /// <summary>Push all pending offline saves; called when the server is reachable again. Best-effort.</summary>
        private async Task FlushPendingAsync()
        {
            if (_offlineStore == null)
                return;

            var pending = await _offlineStore.ListPendingAsync();
            foreach (var (name, data) in pending)
            {
                try
                {
                    var response = await _designClient.SaveAsync(new SaveDesignRequest { Name = name, Data = data });
                    // The server reports refusals (bad name, quota, storage error) in the
                    // response, not as an exception. Clearing is a hard delete of the only
                    // copy, so it happens for an explicit 200 and nothing else.
                    if (response.StatusCode == (int)HttpStatusCode.OK)
                        await _offlineStore.ClearPendingAsync(name);
                }
                catch (Exception e) when (IsDeferredSaveFailure(e))
                {
                    return; // still offline; keep pending
                }
                catch (Exception e) when (e is RpcException or HttpRequestException or OperationCanceledException)
                {
                    // One design the server will not take (too large, forbidden, a server fault)
                    // must not stop the others - or throw out of the save or list that started
                    // this flush, which had already succeeded. It stays pending for next time.
                }
            }
        }

        public async Task<int> SaveDesign(string designName, string data)
        {
            var design = new SaveDesignRequest
            {
                Name = designName,
                Data = data
            };

            try
            {
                var response = await _designClient.SaveAsync(design);
                LastCallWasOffline = false;
                // Only a save the server accepted supersedes the local pending copy.
                if (_offlineStore != null && response.StatusCode == (int)HttpStatusCode.OK)
                    await _offlineStore.ClearPendingAsync(designName); // remote now holds fresher data
                await FlushPendingAsync();
                return response.StatusCode;
            }
            catch (Exception e) when (_offlineStore != null && IsDeferredSaveFailure(e))
            {
                await _offlineStore.SaveAsync(designName, data);
                LastCallWasOffline = true;
                return (int)HttpStatusCode.OK;
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                LastCallWasOffline = false;
                return StatusOf(e);
            }
        }

        public async Task<(int, IList<string>?)> GetAllSavedDesign()
        {
            try
            {
                var response = await _designClient.GetSavedAsync(new Google.Protobuf.WellKnownTypes.Empty());
                LastCallWasOffline = false;
                var names = response.Names?.ToList() ?? new List<string>();
                if (_offlineStore != null)
                {
                    foreach (var local in await _offlineStore.ListAsync())
                        if (!names.Contains(local))
                            names.Add(local);
                }
                await FlushPendingAsync();
                return (response.StatusCode, names);
            }
            catch (Exception e) when (_offlineStore != null && IsTransportFailure(e))
            {
                LastCallWasOffline = true;
                return ((int)HttpStatusCode.OK, await _offlineStore.ListAsync());
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                LastCallWasOffline = false;
                return (StatusOf(e), null);
            }
        }

        public async Task<(int, string?)> LoadDesign(string name)
        {
            var request = new LoadDesignRequest
            {
                Name = name,
            };
            // Unsynced local edits always win over a stale server copy.
            if (_offlineStore != null)
            {
                var pending = await _offlineStore.TryLoadAsync(name);
                if (pending != null)
                {
                    LastCallWasOffline = true;
                    return ((int)HttpStatusCode.OK, pending);
                }
            }
            try
            {
                var response = await _designClient.LoadAsync(request);
                LastCallWasOffline = false;
                return (response.StatusCode, response.Data);
            }
            catch (Exception e) when (_offlineStore != null && IsTransportFailure(e))
            {
                var local = await _offlineStore.TryLoadAsync(name);
                LastCallWasOffline = true;
                return local != null ? ((int)HttpStatusCode.OK, local) : ((int)HttpStatusCode.NotFound, null);
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                LastCallWasOffline = false;
                return (StatusOf(e), null);
            }
        }

        public async Task<int> DeleteDesign(string name)
        {
            var request = new DeleteDesignRequest { Name = name };
            try
            {
                var response = await _designClient.DeleteAsync(request);
                LastCallWasOffline = false;
                // 404 counts: the design may exist only locally (saved offline, never synced).
                if (_offlineStore != null
                    && (response.StatusCode == (int)HttpStatusCode.OK || response.StatusCode == (int)HttpStatusCode.NotFound))
                {
                    var hadLocal = await _offlineStore.TryLoadAsync(name) != null;
                    await _offlineStore.DeleteAsync(name); // remove stale local copy too
                    if (hadLocal)
                        return (int)HttpStatusCode.OK;
                }
                return response.StatusCode;
            }
            catch (Exception e) when (_offlineStore != null && IsTransportFailure(e))
            {
                await _offlineStore.DeleteAsync(name);
                LastCallWasOffline = true;
                return (int)HttpStatusCode.OK;
            }
            catch (Exception e) when (IsCallFailure(e))
            {
                LastCallWasOffline = false;
                return StatusOf(e);
            }
        }
    }
}
