using System;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AzureDesignStudio.Core.Common;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// The apply-audit event vocabulary is persisted verbatim; a constant that violates its own
/// column contract (e.g. the 6-char "Failed" under MinimumLength=7) makes every such event
/// throw at SaveChanges - invisible until the one moment the trail matters most.
/// </summary>
public class PlanApplyEventModelTests
{
    [Theory]
    [InlineData(PlanApplyEventModel.Started)]
    [InlineData(PlanApplyEventModel.Completed)]
    [InlineData(PlanApplyEventModel.Failed)]
    [InlineData(PlanApplyEventModel.TransportLost)]
    [InlineData(PlanApplyEventModel.OutcomeUnknown)]
    public void EventTypeConstants_FitThePersistedColumnContract(string eventType)
    {
        var attribute = typeof(PlanApplyEventModel)
            .GetProperty(nameof(PlanApplyEventModel.EventType))!
            .GetCustomAttribute<StringLengthAttribute>()!;

        Assert.InRange(eventType.Length, attribute.MinimumLength, attribute.MaximumLength);
    }

    [Fact]
    public void Truncate_KeepsValuesUpToTheCapVerbatim()
    {
        Assert.Null(PlanApplyEventModel.Truncate(null));
        Assert.Equal("ok", PlanApplyEventModel.Truncate("ok"));

        var exact = new string('x', PlanApplyEventModel.DetailCap);

        Assert.Equal(exact, PlanApplyEventModel.Truncate(exact));
    }

    [Fact]
    public void Truncate_CapsLongDetailsWithMarker()
    {
        var longDetail = new string('y', PlanApplyEventModel.DetailCap + 500);

        var result = PlanApplyEventModel.Truncate(longDetail);

        Assert.NotNull(result);
        Assert.Equal(PlanApplyEventModel.DetailCap, result.Length);
        Assert.EndsWith("…[trunc]", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(400, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(408, false)]
    [InlineData(409, true)]
    [InlineData(429, true)]
    [InlineData(500, false)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    public void SubmissionRejectionIsProvedOnlyByArmsOwnFourXx(int status, bool expected)
    {
        Assert.Equal(expected, PlanApplyEventModel.ProvesSubmissionRejected(status));
    }
}
