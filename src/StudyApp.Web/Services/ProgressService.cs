using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

public record DeckProgressRow(Guid DeckId, string DeckName, ProgressReport Report);
public record UnitProgressRow(Guid UnitId, string UnitTitle, ProgressReport Report);

/// <summary>
/// Reads and captures study progress. All figures come from <see cref="ProgressPolicy"/> —
/// this service only supplies the card sets: a deck's cards, a unit's (its decks' cards plus
/// any card individually tagged to it), or a course's (every card in it). Same aggregation
/// function throughout, which is the point — the rollup isn't a separate calculation, it's the
/// same one over a wider set.
/// </summary>
public class ProgressService(IDbContextFactory<StudyDbContext> factory, ProgressPolicy policy, TimeProvider timeProvider)
{
    public async Task<ProgressReport> GetDeckProgressAsync(Guid deckId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cards = await db.Cards.AsNoTracking().Where(c => c.DeckId == deckId).ToListAsync();
        var logs = await RecentLogsAsync(db, l => l.Card!.DeckId == deckId);
        return policy.Aggregate(cards, logs);
    }

    /// <summary>
    /// A card counts toward a unit if it's explicitly tagged there (<see cref="Card.UnitId"/> —
    /// set on AI-generated cards, or as a manual override), otherwise if it falls back to its
    /// deck's unit (<see cref="Deck.UnitId"/>). The override exists because a deck can span more
    /// than one lesson; most cards just inherit their deck's unit.
    /// </summary>
    public async Task<ProgressReport> GetUnitProgressAsync(Guid unitId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.UnitId == unitId || (c.UnitId == null && c.Deck!.UnitId == unitId))
            .ToListAsync();
        var logs = await RecentLogsAsync(db, l =>
            l.Card!.UnitId == unitId || (l.Card.UnitId == null && l.Card.Deck!.UnitId == unitId));
        return policy.Aggregate(cards, logs);
    }

    public async Task<ProgressReport> GetCourseProgressAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cards = await db.Cards.AsNoTracking().Where(c => c.Deck!.CourseId == courseId).ToListAsync();
        var logs = await RecentLogsAsync(db, l => l.Card!.Deck!.CourseId == courseId);
        return policy.Aggregate(cards, logs);
    }

    /// <summary>Per-deck breakdown for a course — the Decks tab.</summary>
    public async Task<List<DeckProgressRow>> GetDecksProgressAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var decks = await db.Decks.AsNoTracking()
            .Where(d => d.CourseId == courseId)
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();

        var rows = new List<DeckProgressRow>();
        foreach (var deck in decks)
            rows.Add(new DeckProgressRow(deck.Id, deck.Name, await GetDeckProgressAsync(deck.Id)));
        return rows;
    }

    /// <summary>Per-unit breakdown for a course — the Structure tab. Skips units with no cards.</summary>
    public async Task<List<UnitProgressRow>> GetUnitsProgressAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var units = await db.CourseUnits.AsNoTracking()
            .Where(u => u.CourseId == courseId)
            .OrderBy(u => u.Order)
            .Select(u => new { u.Id, u.Title })
            .ToListAsync();

        var rows = new List<UnitProgressRow>();
        foreach (var unit in units)
        {
            var report = await GetUnitProgressAsync(unit.Id);
            if (report.Total > 0)
                rows.Add(new UnitProgressRow(unit.Id, unit.Title, report));
        }
        return rows;
    }

    /// <summary>Snapshot history for a deck's trend line, oldest first.</summary>
    public async Task<List<ProgressSnapshot>> GetTrendAsync(Guid deckId, int days = 30)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cutoff = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime).AddDays(-days);
        return await db.ProgressSnapshots.AsNoTracking()
            .Where(s => s.DeckId == deckId && s.CapturedOn >= cutoff)
            .OrderBy(s => s.CapturedOn)
            .ToListAsync();
    }

    public async Task<ProgressForecast> GetForecastAsync(Guid deckId, DateTime targetDateUtc)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cards = await db.Cards.AsNoTracking().Where(c => c.DeckId == deckId).ToListAsync();
        return policy.Forecast(cards, targetDateUtc);
    }

    /// <summary>Whole-course version of <see cref="GetForecastAsync"/>, for readiness notes against an assignment due date.</summary>
    public async Task<ProgressForecast> GetCourseForecastAsync(Guid courseId, DateTime targetDateUtc)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cards = await db.Cards.AsNoTracking().Where(c => c.Deck!.CourseId == courseId).ToListAsync();
        return policy.Forecast(cards, targetDateUtc);
    }

    /// <summary>
    /// Practice reviews are excluded, which is the only reason <see cref="ReviewLog.IsPractice"/>
    /// exists. The single figure these logs feed is <c>Retention</c> — "did you recall it when the
    /// scheduler predicted you were about to forget" — and a card you chose to drill early was never
    /// asked that question. Including them would lift retention a little every time a topic was
    /// drilled, which is the one direction a memory statistic must not drift for free.
    /// </summary>
    private async Task<List<ReviewLog>> RecentLogsAsync(
        StudyDbContext db, System.Linq.Expressions.Expression<Func<ReviewLog, bool>> scope)
    {
        var windowStart = policy.RetentionWindowStartUtc();
        return await db.ReviewLogs.AsNoTracking()
            .Where(l => l.ReviewedAt >= windowStart && !l.IsPractice)
            .Where(scope)
            .ToListAsync();
    }
}
