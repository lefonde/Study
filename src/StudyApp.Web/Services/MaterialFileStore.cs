namespace StudyApp.Web.Services;

/// <summary>
/// Stores uploaded material files on disk under one directory per course.
/// FilePath on Material is always relative to the files root — never an absolute path —
/// so the store can be relocated (local disk, mounted volume) without touching the database.
/// </summary>
public class MaterialFileStore(StudyAppPaths paths)
{
    /// <summary>Writes the stream to disk and returns the path to store on Material.FilePath.</summary>
    public async Task<string> SaveAsync(Guid courseId, Guid materialId, string originalFileName, Stream content)
    {
        var courseDir = Path.Combine(paths.FilesDirectory, courseId.ToString());
        Directory.CreateDirectory(courseDir);

        var ext = Path.GetExtension(originalFileName);
        var relativePath = Path.Combine(courseId.ToString(), $"{materialId}{ext}");
        var fullPath = Path.Combine(paths.FilesDirectory, relativePath);

        await using var fileStream = File.Create(fullPath);
        await content.CopyToAsync(fileStream);

        return relativePath.Replace('\\', '/');
    }

    public string GetFullPath(string relativePath) =>
        Path.Combine(paths.FilesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
