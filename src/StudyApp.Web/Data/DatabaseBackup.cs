using Microsoft.Data.Sqlite;

namespace StudyApp.Web.Data;

/// <summary>
/// Snapshots the SQLite database before migrations run, protecting real study data from a bad
/// migration. One backup per calendar day, newest <c>keep</c> retained.
///
/// The backup is verified after it is written. A backup that silently doesn't work is worse
/// than no backup at all, because you stop worrying about the thing that isn't protected —
/// this went wrong once already: opening the source <c>Mode=ReadOnly</c> meant SQLite could
/// not attach the write-ahead log (a read-only connection can't create the -shm file it needs),
/// so everything still sitting in the WAL was invisible and the "backup" was an empty 4 KB
/// file. Hence: open read-write, fold the WAL in first, then prove the copy has the data.
/// </summary>
public static class DatabaseBackup
{
    public static void Run(string dbPath, string backupDir, ILogger? logger = null, int keep = 7)
    {
        if (!File.Exists(dbPath))
            return;

        Directory.CreateDirectory(backupDir);
        var target = Path.Combine(backupDir, $"studyapp-{DateTime.Now:yyyyMMdd}.db");

        if (!File.Exists(target))
        {
            try
            {
                WriteBackup(dbPath, target, logger);
            }
            catch (Exception ex)
            {
                // Never block startup on a failed backup, but never let it pass quietly either.
                logger?.LogError(ex, "Database backup FAILED. Your data is not protected today.");
                TryDelete(target);
            }
        }

        foreach (var old in Directory.GetFiles(backupDir, "studyapp-*.db")
                     .OrderByDescending(f => f, StringComparer.Ordinal)
                     .Skip(keep))
        {
            TryDelete(old);
            RemoveSidecars(old);
        }
    }

    private static void WriteBackup(string dbPath, string target, ILogger? logger)
    {
        int sourceTables;

        // Read-write (not ReadOnly): required for the WAL to be attached and checkpointed.
        using (var source = new SqliteConnection($"Data Source={dbPath}"))
        {
            source.Open();

            // Fold any pending WAL content into the main database so the copy is complete.
            // Harmless no-op if the database isn't in WAL mode.
            using (var checkpoint = source.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
            }

            sourceTables = CountUserTables(source);

            using var destination = new SqliteConnection($"Data Source={target}");
            destination.Open();
            source.BackupDatabase(destination);
            SqliteConnection.ClearPool(destination);
        }
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));

        Verify(target, sourceTables, logger);

        // Last, because verifying reopens the file and recreates them. A backup must be a
        // single self-contained file: sidecars left beside it are actively dangerous, since
        // SQLite replays them on open and a stale -wal makes the backup read as a completely
        // different database — precisely the surprise you don't want mid-restore.
        RemoveSidecars(target);
    }

    /// <summary>Proves the file on disk is a readable database holding the source's tables.</summary>
    private static void Verify(string target, int sourceTables, ILogger? logger)
    {
        using var check = new SqliteConnection($"Data Source={target};Mode=ReadOnly");
        check.Open();

        using (var integrity = check.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (integrity.ExecuteScalar() as string != "ok")
                throw new InvalidOperationException("Backup failed its own integrity check.");
        }

        var backedUpTables = CountUserTables(check);
        // Release the file handle so the sidecars this read created can be removed.
        SqliteConnection.ClearPool(check);

        if (backedUpTables < sourceTables)
            throw new InvalidOperationException(
                $"Backup has {backedUpTables} tables but the database has {sourceTables} — incomplete copy.");

        logger?.LogInformation(
            "Database backed up to {Target} ({Tables} tables, {Size:N0} bytes)",
            Path.GetFileName(target), backedUpTables, new FileInfo(target).Length);
    }

    private static int CountUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void RemoveSidecars(string dbFile)
    {
        TryDelete(dbFile + "-wal");
        TryDelete(dbFile + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A locked leftover isn't worth failing startup over.
        }
    }
}
