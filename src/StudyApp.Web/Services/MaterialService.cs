using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

public class MaterialService(IDbContextFactory<StudyDbContext> factory, MaterialFileStore fileStore)
{
    public async Task<List<Material>> GetAllAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Materials.AsNoTracking()
            .Where(m => m.CourseId == courseId)
            .Include(m => m.Unit)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task<Material?> GetAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Materials.AsNoTracking()
            .Include(m => m.Unit)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    /// <summary>The material with its extract loaded, for the extract viewer.</summary>
    public async Task<Material?> GetWithExtractAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Materials.AsNoTracking()
            .Include(m => m.Unit)
            .Include(m => m.Extract)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<Material> UploadAsync(
        Guid courseId, string fileName, string mimeType, Stream content,
        MaterialKind kind, Guid? unitId, DateTime? dueDate)
    {
        var material = new Material
        {
            CourseId = courseId,
            Kind = kind,
            Title = Path.GetFileNameWithoutExtension(fileName),
            MimeType = mimeType,
            UnitId = unitId,
            DueDate = kind == MaterialKind.HomeAssignment ? dueDate : null,
        };

        material.FilePath = await fileStore.SaveAsync(courseId, material.Id, fileName, content);
        material.SizeBytes = fileStore.GetFullPath(material.FilePath) is { } p && File.Exists(p)
            ? new FileInfo(p).Length : 0;

        await using var db = await factory.CreateDbContextAsync();
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        return material;
    }

    public async Task RetagAsync(Guid materialId, Guid? unitId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var material = await db.Materials.FirstAsync(m => m.Id == materialId);
        material.UnitId = unitId;
        await db.SaveChangesAsync();
    }

    // Soft delete only — the file stays on disk so the material is genuinely recoverable,
    // matching every other entity in the app (there's no purge step anywhere that would
    // make a hard file delete here consistent with a soft-deleted row).
    public async Task DeleteAsync(Guid materialId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var material = await db.Materials.FirstAsync(m => m.Id == materialId);
        material.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    public async Task<List<Material>> GetUpcomingAssignmentsAsync(Guid courseId, int take = 5)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Materials.AsNoTracking()
            .Where(m => m.CourseId == courseId && m.Kind == MaterialKind.HomeAssignment && m.DueDate != null)
            .OrderBy(m => m.DueDate)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>Upcoming assignments across every course, for the Home dashboard.</summary>
    public async Task<List<Material>> GetUpcomingAssignmentsAsync(int take = 5)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Materials.AsNoTracking()
            .Where(m => m.Kind == MaterialKind.HomeAssignment && m.DueDate != null)
            .Include(m => m.Course)
            .OrderBy(m => m.DueDate)
            .Take(take)
            .ToListAsync();
    }
}
