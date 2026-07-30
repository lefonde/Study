using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;

namespace StudyApp.Core.Tests;

public class DuePolicyTests
{
    // Local time zone UTC+2. Local "now" = 2026-07-27 23:00, i.e. 21:00 UTC.
    private static readonly DateTimeOffset LateEvening = new(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    private static DuePolicy PolicyAt(DateTimeOffset utcNow) => new(new FakeTimeProvider(utcNow, Offset));

    private static Card ReviewCard(DateTime dueUtc) =>
        new() { State = CardState.Review, Due = dueUtc, IntervalDays = 1 };

    [Fact]
    public void TodayCutoff_Is_Next_Local_4AM_In_Utc()
    {
        // Study day rolls over at 04:00 local: next rollover = 2026-07-28 04:00 +02:00 = 02:00 UTC.
        Assert.Equal(new DateTime(2026, 7, 28, 2, 0, 0), PolicyAt(LateEvening).TodayCutoffUtc());
    }

    [Fact]
    public void Before_4AM_Still_Belongs_To_Previous_Study_Day()
    {
        // Local 01:00 on the 28th (23:00 UTC on the 27th) → cutoff is still 04:00 on the 28th.
        var policy = PolicyAt(new DateTimeOffset(2026, 7, 27, 23, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTime(2026, 7, 28, 2, 0, 0), policy.TodayCutoffUtc());
    }

    [Fact]
    public void Card_Due_Later_Today_Is_Due()
    {
        // Due 23:30 local (21:30 UTC) — still today.
        Assert.True(PolicyAt(LateEvening).IsDueToday(ReviewCard(LateEvening.UtcDateTime.AddMinutes(30))));
    }

    [Fact]
    public void Card_Reviewed_Late_Night_Is_Not_Due_Again_At_Midnight()
    {
        // Reviewed 23:00 local with +1 day → due tomorrow 23:00 local.
        var due = LateEvening.UtcDateTime.AddDays(1);

        // Five minutes past local midnight, the card must NOT be due.
        var justPastMidnight = PolicyAt(new DateTimeOffset(2026, 7, 27, 22, 5, 0, TimeSpan.Zero));
        Assert.False(justPastMidnight.IsDueToday(ReviewCard(due)));

        // But during the next local day it IS due (falls before that day's cutoff).
        var nextMorning = PolicyAt(new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero));
        Assert.True(nextMorning.IsDueToday(ReviewCard(due)));
    }

    [Fact]
    public void Card_Due_Next_Local_Day_Counts_As_Tomorrow()
    {
        var policy = PolicyAt(LateEvening);
        var due = LateEvening.UtcDateTime.AddDays(1); // tomorrow 23:00 local

        Assert.False(policy.IsDueToday(ReviewCard(due)));
        Assert.True(policy.IsDueTomorrow(ReviewCard(due)));
    }

    [Fact]
    public void New_Cards_Are_Never_Due()
    {
        var card = new Card { State = CardState.New, Due = null };
        Assert.False(PolicyAt(LateEvening).IsDueToday(card));
        Assert.False(PolicyAt(LateEvening).IsDueTomorrow(card));
    }

    // --- practice: reviewing something the scheduler hasn't asked for ---

    [Fact]
    public void A_Card_Due_Today_Is_Not_Ahead_Of_Schedule()
    {
        var policy = PolicyAt(LateEvening);
        var card = ReviewCard(LateEvening.UtcDateTime.AddMinutes(30));

        Assert.True(policy.IsDueToday(card));
        Assert.False(policy.IsAheadOfSchedule(card));
    }

    [Fact]
    public void A_Card_Due_Tomorrow_Is_Ahead_Of_Schedule()
    {
        var policy = PolicyAt(LateEvening);
        Assert.True(policy.IsAheadOfSchedule(ReviewCard(LateEvening.UtcDateTime.AddDays(1))));
    }

    /// <summary>
    /// Introducing a new card is what an ordinary session does. Counting it as practice would mean a
    /// scoped session could never teach anything new, since nothing would ever leave New.
    /// </summary>
    [Fact]
    public void A_New_Card_Is_Never_Ahead_Of_Schedule()
    {
        Assert.False(PolicyAt(LateEvening).IsAheadOfSchedule(new Card { State = CardState.New }));
    }

    [Fact]
    public void An_Overdue_Card_Is_Not_Ahead_Of_Schedule()
    {
        Assert.False(PolicyAt(LateEvening).IsAheadOfSchedule(
            ReviewCard(LateEvening.UtcDateTime.AddDays(-9))));
    }
}
