namespace StudyApp.Web;

/// <summary>
/// Resolves where user data lives. Local runs default to %LOCALAPPDATA%\StudyApp;
/// deployments override it via the StudyApp__DataDirectory environment variable
/// (e.g. /data on a mounted volume). Everything stateful — database, uploads and
/// backups — sits under this one directory so a single persistent volume covers it all.
/// </summary>
public class StudyAppPaths
{
    public StudyAppPaths(IConfiguration configuration)
    {
        var configured = configuration["StudyApp:DataDirectory"];
        DataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyApp")
            : Path.GetFullPath(configured);

        DatabasePath = Path.Combine(DataDirectory, "studyapp.db");
        FilesDirectory = Path.Combine(DataDirectory, "files");
        BackupDirectory = Path.Combine(DataDirectory, "backups");
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string FilesDirectory { get; }
    public string BackupDirectory { get; }

    /// <summary>Creates the data directories. Throws if the volume isn't writable — fail fast at startup.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(FilesDirectory);
    }
}
