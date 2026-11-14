using System.Text.Json;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Discovery.Preflight;

/// <summary>How a preflight request that threw ended (<see cref="PreflightSupport.Classify"/>).</summary>
internal enum RequestFailure { TimedOut, Forbidden, Failed }

/// <summary>
/// What the networked preflight engines (<see cref="PolicyPreflight"/>, <see cref="QuotaPreflight"/>)
/// share, so the never-throw rule, the failure codes and the deadline cannot drift between them:
/// the internal deadline, how a failed request is classified and coded, and ARM id parsing
/// (segment names matched case-insensitively).
/// </summary>
internal static class PreflightSupport
{
    /// <summary>The internal deadline: a negative budget is none at all; anything beyond what a
    /// timer can hold is no limit.</summary>
    public static TimeSpan DeadlineOf(TimeSpan budget) =>
        budget == Timeout.InfiniteTimeSpan || budget.TotalMilliseconds >= int.MaxValue ? Timeout.InfiniteTimeSpan
        : budget < TimeSpan.Zero ? TimeSpan.Zero
        : budget;

    /// <summary>
    /// How a request that threw <paramref name="e"/> ended. An engine never throws except the
    /// caller's cancellation, which is rethrown here carrying <paramref name="ct"/>. A cancellation
    /// by the engine's own deadline (<paramref name="deadlineToken"/>) is TimedOut, a 403 is
    /// Forbidden, and anything else is Failed, with the code <see cref="CodeOf"/> gives.
    /// </summary>
    public static RequestFailure Classify(Exception e, CancellationToken deadlineToken, CancellationToken ct)
    {
        if (e is OperationCanceledException && ct.IsCancellationRequested)
            throw new OperationCanceledException(e.Message, e, ct);
        if (e is OperationCanceledException && deadlineToken.IsCancellationRequested)
            return RequestFailure.TimedOut;
        return e is ResourceGraphException { StatusCode: 403 } ? RequestFailure.Forbidden : RequestFailure.Failed;
    }

    /// <summary>The ARM error code of a failed request (Throttled for a 429), else what failed.</summary>
    public static string CodeOf(Exception e) => e switch
    {
        ResourceGraphException arm => arm.Code,
        JsonException => "MalformedResponse",
        HttpRequestException => "NetworkError",
        OperationCanceledException => "Timeout",
        _ => e.GetType().Name,
    };

    public static string[] Segments(string resourceId) => resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The resource type an ARM id names: the namespace after the last
    /// <c>providers</c> segment plus every type segment after it. With
    /// <paramref name="resourceGroups"/>, a resource group's id names
    /// <c>Microsoft.Resources/resourceGroups</c>; without it, null.</summary>
    public static string? TypeOf(string? resourceId, bool resourceGroups = false)
    {
        if (resourceId is null)
            return null;
        var segments = Segments(resourceId);
        var providers = Array.FindLastIndex(segments, s => s.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (providers < 0 || providers + 2 >= segments.Length)
        {
            if (resourceGroups && segments.Length == 4 && segments[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.Resources/resourceGroups";
            return null;
        }
        var parts = new List<string> { segments[providers + 1] };
        for (var i = providers + 2; i < segments.Length; i += 2)
            parts.Add(segments[i]);
        return string.Join('/', parts);
    }

    /// <summary>A child resource's parent (two or more type/name pairs after the namespace): the
    /// id without its last two segments, casing kept; else null.</summary>
    public static string? ParentOf(string resourceId)
    {
        var segments = Segments(resourceId);
        var providers = Array.FindLastIndex(segments, s => s.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (providers < 0 || segments.Length - (providers + 2) < 4)
            return null;
        var id = resourceId.TrimEnd('/');
        for (var drop = 0; drop < 2; drop++)
            id = id[..id.LastIndexOf('/')];
        return id;
    }
}
