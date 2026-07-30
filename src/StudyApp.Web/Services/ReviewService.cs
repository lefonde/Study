using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Planning;
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

/// <summary>
/// <paramref name="WasPractice"/> says the card was graded ahead of its due date and its schedule
/// was left untouched. The caller needs it because the "does this card come round again" rule
/// differs — see <see cref="SessionQueueBuilder.Requeues"/>.
/// </summary>
public record GradeResult(CardScheduleSnapshot Before, Guid LogId, bool WasPractice);

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

/// <summary>
/// A review session narrowed to one assignment. <paramref name="NewHeldBack"/> is what the
/// per-session new-card cap kept out, so the page can say so rather than silently truncating.
/// </summary>
public record AssignmentSession(
    List<Card> Queue,
    string Title,
    Guid CourseId,
    DateTime DueDateUtc,
    int CardsNeedingWork,
    int NewHeldBack);

/// <summary>
/// A session over one slice of the collection. <paramref name="Title"/> is the scope's own name and
/// may be markdown with LaTeX (topic names are), so display it through the inline-markdown recipe
/// rather than as plain text.
///
/// The three counts exist so the page can be honest about what it did and did not put in front of
/// you: <paramref name="InScope"/> is everything the scope holds, <paramref name="NewHeldBack"/> is
/// what the per-session new-card cap kept out, and <paramref name="PracticeCount"/> is how many
/// queued cards are ahead of schedule and will therefore be graded without their schedule moving.
/// </summary>
public record ScopedSession(
    List<Card> Queue,
    string Title,
    string? Note,
    Guid? CourseId,
    int InScope,
    int DueNow,
    int NewHeldBack,
    int PracticeCount);

public class ReviewService(
    IDbContextFactory<StudyDbContext> factory,
    IScheduler scheduler,
    DuePolicy duePolicy,
    ProgressPolicy progress,
    TimeProvider timeProvider)
{
    /// <summary>
    /// The daily session, or one narrowed to a course or deck. Kept as-is for the callers that only
    /// ever mean those three; it is a wrapper over <see cref="BuildScopedSessionAsync"/> so there is
    /// one code path behind every scope.
    /// </summary>
    public async Task<List<Card>> BuildSessionAsync(Guid? courseId = null, Guid? deckId = null)
    {
        var scope = deckId is { } d ? ReviewScope.ForDeck(d)
            : courseId is { } co ? ReviewScope.ForCourse(co)
            : ReviewScope.Everything;

        return (await BuildScopedSessionAsync(scope)).Queue;
    }

    /// <summary>
    /// Builds a session over whichever slice of the collection was asked for — a course, a deck, a
    /// chapter and everything under it, one topic, or one rung of the learning path.
    ///
    /// <paramref name="practiseAll"/> keeps the cards a normal session drops: those the scheduler
    /// has not asked for yet. Grading one of those leaves its schedule alone (see
    /// <see cref="DuePolicy.IsAheadOfSchedule"/> and <see cref="GradeAsync"/>), so this is safe to
    /// offer on any scope narrow enough that most of it will never be due on a given day — which is
    /// exactly the scopes that were previously impossible to study at all.
    /// </summary>
    public async Task<ScopedSession> BuildScopedSessionAsync(
        ReviewScope scope, bool practiseAll = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cutoff = duePolicy.TodayCutoffUtc();

        var (query, title, note, courseId) = await ResolveScopeAsync(db, scope);

        // Only practice needs the cards a normal session would drop, so only practice pays to fetch
        // them. The daily session keeps the same SQL pre-filter it always had — it runs over every
        // card the user owns, and materialising all of them to throw most away would be a poor trade
        // for a count the banner does not use.
        var candidates = practiseAll
            ? await query.ToListAsync()
            : await query
                .Where(c => c.State == CardState.New || (c.Due != null && c.Due < cutoff))
                .ToListAsync();

        var queue = practiseAll
            ? SessionQueueBuilder.BuildPractice(candidates, cutoff, progress)
            : SessionQueueBuilder.Build(candidates, cutoff);

        return new ScopedSession(
            queue,
            title,
            note,
            courseId,
            InScope: await query.CountAsync(),
            DueNow: await query.CountAsync(c =>
                c.State != CardState.New && c.Due != null && c.Due < cutoff),
            NewHeldBack: candidates.Count(c => c.State == CardState.New)
                         - queue.Count(c => c.State == CardState.New),
            PracticeCount: queue.Count(duePolicy.IsAheadOfSchedule));
    }

    /// <summary>
    /// The cards a scope holds, as a query rather than a list, so the caller can decide how much of
    /// it to materialise and can count the rest in SQL.
    /// </summary>
    private async Task<(IQueryable<Card> Query, string Title, string? Note, Guid? CourseId)>
        ResolveScopeAsync(StudyDbContext db, ReviewScope scope)
    {
        switch (scope.Kind)
        {
            case ReviewScopeKind.Deck when scope.Id is { } deckId:
            {
                var deck = await db.Decks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deckId);
                return (db.Cards.AsNoTracking().Where(c => c.DeckId == deckId),
                    deck?.Name ?? "This deck", null, deck?.CourseId);
            }

            case ReviewScopeKind.Course when scope.Id is { } courseId:
            {
                var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId);
                return (db.Cards.AsNoTracking().Where(c => c.Deck!.CourseId == courseId),
                    course?.Name ?? "This course", null, courseId);
            }

            case ReviewScopeKind.Unit when scope.Id is { } unitId:
            {
                var unit = await db.CourseUnits.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId);
                if (unit is null)
                    return (Nothing(db), "This chapter", null, null);

                // A chapter means its lessons too — asking to study chapter 3 and getting only the
                // cards pinned to the chapter node, while its lessons sit untouched, is not what
                // anyone means by it.
                var units = await db.CourseUnits.AsNoTracking()
                    .Where(u => u.CourseId == unit.CourseId).ToListAsync();
                var ids = UnitTree.WithDescendants(units, unitId).ToList();

                // The same fallback ProgressService.GetUnitProgressAsync uses: an explicit
                // Card.UnitId wins, otherwise the card inherits its deck's unit. Reused rather than
                // rewritten, or review and progress would disagree about what a chapter contains.
                var query = db.Cards.AsNoTracking()
                    .Where(c => (c.UnitId != null && ids.Contains(c.UnitId.Value))
                                || (c.UnitId == null && c.Deck!.UnitId != null
                                    && ids.Contains(c.Deck.UnitId.Value)));

                var kind = unit.Kind == CourseUnitKind.Chapter ? "chapter" : "lesson";
                var note = ids.Count > 1
                    ? $"This {kind} and the {ids.Count - 1} unit(s) under it."
                    : null;
                return (query, unit.Title, note, unit.CourseId);
            }

            case ReviewScopeKind.Topic when scope.Id is { } topicId:
            {
                var topic = await db.CourseTopics.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == topicId);
                if (topic is null)
                    return (Nothing(db), "This topic", null, null);

                return (CardsForTopics(db, [topicId]), topic.Name, null, topic.CourseId);
            }

            case ReviewScopeKind.Stage when scope.CourseId is { } stageCourseId:
            {
                var topicIds = await StageTopicIdsAsync(db, stageCourseId, scope.Stage);
                var note = topicIds.Count > 0
                    ? $"{topicIds.Count} topic(s) at this point in the learning path."
                    : null;
                return (CardsForTopics(db, topicIds), $"Stage {scope.Stage + 1}", note, stageCourseId);
            }

            default:
                return (db.Cards.AsNoTracking(), "Everything", null, null);
        }
    }

    /// <summary>An empty scope, for an id that no longer resolves to anything.</summary>
    private static IQueryable<Card> Nothing(StudyDbContext db) =>
        db.Cards.AsNoTracking().Where(_ => false);

    /// <summary>
    /// Cards that teach any of these topics, through the <see cref="CardTopic"/> links.
    ///
    /// Only cards that have been linked are reachable this way — by gap-targeted generation or by
    /// the coverage job — so a topic-shaped scope on a course that was never linked comes back empty
    /// rather than wrong. The picker warns about that; here it must simply not pretend otherwise.
    /// </summary>
    private static IQueryable<Card> CardsForTopics(
        StudyDbContext db, IReadOnlyCollection<Guid> topicIds)
    {
        if (topicIds.Count == 0)
            return Nothing(db);

        var ids = topicIds.ToList();
        return db.Cards.AsNoTracking()
            .Where(c => db.CardTopics.Any(ct => ids.Contains(ct.CourseTopicId) && ct.CardId == c.Id));
    }

    /// <summary>
    /// The topics sitting at one rung of the learning path.
    ///
    /// Built from the same input the Map tab's Path view uses — non-dismissed topics and the edges
    /// they carry — so the stage numbering here and the stage numbering you clicked always agree. A
    /// stage index is derived, never stored, which is why this is recomputed rather than looked up.
    /// </summary>
    private static async Task<List<Guid>> StageTopicIdsAsync(
        StudyDbContext db, Guid courseId, int stage)
    {
        var topics = await db.CourseTopics.AsNoTracking()
            .Include(t => t.Prerequisites)
            .Where(t => t.CourseId == courseId && !t.IsDismissed)
            .ToListAsync();

        var map = PrerequisiteGraph.Build(topics, PrerequisiteGraph.EdgesOf(topics));

        return [.. map.Stages
            .FirstOrDefault(s => s.Index == stage)?.Topics.Select(t => t.Id) ?? []];
    }

    /// <summary>
    /// Builds a session over just the cards that teach what one assignment assesses. Returns null
    /// when the material isn't a dated assignment, so the page can explain rather than show an
    /// empty queue.
    ///
    /// Unlike a normal session this does <b>not</b> filter on "due today". Preparing for a
    /// deadline means clearing everything that won't still be durable on the day, which is what
    /// <see cref="StudyPlanPolicy.NeedsWorkBefore"/> decides — the same predicate the written plan
    /// counts with, so the two can never disagree about how much work is left.
    /// </summary>
    public async Task<AssignmentSession?> BuildAssignmentSessionAsync(Guid materialId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var material = await db.Materials.AsNoTracking().FirstOrDefaultAsync(m => m.Id == materialId);
        if (material?.DueDate is not { } due)
            return null;

        var target = DateTime.SpecifyKind(due.Date, DateTimeKind.Utc);

        var topicIds = await db.TopicSources.AsNoTracking()
            .Where(s => s.MaterialId == materialId && s.Mention == TopicMention.Assessed)
            .Select(s => s.CourseTopicId)
            .Distinct()
            .ToListAsync();

        var cardIds = topicIds.Count == 0
            ? []
            : await db.CardTopics.AsNoTracking()
                .Where(ct => topicIds.Contains(ct.CourseTopicId))
                .Select(ct => ct.CardId)
                .Distinct()
                .ToListAsync();

        var cards = cardIds.Count == 0
            ? []
            : await db.Cards.AsNoTracking().Where(c => cardIds.Contains(c.Id)).ToListAsync();

        var needingWork = cards.Where(c => StudyPlanPolicy.NeedsWorkBefore(c, target)).ToList();

        // The builder re-applies its own due filter, so hand it a cutoff past the deadline —
        // needingWork has already made the real decision about what belongs in this session.
        // What the builder is still wanted for is the shuffle and the new-card cap.
        var queue = SessionQueueBuilder.Build(needingWork, target.AddDays(1));

        var newHeldBack = needingWork.Count(c => c.State == CardState.New)
                          - queue.Count(c => c.State == CardState.New);

        return new AssignmentSession(
            queue, material.Title, material.CourseId, target, needingWork.Count, newHeldBack);
    }

    /// <summary>
    /// Grades a card: applies the scheduler, logs the review, persists. Returns undo info.
    ///
    /// A card graded <i>ahead of its due date</i> is practice, and the scheduler is deliberately not
    /// applied: you chose to drill it, which says nothing about whether its interval was right, and
    /// letting an early success stretch the interval would make repeated drilling inflate both the
    /// schedule and the mastery figure built from it. The review is still recorded — the work
    /// happened — flagged so retention statistics can leave it out.
    ///
    /// The decision is per card rather than per session, so a scope holding a few due cards among
    /// many early ones does the right thing for each without the caller passing a mode down.
    /// </summary>
    public async Task<GradeResult> GradeAsync(Card card, ReviewGrade grade)
    {
        await using var db = await factory.CreateDbContextAsync();
        var tracked = await db.Cards.FirstAsync(c => c.Id == card.Id);
        var before = CardScheduleSnapshot.From(tracked);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var practice = duePolicy.IsAheadOfSchedule(tracked);
        if (!practice)
            scheduler.Apply(tracked, grade, now);

        var log = new ReviewLog
        {
            CardId = tracked.Id,
            ReviewedAt = now,
            Grade = grade,
            IntervalBeforeDays = before.IntervalDays,
            IntervalAfterDays = tracked.IntervalDays,
            IsPractice = practice,
        };
        db.ReviewLogs.Add(log);
        await db.SaveChangesAsync();

        CardScheduleSnapshot.From(tracked).ApplyTo(card); // keep the UI copy in sync
        return new GradeResult(before, log.Id, practice);
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
