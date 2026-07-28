using Microsoft.Data.Sqlite;

namespace StudyApp.Web.Data;

/// <summary>
/// Handles the one corruption case that is both common and fully recoverable: a damaged
/// write-ahead log beside an intact database.
///
/// SQLite replays the -wal on open, so a malformed one surfaces as
/// "database disk image is malformed" — which reads like total data loss even though the
/// database file itself is perfectly fine. It happens when a process is killed mid-write
/// (force-quit, power loss, a debugger stop). Quarantining the sidecars and retrying restores
/// everything that was checkpointed, which is the overwhelming majority of the data.
///
/// The sidecars are moved, never deleted: if they did hold the only copy of a recent
/// transaction, it is still on disk to recover by hand.
/// </summary>
public static class DatabaseRecovery
{
    public static void EnsureOpenable(string dbPath, string quarantineDir, ILogger? logger = null)
    {
        if (!File.Exists(dbPath))
            return;

        if (CanOpen(dbPath, out var firstError))
            return;

        var wal = dbPath + "-wal";
        var shm = dbPath + "-shm";
        if (!File.Exists(wal) && !File.Exists(shm))
        {
            // Nothing to quarantine, so the damage is in the database file itself. Let the
            // caller fail loudly rather than pretend this is handled — restoring from a
            // backup is a decision for the user, not something to do silently.
            logger?.LogCritical(
                firstError,
                "Database at {Path} is unreadable and has no write-ahead log to discard. " +
                "Restore the newest file from the backups directory.", dbPath);
            return;
        }

        logger?.LogWarning(
            firstError,
            "Database failed to open. Quarantining the write-ahead log and retrying — this is " +
            "usually a process killed mid-write.");

        Directory.CreateDirectory(quarantineDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var sidecar in new[] { wal, shm })
        {
            if (!File.Exists(sidecar))
                continue;
            var destination = Path.Combine(quarantineDir, $"{Path.GetFileName(sidecar)}.{stamp}");
            try
            {
                File.Move(sidecar, destination, overwrite: true);
            }
            catch (IOException ex)
            {
                logger?.LogError(ex, "Could not quarantine {Sidecar}", sidecar);
                return;
            }
        }

        if (CanOpen(dbPath, out var secondError))
        {
            logger?.LogWarning(
                "Database recovered. Any transaction that existed only in the discarded log is " +
                "in {Quarantine}; everything checkpointed to disk is intact.", quarantineDir);
        }
        else
        {
            logger?.LogCritical(
                secondError,
                "Database is still unreadable after discarding the write-ahead log. " +
                "Restore the newest file from the backups directory.");
        }
    }

    private static bool CanOpen(string dbPath, out Exception? error)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // quick_check is the cheap structural probe; a full integrity_check would read
            // every page and slow every single startup for no extra benefit here.
            command.CommandText = "PRAGMA quick_check";
            var result = command.ExecuteScalar() as string;
            if (result == "ok")
            {
                error = null;
                return true;
            }
            error = new InvalidOperationException($"quick_check returned '{result}'");
            return false;
        }
        catch (SqliteException ex)
        {
            error = ex;
            return false;
        }
        finally
        {
            // Dispose() returns the native handle to Microsoft.Data.Sqlite's connection pool
            // rather than closing it — including on the exception path above, which is the
            // common case here (a malformed database throws instead of returning quick_check's
            // result). Without clearing the pool, this process keeps its own handle on the -wal
            // file, so the File.Move below fails with "used by another process" on every retry.
            SqliteConnection.ClearPool(connection);
        }
    }
}
