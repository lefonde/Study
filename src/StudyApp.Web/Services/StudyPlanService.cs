using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Planning;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

/// <summary>
/// Answers "what should I do before this assignment?" by assembling the inputs
/// <see cref="StudyPlanPolicy"/> needs. All the judgement lives in the policy; this only supplies
/// the data — the topics an assignment assesses, and the cards that teach them.
/// </summary>
public class StudyPlanService(IDbContextFactory<StudyDbContext> factory, StudyPlanPolicy policy)
{
    /// <summary>
    /// The plan for one assignment.
    ///
    /// Scope comes from <see cref="TopicMention.Assessed"/> specifically, not from every topic the
    /// material touches: an exam that mentions paging in passing while testing deadlock should
    /// send you to study deadlock. Returns null when the material assesses nothing yet, so callers
    /// can say "map this course first" rather than showing a confidently empty plan.
    /// </summary>
    public async Task<StudyPlan?> GetForAssignmentAsync(Guid materialId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var material = await db.Materials.AsNoTracking().FirstOrDefaultAsync(m => m.Id == materialId);
        if (material?.DueDate is not { } due)
            return null;

        var topicIds = await db.TopicSources.AsNoTracking()
            .Where(s => s.MaterialId == materialId && s.Mention == TopicMention.Assessed)
            .Select(s => s.CourseTopicId)
            .Distinct()
            .ToListAsync();

        if (topicIds.Count == 0)
            return null;

        return await BuildAsync(db, topicIds, DateTime.SpecifyKind(due.Date, DateTimeKind.Utc));
    }

    /// <summary>
    /// Whole-course plan against an arbitrary date — every topic, not only what one assignment
    /// assesses. Used where the question is "how ready am I overall?".
    /// </summary>
    public async Task<StudyPlan> GetForCourseAsync(Guid courseId, DateTime targetDateUtc)
    {
        await using var db = await factory.CreateDbContextAsync();

        var topicIds = await db.CourseTopics.AsNoTracking()
            .Where(t => t.CourseId == courseId && !t.IsDismissed)
            .Select(t => t.Id)
            .ToListAsync();

        return topicIds.Count == 0
            ? StudyPlan.Empty
            : await BuildAsync(db, topicIds, targetDateUtc);
    }

    private async Task<StudyPlan> BuildAsync(
        StudyDbContext db, List<Guid> topicIds, DateTime targetDateUtc)
    {
        var topics = await db.CourseTopics.AsNoTracking()
            .Where(t => topicIds.Contains(t.Id))
            .ToListAsync();

        // One query for every card across every topic in scope, then grouped in memory: a topic
        // set is small, and this avoids a query per topic.
        var links = await db.CardTopics.AsNoTracking()
            .Where(ct => topicIds.Contains(ct.CourseTopicId))
            .Select(ct => new { ct.CourseTopicId, ct.Card })
            .ToListAsync();

        var cardsByTopic = links
            .Where(l => l.Card is not null)
            .GroupBy(l => l.CourseTopicId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Card>)g.Select(l => l.Card!).ToList());

        var input = topics.Select(t =>
            new TopicCards(t, cardsByTopic.GetValueOrDefault(t.Id, [])));

        return policy.Build(input, targetDateUtc);
    }
}
