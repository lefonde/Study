namespace StudyApp.Core.Maintenance;

/// <summary>One backup file, reduced to the only two things retention cares about.</summary>
public record BackupEntry(string Path, DateTime Taken);

/// <summary>
/// Decides which database backups to keep and which to delete.
///
/// Pure and dependency-free so it can be unit-tested: this is the one piece of the backup system
/// that *destroys* files, so a mistake here causes exactly the data loss the backups exist to
/// prevent. It lives in Core rather than beside the SQLite code in Web purely so those tests can
/// exist — it holds no domain knowledge and nothing else in Core depends on it.
/// </summary>
public static class BackupRetention
{
    /// <summary>
    /// Returns the entries safe to delete, keeping:
    /// <list type="bullet">
    /// <item>the newest <paramref name="keepRecent"/> overall — recent granularity;</item>
    /// <item>the first <b>and last</b> snapshot of each of the most recent
    /// <paramref name="keepDays"/> days — history.</item>
    /// </list>
    /// First as well as last, deliberately: a day can begin healthy and end damaged, and keeping
    /// only the newest of each day would throw away the good copy while carefully retaining the
    /// broken one. That is not hypothetical — it is how a day's only snapshot came to record a
    /// database that had already lost a deck of cards.
    /// </summary>
    public static IReadOnlyList<BackupEntry> SelectForDeletion(
        IEnumerable<BackupEntry> backups, int keepRecent = 10, int keepDays = 7)
    {
        var ordered = backups.OrderByDescending(b => b.Taken).ToList();
        if (ordered.Count <= keepRecent)
            return [];

        var keep = ordered.Take(keepRecent)
            .Select(b => b.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var day in ordered.GroupBy(b => b.Taken.Date)
                     .OrderByDescending(g => g.Key)
                     .Take(keepDays))
        {
            keep.Add(day.MinBy(b => b.Taken)!.Path);
            keep.Add(day.MaxBy(b => b.Taken)!.Path);
        }

        return ordered.Where(b => !keep.Contains(b.Path)).ToList();
    }
}
