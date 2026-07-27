using StudyApp.Core.Entities;

namespace StudyApp.Core.Scheduling;

/// <summary>
/// The single definition of "due". A card is due today if its Due timestamp (UTC) falls
/// before the end of the current local *study day*. The study day rolls over at 04:00
/// local rather than midnight (Anki-style), so a card reviewed at 23:00 does not come
/// due again the moment the clock passes 12, and a 1 AM session still counts as "tonight".
/// All time access goes through the injected TimeProvider so tests can fake it.
/// </summary>
public class DuePolicy(TimeProvider timeProvider)
{
    public const int DayRolloverHourLocal = 4;

    /// <summary>End of the current local study day (next local 04:00), expressed in UTC.</summary>
    public DateTime TodayCutoffUtc()
    {
        var localNow = timeProvider.GetLocalNow();
        var rollover = localNow.Date.AddHours(DayRolloverHourLocal);
        if (localNow.DateTime >= rollover)
            rollover = rollover.AddDays(1);
        // Uses the current offset; may be off by an hour on a DST transition day. Acceptable.
        return new DateTimeOffset(rollover, localNow.Offset).UtcDateTime;
    }

    /// <summary>End of the next local study day, expressed in UTC.</summary>
    public DateTime TomorrowCutoffUtc() => TodayCutoffUtc().AddDays(1);

    public bool IsDueToday(Card card) =>
        card.State != CardState.New && card.Due is { } due && due < TodayCutoffUtc();

    public bool IsDueTomorrow(Card card) =>
        card.State != CardState.New && card.Due is { } due
        && due >= TodayCutoffUtc() && due < TomorrowCutoffUtc();
}
