using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

/// <summary>
/// Writes today's <see cref="ProgressReport"/> per deck to <see cref="ProgressSnapshot"/>, so
/// trend charts read stored rows instead of replaying every <see cref="ReviewLog"/> on each
/// page load. Called from two places, neither of which is new machinery: once at host startup
/// (<c>Ai.JobRunner</c> already runs one-time startup work before its job loop, alongside
/// <c>FailInterruptedJobsAsync</c>) and once right after a review session ends (a plain scoped
/// call from <c>Review.razor</c> — there's no queue to reuse for a UI-triggered event, and one
/// deck's worth of cards is cheap enough not to need one).
///
/// Idempotent by design: the unique index on (DeckId, CapturedOn) is the actual guarantee,
/// the existence check just avoids the round trip in the common case where today is already
/// captured.
/// </summary>
public class ProgressSnapshotService(
    IDbContextFactory<StudyDbContext> factory,
    ProgressPolicy policy,
    TimeProvider timeProvider,
    ILogger<ProgressSnapshotService> logger)
{
    /// <summary>Captures every deck that doesn't yet have a row for today. Run at startup.</summary>
    public async Task CaptureAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var today = Today();
        var alreadyCaptured = await db.ProgressSnapshots.AsNoTracking()
            .Where(s => s.CapturedOn == today)
            .Select(s => s.DeckId)
            .ToListAsync(ct);

        var pending = await db.Decks.AsNoTracking()
            .Where(d => !alreadyCaptured.Contains(d.Id))
            .Select(d => new { d.Id, d.CourseId })
            .ToListAsync(ct);

        foreach (var deck in pending)
            await CaptureOneAsync(db, deck.Id, deck.CourseId, today, ct);

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Captured progress snapshots for {Count} deck(s)", pending.Count);
        }
    }

    /// <summary>Captures (or re-captures) one deck's snapshot for today. Call after a review session ends.</summary>
    public async Task CaptureForDeckAsync(Guid deckId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var deck = await db.Decks.AsNoTracking()
            .Where(d => d.Id == deckId)
            .Select(d => new { d.Id, d.CourseId })
            .FirstOrDefaultAsync(ct);
        if (deck is null)
            return;

        var today = Today();
        var existing = await db.ProgressSnapshots.FirstOrDefaultAsync(
            s => s.DeckId == deckId && s.CapturedOn == today, ct);
        if (existing is not null)
            db.ProgressSnapshots.Remove(existing); // replaced below with the post-session figures

        await CaptureOneAsync(db, deck.Id, deck.CourseId, today, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task CaptureOneAsync(StudyDbContext db, Guid deckId, Guid courseId, DateOnly today, CancellationToken ct)
    {
        var cards = await db.Cards.AsNoTracking().Where(c => c.DeckId == deckId).ToListAsync(ct);
        if (cards.Count == 0)
            return; // nothing to show yet — no point in a row of zeros

        var windowStart = policy.RetentionWindowStartUtc();
        var logs = await db.ReviewLogs.AsNoTracking()
            .Where(l => l.Card!.DeckId == deckId && l.ReviewedAt >= windowStart)
            .ToListAsync(ct);

        var report = policy.Aggregate(cards, logs);

        db.ProgressSnapshots.Add(new ProgressSnapshot
        {
            DeckId = deckId,
            CourseId = courseId,
            CapturedOn = today,
            Total = report.Total,
            Unseen = report.Unseen,
            Learning = report.Learning,
            Young = report.Young,
            Mature = report.Mature,
            Mastery = report.Mastery,
            RecallNow = report.RecallNow,
            Retention = report.Retention,
        });
    }

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
}
