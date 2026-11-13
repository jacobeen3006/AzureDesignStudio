namespace AzureDesignStudio.Services;

/// <summary>The HTTP-style status codes the services put in their responses. The services
/// used Microsoft.AspNetCore.Http.StatusCodes, which ties them to ASP.NET Core; these are
/// the same values with no host behind them.</summary>
public static class StatusCodes
{
    public const int Status200OK = 200;
    public const int Status202Accepted = 202;
    public const int Status400BadRequest = 400;
    public const int Status401Unauthorized = 401;
    public const int Status403Forbidden = 403;
    public const int Status404NotFound = 404;
    public const int Status409Conflict = 409;
    public const int Status412PreconditionFailed = 412;
    public const int Status500InternalServerError = 500;
    public const int Status502BadGateway = 502;
    public const int Status503ServiceUnavailable = 503;
    public const int Status507InsufficientStorage = 507;
}