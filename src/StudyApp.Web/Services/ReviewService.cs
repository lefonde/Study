using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

/// <summary>Scheduling fields of a card frozen at a point in time; used for one-step undo.</summary>
public record CardScheduleSnapshot(
    CardState State, DateTime? Due, double IntervalDays, double EaseFactor, int Repetitions, int Lapses)
{
    public static CardScheduleSnapshot From(Card c) =>
        new(c.State, c.Due, c.IntervalDays, c.EaseFactor, c.Repetitions, c.Lapses);

    public void ApplyTo(Card c) =>
        (c.State, c.Due, c.IntervalDays, c.EaseFactor, c.Repetitions, c.Lapses) =
        (State, Due, IntervalDays, EaseFactor, Repetitions, Lapses);
}

public record GradeResult(CardScheduleSnapshot Before, Guid LogId);

public record CourseDueSummary(Guid CourseId, string Name, string Color, int Due, int New, int DueTomorrow)
{
    public int Total => Due + New;
}

public record HomeSummary(List<CourseDueSummary> Courses)
{
    public int TotalDue => Courses.Sum(c => c.Due);
    public int TotalNew => Courses.Sum(c => c.New);
    public int TotalDueTomorrow => Courses.Sum(c => c.DueTomorrow);
    public bool HasAnythingToStudy => TotalDue + TotalNew > 0;
}

public class ReviewService(
    IDbContextFactory<StudyDbContext> factory,
    IScheduler scheduler,
    DuePolicy duePolicy,
    TimeProvider timeProvider)
{
    public async Task<List<Card>> BuildSessionAsync(Guid? courseId = null, Guid? deckId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cutoff = duePolicy.TodayCutoffUtc();

        var query = db.Cards.AsNoTracking().AsQueryable();
        if (deckId is { } d)
            query = query.Where(c => c.DeckId == d);
        else if (courseId is { } co)
            query = query.Where(c => c.Deck!.CourseId == co);

        var candidates = await query
            .Where(c => c.State == CardState.New || (c.Due != null && c.Due < cutoff))
            .ToListAsync();

        return SessionQueueBuilder.Build(candidates, cutoff);
    }

    /// <summary>Grades a card: applies the scheduler, logs the review, persists. Returns undo info.</summary>
    public async Task<GradeResult> GradeAsync(Card card, ReviewGrade grade)
    {
        await using var db = await factory.CreateDbContextAsync();
        var tracked = await db.Cards.FirstAsync(c => c.Id == card.Id);
        var before = CardScheduleSnapshot.From(tracked);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        scheduler.Apply(tracked, grade, now);

        var log = new ReviewLog
        {
            CardId = tracked.Id,
            ReviewedAt = now,
            Grade = grade,
            IntervalBeforeDays = before.IntervalDays,
            IntervalAfterDays = tracked.IntervalDays,
        };
        db.ReviewLogs.Add(log);
        await db.SaveChangesAsync();

        CardScheduleSnapshot.From(tracked).ApplyTo(card); // keep the UI copy in sync
        return new GradeResult(before, log.Id);
    }

    /// <summary>Reverts the last grade: restores scheduling state and removes the log entry.</summary>
    public async Task UndoAsync(Card card, GradeResult last)
    {
        await using var db = await factory.CreateDbContextAsync();
        var tracked = await db.Cards.FirstAsync(c => c.Id == card.Id);
        last.Before.ApplyTo(tracked);

        var log = await db.ReviewLogs.FindAsync(last.LogId);
        if (log is not null)
            db.ReviewLogs.Remove(log);

        await db.SaveChangesAsync();
        last.Before.ApplyTo(card);
    }

    public async Task<HomeSummary> GetHomeSummaryAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var today = duePolicy.TodayCutoffUtc();
        var tomorrow = duePolicy.TomorrowCutoffUtc();

        var rows = await db.Cards.AsNoTracking()
            .Select(c => new
            {
                c.Deck!.CourseId,
                c.Deck.Course!.Name,
                c.Deck.Course.Color,
                c.State,
                c.Due,
            })
            .ToListAsync();

        var courses = rows
            .GroupBy(r => (r.CourseId, r.Name, r.Color))
            .Select(g => new CourseDueSummary(
                g.Key.CourseId,
                g.Key.Name,
                g.Key.Color,
                g.Count(r => r.State != CardState.New && r.Due < today),
                g.Count(r => r.State == CardState.New),
                g.Count(r => r.State != CardState.New && r.Due >= today && r.Due < tomorrow)))
            .OrderBy(c => c.Name)
            .ToList();

        return new HomeSummary(courses);
    }
}
