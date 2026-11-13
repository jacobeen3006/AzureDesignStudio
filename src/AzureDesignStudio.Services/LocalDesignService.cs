using System.Text.Json;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace AzureDesignStudio.Services
{
    /// <summary>
    /// The design service of a single-user app: designs live in a local store (the desktop's
    /// workspace database), so there is no per-user partition, no sign-in requirement and no
    /// quota - the server's ten-design limit protects a shared database, not a laptop.
    /// Status codes and name rules are the server's, so the same drawers work unchanged.
    /// </summary>
    public sealed class LocalDesignService(IDesignOfflineStore store, ILogger<LocalDesignService> logger) : Design.DesignBase
    {
        public override async Task<SaveDesignResponse> Save(SaveDesignRequest request, ServerCallContext context)
        {
            if (!ServiceTools.IsValidName(request.Name))
                return new SaveDesignResponse { StatusCode = StatusCodes.Status400BadRequest };

            // The server's database refuses a design that is not JSON (its check constraint);
            // the local store has none, so the same rule is applied here.
            try
            {
                using var _ = JsonDocument.Parse(request.Data ?? string.Empty);
            }
            catch (JsonException)
            {
                logger.LogWarning("Design {Name} is not JSON; not saved.", request.Name);
                return new SaveDesignResponse { StatusCode = StatusCodes.Status400BadRequest };
            }

            await store.SaveAsync(request.Name, request.Data!);
            return new SaveDesignResponse { StatusCode = StatusCodes.Status200OK };
        }

        public override async Task<GetSavedDesignResponse> GetSaved(Empty request, ServerCallContext context)
        {
            var response = new GetSavedDesignResponse { StatusCode = StatusCodes.Status200OK };
            response.Names.AddRange(await store.ListAsync());
            return response;
        }

        public override async Task<LoadDesignResponse> Load(LoadDesignRequest request, ServerCallContext context)
        {
            if (!ServiceTools.IsValidName(request.Name))
                return new LoadDesignResponse { StatusCode = StatusCodes.Status400BadRequest };

            var data = await store.TryLoadAsync(request.Name);
            return data is null
                ? new LoadDesignResponse { StatusCode = StatusCodes.Status404NotFound }
                : new LoadDesignResponse { StatusCode = StatusCodes.Status200OK, Data = data };
        }

        public override async Task<DeleteDesignResponse> Delete(DeleteDesignRequest request, ServerCallContext context)
        {
            if (!ServiceTools.IsValidName(request.Name))
                return new DeleteDesignResponse { StatusCode = StatusCodes.Status400BadRequest };

            if (await store.TryLoadAsync(request.Name) is null)
                return new DeleteDesignResponse { StatusCode = StatusCodes.Status404NotFound };

            await store.DeleteAsync(request.Name);
            return new DeleteDesignResponse { StatusCode = StatusCodes.Status200OK };
        }
    }
}
