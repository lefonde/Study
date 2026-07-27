using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

public record CourseSpend(long InputTokens, long OutputTokens, decimal Usd, int JobCount);

/// <summary>Queues AI work and reports on it. The UI never touches <see cref="ClaudeService"/> directly.</summary>
public class AiJobService(
    IDbContextFactory<StudyDbContext> factory,
    JobQueue queue,
    AiOptions options)
{
    public async Task<Guid> QueueAsync(Guid materialId, JobKind kind, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var material = await db.Materials.AsNoTracking().FirstAsync(m => m.Id == materialId, ct);

        // One in-flight job per material: double-clicking "Generate" shouldn't buy two runs.
        var alreadyRunning = await db.GenerationJobs.AnyAsync(
            j => j.MaterialId == materialId && j.Kind == kind
                 && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);
        if (alreadyRunning)
            throw new InvalidOperationException("That job is already running for this material.");

        var job = new GenerationJob
        {
            CourseId = material.CourseId,
            MaterialId = materialId,
            Kind = kind,
            Status = JobStatus.Queued,
            Model = options.Model,
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(job.Id, ct);
        return job.Id;
    }

    public async Task<List<GenerationJob>> GetRecentAsync(Guid courseId, int take = 10)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.GenerationJobs.AsNoTracking()
            .Where(j => j.CourseId == courseId)
            .Include(j => j.Material)
            .OrderByDescending(j => j.CreatedAt)
            .Take(take)
            .ToListAsync();
    }

    public async Task<bool> HasActiveJobsAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.GenerationJobs.AnyAsync(
            j => j.CourseId == courseId
                 && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running));
    }

    /// <summary>Spend for one course, or across every course when courseId is null.</summary>
    public async Task<CourseSpend> GetSpendAsync(Guid? courseId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var jobs = await db.GenerationJobs.AsNoTracking()
            .Where(j => courseId == null || j.CourseId == courseId)
            .Select(j => new { j.Model, j.InputTokens, j.OutputTokens })
            .ToListAsync();

        return new CourseSpend(
            jobs.Sum(j => j.InputTokens),
            jobs.Sum(j => j.OutputTokens),
            jobs.Sum(j => AiPricing.Estimate(j.Model, j.InputTokens, j.OutputTokens)),
            jobs.Count);
    }
}
