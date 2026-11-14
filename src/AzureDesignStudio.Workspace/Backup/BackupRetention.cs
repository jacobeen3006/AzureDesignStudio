namespace AzureDesignStudio.Workspace;

/// <summary>
/// How many non-manual backups to keep and for how long premigration backups are kept regardless.
/// The numbers are constants of the policy, changeable without touching stored data.
/// <para>
/// <paramref name="KeepNewestWithData"/> protects against a recovery wiping out the restore points: after a
/// quarantine (or any unnoticed wipe) a fresh empty workspace is backed up every day, and without this rule
/// five days of backups of an empty database would push every backup that holds data out of the newest
/// five. The newest N scheduled or premigration backups that hold data are therefore kept on top of the
/// other rules (0 turns it off).
/// </para>
/// </summary>
public sealed record RetentionPolicy(int KeepNewest, TimeSpan PremigrationWindow, int KeepNewestWithData = 0)
{
    /// <summary>The newest 5, every premigration backup from the last 30 days, and the newest 2 that hold data.</summary>
    public static RetentionPolicy Default { get; } = new(5, TimeSpan.FromDays(30), 2);
}

/// <summary>What <see cref="BackupRetention.Plan"/> decided for each backup.</summary>
public sealed record RetentionPlan(IReadOnlyList<BackupFile> Keep, IReadOnlyList<BackupFile> Delete);

/// <summary>The retention rule as one pure function: no I/O, same input, same plan.</summary>
public static class BackupRetention
{
    /// <summary>
    /// Keeps every <see cref="BackupReason.Manual"/> backup, the newest <see cref="RetentionPolicy.KeepNewest"/>
    /// of the scheduled and premigration ones (by creation time descending, then file name), and every
    /// premigration backup whose age is at most the window, and the newest
    /// <see cref="RetentionPolicy.KeepNewestWithData"/> non-manual backups for which <paramref name="holdsData"/>
    /// is true (null = none do). Everything else is deleted.
    /// </summary>
    public static RetentionPlan Plan(
        IReadOnlyList<BackupFile> files, DateTimeOffset now, RetentionPolicy? policy = null,
        Func<BackupFile, bool>? holdsData = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        policy ??= RetentionPolicy.Default;

        var newest = files
            .Where(f => f.Reason != BackupReason.Manual)
            .OrderByDescending(f => f.CreatedUtc)
            .ThenBy(f => Path.GetFileName(f.Path), StringComparer.Ordinal)
            .Take(Math.Max(0, policy.KeepNewest))
            .ToHashSet();

        var withData = holdsData is null || policy.KeepNewestWithData <= 0
            ? []
            : files
                .Where(f => f.Reason != BackupReason.Manual && holdsData(f))
                .OrderByDescending(f => f.CreatedUtc)
                .ThenBy(f => Path.GetFileName(f.Path), StringComparer.Ordinal)
                .Take(policy.KeepNewestWithData)
                .ToHashSet();

        var keep = new List<BackupFile>();
        var delete = new List<BackupFile>();
        foreach (var file in files)
        {
            var kept = file.Reason == BackupReason.Manual
                || newest.Contains(file)
                || withData.Contains(file)
                || (file.Reason == BackupReason.Premigration && now - file.CreatedUtc <= policy.PremigrationWindow);
            (kept ? keep : delete).Add(file);
        }

        return new RetentionPlan(keep, delete);
    }
}
