using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

public class CourseUnitService(IDbContextFactory<StudyDbContext> factory)
{
    public async Task<List<CourseUnit>> GetTreeAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.CourseUnits.AsNoTracking()
            .Where(u => u.CourseId == courseId)
            .OrderBy(u => u.Order)
            .ToListAsync();
    }

    public async Task<CourseUnit> CreateAsync(Guid courseId, Guid? parentId, CourseUnitKind kind, string title)
    {
        await using var db = await factory.CreateDbContextAsync();
        var maxOrder = await db.CourseUnits
            .Where(u => u.CourseId == courseId && u.ParentId == parentId)
            .Select(u => (int?)u.Order)
            .MaxAsync() ?? -1;

        var unit = new CourseUnit
        {
            CourseId = courseId,
            ParentId = parentId,
            Kind = kind,
            Title = title.Trim(),
            Order = maxOrder + 1,
        };
        db.CourseUnits.Add(unit);
        await db.SaveChangesAsync();
        return unit;
    }

    public async Task RenameAsync(Guid unitId, string title)
    {
        await using var db = await factory.CreateDbContextAsync();
        var unit = await db.CourseUnits.FirstAsync(u => u.Id == unitId);
        unit.Title = title.Trim();
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid unitId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var unit = await db.CourseUnits.FirstOrDefaultAsync(u => u.Id == unitId);
        if (unit is null)
            return;

        // Load the whole course's units to compute the descendant closure in memory —
        // trees here are small, and this handles any depth (not just direct children).
        var courseUnits = await db.CourseUnits.Where(u => u.CourseId == unit.CourseId).ToListAsync();
        var toDelete = DescendantsAndSelf(courseUnits, unitId).ToList();
        foreach (var u in toDelete)
            u.IsDeleted = true;

        // A soft-delete doesn't trip the FK's SetNull behavior (that only fires on a hard
        // delete), so the course's position marker must be cleared explicitly if it pointed
        // at the deleted unit or one of its descendants.
        var course = await db.Courses.FirstAsync(c => c.Id == unit.CourseId);
        if (course.CurrentUnitId is { } cur && toDelete.Any(u => u.Id == cur))
            course.CurrentUnitId = null;

        await db.SaveChangesAsync();
    }

    private static IEnumerable<CourseUnit> DescendantsAndSelf(List<CourseUnit> all, Guid rootId)
    {
        var stack = new Stack<Guid>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            var node = all.FirstOrDefault(u => u.Id == id);
            if (node is null)
                continue;
            yield return node;
            foreach (var child in all.Where(u => u.ParentId == id))
                stack.Push(child.Id);
        }
    }

    public async Task SetPositionAsync(Guid courseId, Guid? unitId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var course = await db.Courses.FirstAsync(c => c.Id == courseId);
        course.CurrentUnitId = unitId;
        await db.SaveChangesAsync();
    }
}
