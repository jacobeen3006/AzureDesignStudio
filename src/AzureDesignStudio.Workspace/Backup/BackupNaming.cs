using System.Globalization;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Workspace;

/// <summary>Why a backup was taken; part of its file name and the retention rule.</summary>
public enum BackupReason
{
    /// <summary>Requested by the user; never pruned.</summary>
    Manual,

    /// <summary>Taken by the daily timer.</summary>
    Scheduled,

    /// <summary>Taken just before a schema upgrade.</summary>
    Premigration,
}

/// <summary>A backup file that is named like one of ours.</summary>
public sealed record BackupFile(string Path, DateTimeOffset CreatedUtc, BackupReason Reason, long SizeBytes);

/// <summary>
/// <c>&lt;stem&gt;-&lt;UTC yyyyMMddTHHmmssZ&gt;-&lt;manual|scheduled|premigration&gt;.db</c>. Names are parsed only by a
/// strict anchored pattern, so nothing else in a user-writable directory is ever listed, and therefore never
/// pruned, and no path separator can match.
/// </summary>
public static class BackupNaming
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static string FileName(string stem, DateTimeOffset utc, BackupReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        return $"{stem}-{utc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{ReasonText(reason)}.db";
    }

    public static bool TryParse(string fileName, string stem, out DateTimeOffset createdUtc, out BackupReason reason)
    {
        createdUtc = default;
        reason = default;
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(stem)) return false;

        // [0-9] and \z on purpose: \d matches non-ASCII digits and $ matches before a trailing newline.
        var match = Regex.Match(fileName,
            "^" + Regex.Escape(stem) + @"-([0-9]{8}T[0-9]{6}Z)-(manual|scheduled|premigration)\.db\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;

        if (!DateTimeOffset.TryParseExact(match.Groups[1].Value, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createdUtc))
            return false;

        reason = match.Groups[2].Value switch
        {
            "manual" => BackupReason.Manual,
            "scheduled" => BackupReason.Scheduled,
            _ => BackupReason.Premigration,
        };
        return true;
    }

    internal static string ReasonText(BackupReason reason) => reason switch
    {
        BackupReason.Manual => "manual",
        BackupReason.Scheduled => "scheduled",
        BackupReason.Premigration => "premigration",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown backup reason."),
    };
}
