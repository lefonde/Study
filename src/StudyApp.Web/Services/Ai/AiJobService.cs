using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

public record CourseSpend(long InputTokens, long OutputTokens, decimal Usd, int JobCount);

/// <summary>What a bulk ingestion would cover, so the cost can be shown before committing to it.</summary>
public record PendingIngestion(int MaterialCount, long TotalBytes);

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

    /// <summary>
    /// Queues a course-scoped job — one with no material of its own, because it reads all of them.
    /// Refuses a duplicate while one is in flight: mapping is the kind of run it's easy to fire
    /// twice by accident and pointless to pay for twice.
    /// </summary>
    public async Task<Guid> QueueCourseJobAsync(Guid courseId, JobKind kind, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var alreadyRunning = await db.GenerationJobs.AnyAsync(
            j => j.CourseId == courseId && j.Kind == kind
                 && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);
        if (alreadyRunning)
            throw new InvalidOperationException("That run is already in progress for this course.");

        var job = new GenerationJob
        {
            CourseId = courseId,
            MaterialId = null,
            Kind = kind,
            Status = JobStatus.Queued,
            Model = options.Model,
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(job.Id, ct);
        return job.Id;
    }

    /// <summary>
    /// Queues generation aimed at specific topics rather than at a material — the action a
    /// coverage gap leads to.
    /// </summary>
    public async Task<Guid> QueueTopicGenerationAsync(
        Guid courseId, IReadOnlyList<Guid> topicIds, CancellationToken ct = default)
    {
        if (topicIds.Count == 0)
            throw new InvalidOperationException("Pick at least one topic to generate cards for.");

        await using var db = await factory.CreateDbContextAsync(ct);

        var job = new GenerationJob
        {
            CourseId = courseId,
            MaterialId = null,
            Kind = JobKind.GenerateCards,
            Status = JobStatus.Queued,
            Model = options.Model,
            TargetTopicIds = [.. topicIds],
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(job.Id, ct);
        return job.Id;
    }

    /// <summary>
    /// Materials in this course that haven't been ingested yet — the scope of "Ingest all", and
    /// the basis of the estimate shown before it runs. Failed materials are included: a failure
    /// is usually transient (a timeout, an oversized file) and retrying is the point.
    /// </summary>
    public async Task<PendingIngestion> GetPendingIngestionAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var pending = await db.Materials.AsNoTracking()
            .Where(m => m.CourseId == courseId && m.Status != MaterialStatus.Ingested)
            .Select(m => m.SizeBytes)
            .ToListAsync();

        return new PendingIngestion(pending.Count, pending.Sum());
    }

    /// <summary>
    /// Queues an ingestion for every un-ingested material in the course, skipping any that
    /// already has one in flight. Returns how many were actually queued.
    ///
    /// One job per material rather than one big job: <see cref="JobRunner"/> already runs them
    /// serially, and per-material jobs mean a single bad file fails alone instead of taking the
    /// whole batch — and everything already ingested isn't paid for twice on a retry.
    /// </summary>
    public async Task<int> QueueIngestAllAsync(Guid courseId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var pending = await db.Materials.AsNoTracking()
            .Where(m => m.CourseId == courseId && m.Status != MaterialStatus.Ingested)
            .Select(m => m.Id)
            .ToListAsync(ct);

        var inFlight = await db.GenerationJobs.AsNoTracking()
            .Where(j => j.CourseId == courseId && j.Kind == JobKind.Ingest
                        && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .Select(j => j.MaterialId)
            .ToListAsync(ct);

        var toQueue = pending.Where(id => !inFlight.Contains(id)).ToList();
        if (toQueue.Count == 0)
            return 0;

        var jobs = toQueue.Select(materialId => new GenerationJob
        {
            CourseId = courseId,
            MaterialId = materialId,
            Kind = JobKind.Ingest,
            Status = JobStatus.Queued,
            Model = options.Model,
        }).ToList();

        db.GenerationJobs.AddRange(jobs);
        await db.SaveChangesAsync(ct);

        // Enqueued only after the rows are committed, so the runner can never pick up a job id
        // that isn't in the database yet.
        foreach (var job in jobs)
            await queue.EnqueueAsync(job.Id, ct);

        return jobs.Count;
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
