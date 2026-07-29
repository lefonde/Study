using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Reviews a submitted solution against the assignment it answers.
///
/// Like every stage after ingestion, this reads extracts rather than files — which is why a
/// submission has to be ingested before it can be reviewed, and why re-running a review costs
/// nothing extra in vision tokens.
///
/// The output is not just prose: each finding is attributed to a course topic, so a wrong answer
/// becomes a concrete statement the map can show and gap-targeted generation can act on.
/// </summary>
public class SubmissionReviewService(
    IDbContextFactory<StudyDbContext> factory,
    ClaudeService claude)
{
    public async Task RunAsync(GenerationJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var submission = await db.Materials.AsNoTracking()
            .Include(m => m.Extract)
            .FirstOrDefaultAsync(m => m.Id == job.MaterialId, ct)
            ?? throw new InvalidOperationException("The solution was deleted before the review ran.");

        if (submission.Extract is not { Sections.Count: > 0 } solutionExtract)
            throw new InvalidOperationException(
                "This solution hasn't been ingested yet — run ingestion on it first.");

        if (submission.SubmissionForId is not { } assignmentId)
            throw new InvalidOperationException(
                "This file isn't attached to an assignment, so there is nothing to review it against.");

        var assignment = await db.Materials.AsNoTracking()
            .Include(m => m.Extract)
            .FirstOrDefaultAsync(m => m.Id == assignmentId, ct)
            ?? throw new InvalidOperationException("The assignment this answers no longer exists.");

        if (assignment.Extract is not { Sections.Count: > 0 } assignmentExtract)
            throw new InvalidOperationException(
                $"“{assignment.Title}” hasn't been ingested, so there are no questions to review against.");

        var topics = await db.CourseTopics.AsNoTracking()
            .Where(t => t.CourseId == job.CourseId && !t.IsDismissed)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        // Short refs across the boundary, as everywhere else: an echoed GUID invites a single
        // flipped character that silently files a mistake under the wrong topic.
        var topicRefs = topics.Select((t, i) => (Topic: t, Ref: $"T{i + 1}")).ToList();

        var result = await claude.ReviewSolutionAsync(
            assignmentExtract.ToMarkdown(),
            solutionExtract.ToMarkdown(),
            BuildTopicList(topicRefs),
            ct);

        var topicByRef = topicRefs.ToDictionary(x => x.Ref, x => x.Topic.Id, StringComparer.OrdinalIgnoreCase);

        var review = new SubmissionReview
        {
            MaterialId = submission.Id,
            JobId = job.Id,
            Summary = ClaudeService.NormalizeMarkdown(result.Value.Summary).Trim(),
            Findings = [.. (result.Value.Findings ?? [])
                .Where(f => !string.IsNullOrWhiteSpace(f.Question))
                .Select(f => new ReviewFinding
                {
                    Question = f.Question.Trim(),
                    Verdict = ParseVerdict(f.Verdict),
                    Comment = ClaudeService.NormalizeMarkdown(f.Comment).Trim(),
                    CourseTopicId = !string.IsNullOrWhiteSpace(f.TopicRef)
                                    && topicByRef.TryGetValue(f.TopicRef.Trim(), out var id)
                        ? id
                        : null,
                })],
        };

        // One review per submission: re-running replaces rather than accumulating, the same way
        // re-ingesting replaces an extract. A stale review beside a fresh one is only confusing.
        var existing = await db.SubmissionReviews
            .Where(r => r.MaterialId == submission.Id)
            .ToListAsync(ct);
        db.SubmissionReviews.RemoveRange(existing);
        db.SubmissionReviews.Add(review);

        job.InputTokens = result.InputTokens;
        job.OutputTokens = result.OutputTokens;
        job.Message = review.Findings.Count == 0
            ? "Reviewed, but no answers could be matched to the assignment's questions."
            : $"{review.Findings.Count} answer(s) reviewed · "
              + $"{review.CountOf(ReviewVerdict.Correct)} correct, "
              + $"{review.Findings.Count - review.CountOf(ReviewVerdict.Correct)} to look at.";

        db.GenerationJobs.Attach(job);
        db.Entry(job).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    private static string BuildTopicList(List<(CourseTopic Topic, string Ref)> topicRefs)
    {
        if (topicRefs.Count == 0)
            return "(none — this course hasn't been mapped, so leave every topicRef empty)";

        var sb = new StringBuilder();
        foreach (var (topic, reference) in topicRefs)
            sb.AppendLine($"- [{reference}] {topic.Name}");
        return sb.ToString();
    }

    /// <summary>
    /// Defaults to <see cref="ReviewVerdict.Partly"/> for anything unrecognised: an unparseable
    /// verdict should draw the student's eye to the answer, not quietly mark it correct.
    /// </summary>
    private static ReviewVerdict ParseVerdict(string? verdict) => verdict?.Trim().ToLowerInvariant() switch
    {
        "correct" => ReviewVerdict.Correct,
        "incorrect" => ReviewVerdict.Incorrect,
        "missing" => ReviewVerdict.Missing,
        _ => ReviewVerdict.Partly,
    };

    public async Task<SubmissionReview?> GetForSubmissionAsync(Guid materialId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SubmissionReviews.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MaterialId == materialId);
    }

    /// <summary>Reviews for every solution handed in against one assignment, newest first.</summary>
    public async Task<List<SubmissionReview>> GetForAssignmentAsync(Guid assignmentId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SubmissionReviews.AsNoTracking()
            .Include(r => r.Material)
            .Where(r => r.Material!.SubmissionForId == assignmentId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }
}
