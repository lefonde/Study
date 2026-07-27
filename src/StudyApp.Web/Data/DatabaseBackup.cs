using Microsoft.Data.Sqlite;

namespace StudyApp.Web.Data;

/// <summary>
/// Snapshots the SQLite database to a rotating backup before migrations run, protecting real
/// study data from a bad migration. Uses SQLite's backup API rather than a file copy so
/// WAL-mode content is included. One backup per calendar day, newest 7 kept.
/// </summary>
public static class DatabaseBackup
{
    public static void Run(string dbPath, string backupDir, int keep = 7)
    {
        if (!File.Exists(dbPath))
            return;

        Directory.CreateDirectory(backupDir);
        var target = Path.Combine(backupDir, $"studyapp-{DateTime.Now:yyyyMMdd}.db");
        if (!File.Exists(target))
        {
            using var source = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            using var destination = new SqliteConnection($"Data Source={target}");
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            SqliteConnection.ClearPool(source);
            SqliteConnection.ClearPool(destination);
        }

        foreach (var old in Directory.GetFiles(backupDir, "studyapp-*.db")
                     .OrderByDescending(f => f, StringComparer.Ordinal)
                     .Skip(keep))
        {
            File.Delete(old);
        }
    }
}
