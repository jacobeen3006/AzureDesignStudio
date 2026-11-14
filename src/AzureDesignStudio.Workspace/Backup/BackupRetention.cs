namespace AzureDesignStudio.Workspace;

/// <summary>
/// How many non-manual backups to keep and for how long premigration backups are kept regardless.
/// The numbers are constants of the policy, changeable without touching stored data.
/// </summary>
public sealed record RetentionPolicy(int KeepNewest, TimeSpan PremigrationWindow)
{
    /// <summary>The newest 5, plus every premigration backup from the last 30 days.</summary>
    public static RetentionPolicy Default { get; } = new(5, TimeSpan.FromDays(30));
}

/// <summary>What <see cref="BackupRetention.Plan"/> decided for each backup.</summary>
public sealed record RetentionPlan(IReadOnlyList<BackupFile> Keep, IReadOnlyList<BackupFile> Delete);

/// <summary>The retention rule as one pure function: no I/O, same input, same plan.</summary>
public static class BackupRetention
{
    /// <summary>
    /// Keeps every <see cref="BackupReason.Manual"/> backup, the newest <see cref="RetentionPolicy.KeepNewest"/>
    /// of the scheduled and premigration ones (by creation time descending, then file name), and every
    /// premigration backup whose age is at most the window. Everything else is deleted.
    /// </summary>
    public static RetentionPlan Plan(IReadOnlyList<BackupFile> files, DateTimeOffset now, RetentionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        policy ??= RetentionPolicy.Default;

        var newest = files
            .Where(f => f.Reason != BackupReason.Manual)
            .OrderByDescending(f => f.CreatedUtc)
            .ThenBy(f => Path.GetFileName(f.Path), StringComparer.Ordinal)
            .Take(Math.Max(0, policy.KeepNewest))
            .ToHashSet();

        var keep = new List<BackupFile>();
        var delete = new List<BackupFile>();
        foreach (var file in files)
        {
            var kept = file.Reason == BackupReason.Manual
                || newest.Contains(file)
                || (file.Reason == BackupReason.Premigration && now - file.CreatedUtc <= policy.PremigrationWindow);
            (kept ? keep : delete).Add(file);
        }

        return new RetentionPlan(keep, delete);
    }
}
