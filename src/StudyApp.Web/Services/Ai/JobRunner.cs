using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;
using StudyApp.Web.Services;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Runs queued AI work one job at a time in the background, so a multi-minute ingestion never
/// blocks a Blazor circuit. Serial by design: these calls are expensive and the app has one
/// user, so there's nothing to gain from concurrency and a real risk of surprise spend.
/// </summary>
public class JobRunner(
    JobQueue queue,
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<StudyDbContext> factory,
    TimeProvider timeProvider,
    ILogger<JobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedJobsAsync(stoppingToken);
        await CaptureStartupProgressSnapshotsAsync(stoppingToken);

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunOneAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad job kill the runner — the next one should still get a turn.
                logger.LogError(ex, "Job {JobId} failed unexpectedly", jobId);
            }
        }
    }

    /// <summary>
    /// A job that was mid-flight when the process stopped can't be resumed — the API call is
    /// gone. Mark it failed at startup so the UI doesn't show a spinner forever.
    /// </summary>
    private async Task FailInterruptedJobsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stranded = await db.GenerationJobs
            .Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued)
            .ToListAsync(ct);

        foreach (var job in stranded)
        {
            job.Status = JobStatus.Failed;
            job.Message = "Interrupted by an app restart. Run it again.";
            job.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        }

        if (stranded.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Piggybacks on this BackgroundService's existing startup phase rather than adding a
    /// second hosted service just to run once at boot. Unrelated to the AI job queue below —
    /// resolved from its own DI scope, not <see cref="ProgressSnapshotService"/> being an AI
    /// concern.
    /// </summary>
    private async Task CaptureStartupProgressSnapshotsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ProgressSnapshotService>().CaptureAllAsync(ct);
        }
        catch (Exception ex)
        {
            // Missing today's snapshot is a gap in a trend chart, not a reason to stop the
            // job runner from starting.
            logger.LogWarning(ex, "Startup progress snapshot capture failed");
        }
    }

    private async Task RunOneAsync(Guid jobId, CancellationToken ct)
    {
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var queued = await db.GenerationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (queued is null)
                return;
            queued.Status = JobStatus.Running;
            queued.StartedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        using var scope = scopeFactory.CreateScope();
        await using var readDb = await factory.CreateDbContextAsync(ct);
        var job = await readDb.GenerationJobs.AsNoTracking().FirstAsync(j => j.Id == jobId, ct);

        string? failure = null;
        try
        {
            switch (job.Kind)
            {
                case JobKind.Ingest:
                    await scope.ServiceProvider.GetRequiredService<IngestionService>().RunAsync(job, ct);
                    break;
                case JobKind.GenerateCards:
                    await scope.ServiceProvider.GetRequiredService<CardGenerationService>().RunAsync(job, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({Kind}) failed", jobId, job.Kind);
            failure = ex.Message;
        }

        await using var finishDb = await factory.CreateDbContextAsync(ct);
        var finished = await finishDb.GenerationJobs.FirstAsync(j => j.Id == jobId, ct);
        finished.Status = failure is null ? JobStatus.Succeeded : JobStatus.Failed;
        finished.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (failure is not null)
        {
            finished.Message = failure;
            if (job.Kind == JobKind.Ingest && job.MaterialId is { } materialId)
            {
                var material = await finishDb.Materials.FirstOrDefaultAsync(m => m.Id == materialId, ct);
                if (material is not null)
                    material.Status = MaterialStatus.Failed;
            }
        }
        await finishDb.SaveChangesAsync(ct);
    }
}
