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
}
