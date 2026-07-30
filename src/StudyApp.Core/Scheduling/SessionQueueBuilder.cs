using StudyApp.Core.Entities;

namespace StudyApp.Core.Scheduling;

/// <summary>
/// Builds a review session queue: due review-cards first, then new cards capped per session,
/// shuffled within each group so import order doesn't clump related cards together.
/// </summary>
public static class SessionQueueBuilder
{
    public const int MaxNewPerSession = 20;

    public static List<Card> Build(
        IEnumerable<Card> cards, DateTime dueCutoffUtc, int maxNew = MaxNewPerSession, Random? rng = null)
    {
        rng ??= Random.Shared;
        var pool = cards.Where(c => !c.IsDeleted).ToList();

        var due = pool
            .Where(c => c.State != CardState.New && c.Due is { } d && d < dueCutoffUtc)
            .OrderBy(_ => rng.Next())
            .ToList();
        var fresh = pool
            .Where(c => c.State == CardState.New)
            .OrderBy(_ => rng.Next())
            .Take(maxNew)
            .ToList();

        return [.. due, .. fresh];
    }

    /// <summary>
    /// A queue for drilling a narrow scope, which keeps the cards <see cref="Build"/> drops: those
    /// the scheduler has not asked for yet. Three tiers, in this order:
    ///
    /// <list type="number">
    /// <item>due reviews, shuffled — they are the ones that actually matter today;</item>
    /// <item>everything not yet due, <b>weakest memory first</b> by
    ///   <see cref="ProgressPolicy.Retrievability"/>. If the point of a drill is to find what has
    ///   gone soft, the card most likely to have been forgotten belongs at the front, and any other
    ///   order is arbitrary;</item>
    /// <item>new cards, shuffled and still capped.</item>
    /// </list>
    ///
    /// The cap survives here for the same reason <c>BuildAssignmentSessionAsync</c> keeps it: the
    /// moment you most want to meet sixty new cards at once is the moment it works worst. The
    /// caller is expected to report what was held back rather than truncate in silence.
    /// </summary>
    public static List<Card> BuildPractice(
        IEnumerable<Card> cards,
        DateTime dueCutoffUtc,
        ProgressPolicy progress,
        int maxNew = MaxNewPerSession,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var pool = cards.Where(c => !c.IsDeleted).ToList();

        var due = pool
            .Where(c => c.State != CardState.New && c.Due is { } d && d < dueCutoffUtc)
            .OrderBy(_ => rng.Next())
            .ToList();
        var early = pool
            .Where(c => c.State != CardState.New && (c.Due is null || c.Due >= dueCutoffUtc))
            .OrderBy(progress.Retrievability)
            .ToList();
        var fresh = pool
            .Where(c => c.State == CardState.New)
            .OrderBy(_ => rng.Next())
            .Take(maxNew)
            .ToList();

        return [.. due, .. early, .. fresh];
    }

    /// <summary>
    /// Whether a just-graded card should come round again later in this session.
    ///
    /// In ordinary review the answer is read off the card: <c>Again</c> (and <c>Hard</c> while
    /// learning) puts it into <see cref="CardState.Learning"/>, and a learning card is by definition
    /// unfinished. A practice grade deliberately leaves the card's state untouched, so that signal
    /// never appears — which would mean the card you just got wrong quietly did not come back, in
    /// precisely the situation you asked to drill it. So practice keys off the grade instead.
    /// </summary>
    public static bool Requeues(Card card, ReviewGrade grade, bool practice) =>
        practice ? grade == ReviewGrade.Again : card.State == CardState.Learning;
}
