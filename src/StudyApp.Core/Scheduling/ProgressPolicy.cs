using StudyApp.Core.Entities;

namespace StudyApp.Core.Scheduling;

/// <summary>Where a card sits on the road from unseen to durably learned.</summary>
public enum CardBucket
{
    Unseen,
    Learning,
    /// <summary>In Review, but its interval hasn't reached <see cref="ProgressPolicy.MaturityDays"/> yet.</summary>
    Young,
    /// <summary>In Review with an interval at or beyond <see cref="ProgressPolicy.MaturityDays"/> — durably learned.</summary>
    Mature,
}

/// <summary>Progress for one set of cards — a deck, a unit, or a whole course, all the same shape.</summary>
public record ProgressReport(
    int Total,
    int Unseen,
    int Learning,
    int Young,
    int Mature,
    int Overdue,
    int Leeches,
    double Mastery,
    double RecallNow,
    double? Retention)
{
    public static ProgressReport Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>What a deck's cards look like on a target date if review keeps up as it has.</summary>
public record ProgressForecast(int AlreadyDurable, int WillLapseBeforeTarget, double ProjectedMastery);

/// <summary>
/// The single definition of "progress" — deliberately built only from data every scheduler
/// produces (<see cref="Card.IntervalDays"/>, <see cref="Card.Lapses"/>, <see cref="Card.State"/>,
/// <see cref="ReviewLog"/>), never from <see cref="Card.EaseFactor"/>, which is specific to
/// <see cref="Sm2Scheduler"/> and won't exist once FSRS replaces it behind <see cref="IScheduler"/>.
///
/// Progress is two independent axes, not one number:
///   - <b>Mastery</b> (durability) — how well-established the memory is. Only moves when you study.
///   - <b>RecallNow</b> (retrievability) — probability you'd get it right this instant. Decays
///     with elapsed time even if you never open the app, by design: forgetting is real.
/// Collapsing these into one score would either hide a wall of overdue cards behind a good
/// mastery number, or make "progress" fall while the user sleeps — both wrong.
/// </summary>
public class ProgressPolicy(TimeProvider timeProvider)
{
    /// <summary>Anki's long-standing convention for "durably learned": interval ≥ 21 days.</summary>
    public const int MaturityDays = 21;

    /// <summary>Anki's leech threshold — a card that has cost 8+ lapses is wasting review time.</summary>
    public const int LeechLapses = 8;

    /// <summary>
    /// The recall probability a scheduler targets when a card comes due. SM-2 and FSRS both aim
    /// for ~90% at the scheduled review point, so this is the correct anchor for either.
    /// </summary>
    public const double TargetRetrievability = 0.9;

    public CardBucket BucketOf(Card card) => card.State switch
    {
        CardState.New => CardBucket.Unseen,
        CardState.Learning => CardBucket.Learning,
        _ => card.IntervalDays >= MaturityDays ? CardBucket.Mature : CardBucket.Young,
    };

    public bool IsLeech(Card card) => card.Lapses >= LeechLapses;

    public bool IsOverdue(Card card) =>
        card.State != CardState.New && card.Due is { } due && due < timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Durability in [0, 1] — the interval scaled against <see cref="MaturityDays"/>. New and
    /// Learning cards score 0: nothing has been proven durable yet.
    /// </summary>
    public static double Durability(Card card) =>
        card.State is CardState.New or CardState.Learning
            ? 0.0
            : Math.Clamp(card.IntervalDays / MaturityDays, 0.0, 1.0);

    /// <summary>
    /// Probability of recall right now, via the exponential forgetting curve anchored at
    /// <see cref="TargetRetrievability"/> when elapsed time equals the card's own interval — the
    /// point the scheduler chose specifically so recall would sit there. A card exactly on
    /// schedule always reads ~90% regardless of how long its interval is.
    /// </summary>
    public double Retrievability(Card card)
    {
        if (card.State is CardState.New or CardState.Learning || card.IntervalDays <= 0)
            return 0.0;

        var due = card.Due ?? timeProvider.GetUtcNow().UtcDateTime;
        var elapsedDays = (timeProvider.GetUtcNow().UtcDateTime - (due - TimeSpan.FromDays(card.IntervalDays))).TotalDays;
        if (elapsedDays <= 0)
            return 1.0;

        return Math.Pow(TargetRetrievability, elapsedDays / card.IntervalDays);
    }

    /// <summary>
    /// Rolls any set of cards up into one report. The same function serves a deck, a unit
    /// (cards across its decks), or a whole course — that reuse is the reason this exists as a
    /// pure aggregation over an arbitrary sequence rather than a deck-shaped query.
    /// </summary>
    public ProgressReport Aggregate(IEnumerable<Card> cards, IReadOnlyList<ReviewLog>? recentLogs = null)
    {
        var list = cards as IReadOnlyCollection<Card> ?? cards.ToList();
        if (list.Count == 0)
            return ProgressReport.Empty;

        int unseen = 0, learning = 0, young = 0, mature = 0, overdue = 0, leeches = 0;
        double masterySum = 0, recallSum = 0;

        foreach (var card in list)
        {
            switch (BucketOf(card))
            {
                case CardBucket.Unseen: unseen++; break;
                case CardBucket.Learning: learning++; break;
                case CardBucket.Young: young++; break;
                case CardBucket.Mature: mature++; break;
            }
            if (IsOverdue(card)) overdue++;
            if (IsLeech(card)) leeches++;
            masterySum += Durability(card);
            recallSum += Retrievability(card);
        }

        double? retention = null;
        if (recentLogs is { Count: > 0 })
        {
            var graded = recentLogs.Count;
            var successful = recentLogs.Count(l => l.Grade != ReviewGrade.Again);
            retention = (double)successful / graded;
        }

        return new ProgressReport(
            list.Count, unseen, learning, young, mature, overdue, leeches,
            Mastery: masterySum / list.Count,
            RecallNow: recallSum / list.Count,
            Retention: retention);
    }

    /// <summary>
    /// Reviews from the trailing window ending now — the input <see cref="Aggregate"/> expects
    /// for its <c>Retention</c> figure. Callers fetch the rows (a DB query); this just defines
    /// the window so every caller uses the same one.
    /// </summary>
    public DateTime RetentionWindowStartUtc(int days = 30) =>
        timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);

    /// <summary>
    /// Where a card set is likely to stand on <paramref name="targetDate"/> if review continues
    /// as it has: a card whose current due date already falls on/after the target needs no more
    /// work to still be durable then; one due before it will lapse without another review.
    /// This is a projection under a stated assumption, not a promise — surface it that way.
    /// </summary>
    public ProgressForecast Forecast(IEnumerable<Card> cards, DateTime targetDateUtc)
    {
        var list = cards as IReadOnlyCollection<Card> ?? cards.ToList();
        if (list.Count == 0)
            return new ProgressForecast(0, 0, 0);

        var durable = 0;
        var willLapse = 0;
        var masterySum = 0.0;

        foreach (var card in list)
        {
            var staysDurable = card.State == CardState.Review
                                && card.Due is { } due && due >= targetDateUtc;
            if (staysDurable)
            {
                durable++;
                masterySum += Durability(card);
            }
            else
            {
                willLapse++;
            }
        }

        return new ProgressForecast(durable, willLapse, masterySum / list.Count);
    }
}
