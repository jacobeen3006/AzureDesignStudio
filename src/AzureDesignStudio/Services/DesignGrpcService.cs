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
                    await _designClient.SaveAsync(new SaveDesignRequest { Name = name, Data = data });
                    await _offlineStore.ClearPendingAsync(name);
                }
                catch (Exception e) when (IsTransportFailure(e))
                {
                    return; // still offline; keep pending
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
                if (_offlineStore != null)
                    await _offlineStore.ClearPendingAsync(designName); // remote now holds fresher data
                await FlushPendingAsync();
                return response.StatusCode;
            }
            catch (Exception e) when (_offlineStore != null && IsTransportFailure(e))
            {
                await _offlineStore.SaveAsync(designName, data);
                LastCallWasOffline = true;
                return (int)HttpStatusCode.OK;
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
        }

        public async Task<int> DeleteDesign(string name)
        {
            var request = new DeleteDesignRequest { Name = name };
            try
            {
                var response = await _designClient.DeleteAsync(request);
                LastCallWasOffline = false;
                if (_offlineStore != null)
                    await _offlineStore.DeleteAsync(name); // remove stale local copy too
                return response.StatusCode;
            }
            catch (Exception e) when (_offlineStore != null && IsTransportFailure(e))
            {
                await _offlineStore.DeleteAsync(name);
                LastCallWasOffline = true;
                return (int)HttpStatusCode.OK;
            }
        }
    }
}
