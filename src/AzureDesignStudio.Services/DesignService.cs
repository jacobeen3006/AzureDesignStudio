using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Services
{
    public class DesignService : Design.DesignBase
    {
        private const int MaxDesignsPerUser = 10;
        private readonly DesignDbContext designContext = null!;
        private readonly ILogger<DesignService> logger;
        public DesignService(DesignDbContext context, ILogger<DesignService> logger)
        {
            designContext = context;

            this.logger = logger;
        }

        public override async Task<SaveDesignResponse> Save(SaveDesignRequest request, ServerCallContext context)
        {
            var response = new SaveDesignResponse();
            // The caller comes first, before request validation. A bad identity answers in-band.
            if (!ServiceTools.TryGetCaller(context, out var caller, out var failure))
            {
                logger.LogWarning("Access denied. {Failure}", failure);
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return response;
            }
            var userId = caller.UserId;

            // Validate input
            if (!ServiceTools.IsValidName(request.Name))
            {
                logger.LogWarning("{RequestName} is invalid, from user: {UserId}", request.Name, userId);
                response.StatusCode = StatusCodes.Status400BadRequest;
                return response;
            }

            var design = designContext.AdsDesigns.FirstOrDefault(d => d.UserId == userId && d.Name == request.Name);
            if (design == null)
            {
                // The quota limits how many designs a user holds, so it gates creation only:
                // checked before the lookup, it also refused to save changes to a design the
                // user already had once they reached the limit.
                var count = designContext.AdsDesigns.Where(d => d.UserId == userId).Count();
                if (count >= MaxDesignsPerUser)
                {
                    logger.LogInformation("Hit quota limit. User: {UserId}", userId);
                    response.StatusCode = StatusCodes.Status507InsufficientStorage;
                    return response;
                }

                logger.LogInformation("Save new design. Name: {DesignName}", request.Name);
                design = new DesignModel
                {
                    Name = request.Name,
                    UserId = userId,
                    DesignData = request.Data,
                    LastModified = DateTime.Now,
                };
                designContext.AdsDesigns.Add(design);
            }
            else
            {
                logger.LogInformation("Update existing design. Name: {DesignName}", request.Name);
                design.DesignData = request.Data;
                design.LastModified = DateTime.Now;
                designContext.AdsDesigns.Update(design);
            }

            try
            {
                await designContext.SaveChangesAsync();
                response.StatusCode = StatusCodes.Status200OK;
            }
            catch (DbUpdateException dbex)
            {
                logger.LogWarning(dbex, "DbUpdateException occurred.");
                response.StatusCode = StatusCodes.Status400BadRequest;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Exception occurred.");
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            return response;
        }

        public override Task<GetSavedDesignResponse> GetSaved(Empty request, ServerCallContext context)
        {
            var response = new GetSavedDesignResponse();
            // A bad identity answers in-band, never with an RpcException: the client reads
            // Unauthenticated as "offline, retry later" and would queue the call forever.
            if (!ServiceTools.TryGetCaller(context, out var caller, out var failure))
            {
                logger.LogWarning("Access denied. {Failure}", failure);
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.FromResult(response);
            }
            var userId = caller.UserId;

            var names = designContext.AdsDesigns.Where(d => d.UserId == userId).Select(d => d.Name);
            response.Names.AddRange(names);
            response.StatusCode = StatusCodes.Status200OK;

            return Task.FromResult(response);
        }

        public override async Task<LoadDesignResponse> Load(LoadDesignRequest request, ServerCallContext context)
        {
            var response = new LoadDesignResponse();
            if (!ServiceTools.TryGetCaller(context, out var caller, out var failure))
            {
                logger.LogWarning("Access denied. {Failure}", failure);
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return response;
            }
            var userId = caller.UserId;

            if (!ServiceTools.IsValidName(request.Name))
            {
                logger.LogWarning("{RequestName} is invalid, from user: {UserId}", request.Name, userId);
                response.StatusCode = StatusCodes.Status400BadRequest;
                return response;
            }

            // Protobuf string setters reject null, so a missing design must not be assigned.
            var data = (await designContext.AdsDesigns.AsNoTracking()
                .FirstOrDefaultAsync(d => d.UserId == userId && d.Name == request.Name))?.DesignData;
            if (string.IsNullOrEmpty(data))
            {
                logger.LogWarning("{RequestName} is not found, from user: {UserId}", request.Name, userId);
                response.StatusCode = StatusCodes.Status404NotFound;
            }
            else
            {
                response.Data = data;
                response.StatusCode = StatusCodes.Status200OK;
            }

            return response;
        }

        public override async Task<DeleteDesignResponse> Delete(DeleteDesignRequest request, ServerCallContext context)
        {
            var response = new DeleteDesignResponse();
            if (!ServiceTools.TryGetCaller(context, out var caller, out var failure))
            {
                logger.LogWarning("Access denied. {Failure}", failure);
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return response;
            }
            var userId = caller.UserId;

            if (!ServiceTools.IsValidName(request.Name))
            {
                logger.LogWarning("{RequestName} is invalid, from user: {UserId}", request.Name, userId);
                response.StatusCode = StatusCodes.Status400BadRequest;
                return response;
            }

            var design = await designContext.AdsDesigns.FirstOrDefaultAsync(d => d.UserId == userId && d.Name == request.Name);
            if (design == null)
            {
                logger.LogWarning("{RequestName} is not found, from user: {UserId}", request.Name, userId);
                response.StatusCode = StatusCodes.Status404NotFound;
                return response;
            }

            try
            {
                designContext.AdsDesigns.Remove(design);
                await designContext.SaveChangesAsync();
                response.StatusCode = StatusCodes.Status200OK;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Exception occurred.");
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            return response;
        }
    }
}
