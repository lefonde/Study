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
        // Seeded here rather than as a property initialiser — see Course.AssessmentWeights for why.
        var course = new Course
        {
            Name = name.Trim(),
            Color = color,
            AssessmentWeights = AssessmentWeight.Defaults(),
        };
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

    /// <summary>
    /// The course's assessment-weighting profile, always complete.
    ///
    /// Goes through <see cref="AssessmentWeight.Complete"/> rather than returning the stored list
    /// directly, because two cases yield a partial profile: courses created before the column
    /// existed read back NULL (the property initializer only applies to objects constructed in
    /// C#, not to rows materialised by EF), and a <see cref="MaterialKind"/> added later would be
    /// absent from every profile already saved.
    /// </summary>
    public async Task<List<AssessmentWeight>> GetAssessmentWeightsAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        return AssessmentWeight.Complete(course?.AssessmentWeights ?? []);
    }

    public async Task SaveAssessmentWeightsAsync(Guid id, IEnumerable<AssessmentWeight> weights)
    {
        await using var db = await factory.CreateDbContextAsync();
        var course = await db.Courses.FirstAsync(c => c.Id == id);
        course.AssessmentWeights = AssessmentWeight.Complete(weights)
            .Select(w => new AssessmentWeight { Kind = w.Kind, Weight = Math.Clamp(w.Weight, 0, 100) })
            .ToList();
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
