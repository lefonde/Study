namespace StudyApp.Web.Data;

/// <summary>
/// Takes a database snapshot at intervals while the app is running.
///
/// The startup backup alone leaves a long session completely unprotected: a day of studying,
/// uploading and ingesting can all sit between one start and the next, and bulk ingestion makes
/// that window expensive — an extract represents real money already spent.
///
/// This gets its own <see cref="BackgroundService"/> rather than piggybacking on
/// <c>JobRunner</c>'s startup phase the way progress snapshots do: that hook runs once at boot,
/// and this needs a timer that keeps firing.
/// </summary>
public class PeriodicBackupService(
    StudyAppPaths paths,
    TimeProvider timeProvider,
    ILogger<PeriodicBackupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                if (!HasChangedSinceLastBackup())
                    continue;

                DatabaseBackup.Run(paths.DatabasePath, paths.BackupDirectory, logger);
            }
            catch (Exception ex)
            {
                // A failed periodic backup must never take the app down with it. Run() already
                // logs the failure in detail; this is the last line of defence.
                logger.LogError(ex, "Periodic backup failed");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Skips the snapshot when the database hasn't been written since the newest backup, so an
    /// app left open for days doesn't push its own real history out of the retention window
    /// with identical copies.
    /// </summary>
    private bool HasChangedSinceLastBackup()
    {
        if (!File.Exists(paths.DatabasePath))
            return false;

        if (!Directory.Exists(paths.BackupDirectory))
            return true;

        var newest = new DirectoryInfo(paths.BackupDirectory)
            .GetFiles("studyapp-*.db")
            .Max(f => (DateTime?)f.LastWriteTimeUtc);

        if (newest is null)
            return true;

        // The -wal holds writes that haven't reached the main file yet, so an active session
        // can leave studyapp.db itself untouched for a long time. Take the newest of the pair.
        var wal = paths.DatabasePath + "-wal";
        var lastWrite = File.GetLastWriteTimeUtc(paths.DatabasePath);
        if (File.Exists(wal))
            lastWrite = lastWrite > File.GetLastWriteTimeUtc(wal) ? lastWrite : File.GetLastWriteTimeUtc(wal);

        return lastWrite > newest.Value;
    }
}
