using Microsoft.Data.Sqlite;
using StudyApp.Core.Maintenance;

namespace StudyApp.Web.Data;

/// <summary>
/// Snapshots the SQLite database — at startup before migrations run, and periodically while the
/// app is up (see <see cref="PeriodicBackupService"/>).
///
/// The backup is verified after it is written. A backup that silently doesn't work is worse
/// than no backup at all, because you stop worrying about the thing that isn't protected —
/// this went wrong once already: opening the source <c>Mode=ReadOnly</c> meant SQLite could
/// not attach the write-ahead log (a read-only connection can't create the -shm file it needs),
/// so everything still sitting in the WAL was invisible and the "backup" was an empty 4 KB
/// file. Hence: open read-write, fold the WAL in first, then prove the copy has the data.
///
/// Snapshots are timestamped, not one-per-day. The earlier design took a single backup on the
/// first startup of each calendar day and skipped every later one, which failed badly in
/// practice: a day whose first startup happened from an already-degraded database spent its
/// only slot recording the damage, and everything done for the rest of that day had no
/// protection at all.
/// </summary>
public static class DatabaseBackup
{
    private const string Prefix = "studyapp-";

    /// <summary>
    /// Writes a snapshot and prunes old ones. Retention keeps the newest
    /// <paramref name="keepRecent"/> overall, plus the <i>first and last</i> snapshot of each of
    /// the most recent <paramref name="keepDays"/> days — first as well as last, because a day
    /// can start healthy and end damaged, and keeping only the newest would discard the good
    /// copy and retain the broken one.
    /// </summary>
    public static void Run(
        string dbPath, string backupDir, ILogger? logger = null, int keepRecent = 10, int keepDays = 7)
    {
        if (!File.Exists(dbPath))
            return;

        Directory.CreateDirectory(backupDir);
        var target = Path.Combine(backupDir, $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmss}.db");

        // A second snapshot inside the same second would collide; never overwrite one.
        if (File.Exists(target))
            return;

        try
        {
            WriteBackup(dbPath, target, backupDir, logger);
        }
        catch (Exception ex)
        {
            // Never block startup on a failed backup, but never let it pass quietly either.
            logger?.LogError(ex, "Database backup FAILED. Your data is not protected right now.");
            TryDelete(target);
            return;
        }

        Prune(backupDir, keepRecent, keepDays, logger);
    }

    private static void WriteBackup(string dbPath, string target, string backupDir, ILogger? logger)
    {
        int sourceTables;
        int sourceCards;

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
            sourceCards = CountRows(source, "Cards");

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

        WarnIfDataDisappeared(backupDir, target, sourceCards, logger);
    }

    /// <summary>
    /// Compares this snapshot's card count against the previous one and says so if it dropped.
    ///
    /// A backup faithfully recording that your data is gone is still a correct backup and must
    /// not fail — but it is exactly the moment a human needs to intervene, before retention
    /// ages out the copies that still hold the data. This is the signal that was missing when a
    /// day's snapshot captured a database that had silently lost a deck of cards.
    /// </summary>
    private static void WarnIfDataDisappeared(
        string backupDir, string justWritten, int currentCards, ILogger? logger)
    {
        var previous = EnumerateBackups(backupDir)
            .Where(b => !string.Equals(b.Path, justWritten, StringComparison.OrdinalIgnoreCase))
            .MaxBy(b => b.Taken);

        if (previous is null)
            return;

        int previousCards;
        try
        {
            using var connection = new SqliteConnection($"Data Source={previous.Path};Mode=ReadOnly");
            connection.Open();
            previousCards = CountRows(connection, "Cards");
        }
        catch (SqliteException)
        {
            return; // An unreadable older backup is not this snapshot's problem.
        }
        finally
        {
            // Reading an existing backup must leave it exactly as it was found. Opening it
            // recreates the -wal/-shm sidecars, and a backup with sidecars beside it is the
            // dangerous case described above: SQLite replays them on open, so the file can
            // later read as a different database than the one that was verified.
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={previous.Path};Mode=ReadOnly"));
            RemoveSidecars(previous.Path);
        }

        if (currentCards < previousCards)
        {
            logger?.LogWarning(
                "Backup shows {Current} cards but the previous snapshot ({Previous}) had {Before}. " +
                "This backup is correct, but the database appears to have LOST data — check before " +
                "the older snapshots age out of {Dir}.",
                currentCards, Path.GetFileName(previous.Path), previousCards, backupDir);
        }
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

    /// <summary>
    /// Backups whose timestamp could be parsed, newest first. Files that don't match the naming
    /// scheme are ignored rather than deleted — an unrecognised file in here is far more likely
    /// to be something a human put there deliberately than junk worth removing.
    /// </summary>
    private static List<BackupEntry> EnumerateBackups(string backupDir) =>
        Directory.GetFiles(backupDir, $"{Prefix}*.db")
            .Select(path => (path, taken: ParseTimestamp(path)))
            .Where(x => x.taken is not null)
            .Select(x => new BackupEntry(x.path, x.taken!.Value))
            .OrderByDescending(b => b.Taken)
            .ToList();

    /// <summary>
    /// Reads the timestamp out of a backup filename. Accepts the current
    /// <c>yyyyMMdd-HHmmss</c> form and the older date-only <c>yyyyMMdd</c> one, so snapshots
    /// taken before this change still take part in retention instead of accumulating forever.
    /// </summary>
    private static DateTime? ParseTimestamp(string path)
    {
        var stamp = Path.GetFileNameWithoutExtension(path)[Prefix.Length..];
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", null,
                   System.Globalization.DateTimeStyles.None, out var precise) ? precise
             : DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                   System.Globalization.DateTimeStyles.None, out var dateOnly) ? dateOnly
             : null;
    }

    private static void Prune(string backupDir, int keepRecent, int keepDays, ILogger? logger)
    {
        foreach (var stale in BackupRetention.SelectForDeletion(
                     EnumerateBackups(backupDir), keepRecent, keepDays))
        {
            TryDelete(stale.Path);
            RemoveSidecars(stale.Path);
            logger?.LogDebug("Pruned old backup {File}", Path.GetFileName(stale.Path));
        }
    }

    private static int CountUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Row count, or -1 when the table doesn't exist yet (a database predating it).</summary>
    private static int CountRows(SqliteConnection connection, string table)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return -1;
        }
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
