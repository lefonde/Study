using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

public class CourseService(IDbContextFactory<StudyDbContext> factory)
{
    public async Task<List<Course>> GetAllAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Courses.AsNoTracking()
            .Include(c => c.Decks)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<Course?> GetAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Courses.AsNoTracking()
            .Include(c => c.Decks.OrderBy(d => d.Name))
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<Course> CreateAsync(string name, string color)
    {
        await using var db = await factory.CreateDbContextAsync();
        var course = new Course { Name = name.Trim(), Color = color };
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return course;
    }

    public async Task UpdateAsync(Guid id, string name, string color)
    {
        await using var db = await factory.CreateDbContextAsync();
        var course = await db.Courses.FirstAsync(c => c.Id == id);
        course.Name = name.Trim();
        course.Color = color;
        await db.SaveChangesAsync();
    }

    public async Task SaveNotesAsync(Guid id, string notesMarkdown)
    {
        await using var db = await factory.CreateDbContextAsync();
        var course = await db.Courses.FirstAsync(c => c.Id == id);
        course.NotesMarkdown = notesMarkdown;
        await db.SaveChangesAsync();
    }

    public async Task SoftDeleteAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var course = await db.Courses
            .Include(c => c.Decks)
            .ThenInclude(d => d.Cards)
            .Include(c => c.Units)
            .Include(c => c.Materials)
            .FirstAsync(c => c.Id == id);

        course.IsDeleted = true;
        foreach (var deck in course.Decks)
        {
            deck.IsDeleted = true;
            foreach (var card in deck.Cards)
                card.IsDeleted = true;
        }
        foreach (var unit in course.Units)
            unit.IsDeleted = true;
        foreach (var material in course.Materials)
            material.IsDeleted = true;
        await db.SaveChangesAsync();
    }
}
