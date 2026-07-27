using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;

namespace StudyApp.Core.Tests;

public class Sm2SchedulerTests
{
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly Sm2Scheduler _scheduler = new();

    private static Card NewCard() => new() { Front = "f", Back = "b" };

    [Fact]
    public void NewCard_Good_Graduates_At_1_Day()
    {
        var card = NewCard();
        _scheduler.Apply(card, ReviewGrade.Good, Now);

        Assert.Equal(CardState.Review, card.State);
        Assert.Equal(1, card.Repetitions);
        Assert.Equal(1, card.IntervalDays);
        Assert.Equal(Now.AddDays(1), card.Due);
        Assert.Equal(2.5, card.EaseFactor);
    }

    [Fact]
    public void Second_Good_Gives_6_Days()
    {
        var card = NewCard();
        _scheduler.Apply(card, ReviewGrade.Good, Now);
        _scheduler.Apply(card, ReviewGrade.Good, Now.AddDays(1));

        Assert.Equal(6, card.IntervalDays);
        Assert.Equal(Now.AddDays(1).AddDays(6), card.Due);
    }

    [Fact]
    public void Third_Good_Multiplies_By_Ease()
    {
        var card = NewCard();
        _scheduler.Apply(card, ReviewGrade.Good, Now);
        _scheduler.Apply(card, ReviewGrade.Good, Now);
        _scheduler.Apply(card, ReviewGrade.Good, Now);

        Assert.Equal(15, card.IntervalDays); // 6 × 2.5
    }

    [Fact]
    public void Interval_Rounds_AwayFromZero()
    {
        var card = NewCard();
        _scheduler.Apply(card, ReviewGrade.Good, Now);
        _scheduler.Apply(card, ReviewGrade.Good, Now);
        _scheduler.Apply(card, ReviewGrade.Good, Now);
        _scheduler.Apply(card, ReviewGrade.Good, Now);

        Assert.Equal(38, card.IntervalDays); // 15 × 2.5 = 37.5 → 38
    }

    [Fact]
    public void Review_Hard_Multiplies_1_2_And_Reduces_Ease()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.IntervalDays = 10;
        card.Repetitions = 3;

        _scheduler.Apply(card, ReviewGrade.Hard, Now);

        Assert.Equal(12, card.IntervalDays);
        Assert.Equal(2.35, card.EaseFactor, 3);
    }

    [Fact]
    public void Review_Hard_Grows_By_At_Least_One_Day()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.IntervalDays = 1;
        card.Repetitions = 3;

        _scheduler.Apply(card, ReviewGrade.Hard, Now);

        Assert.Equal(2, card.IntervalDays); // 1 × 1.2 = 1.2, but min +1 day
    }

    [Fact]
    public void Review_Easy_Boosts_Interval_And_Ease()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.IntervalDays = 10;
        card.Repetitions = 3;

        _scheduler.Apply(card, ReviewGrade.Easy, Now);

        Assert.Equal(33, card.IntervalDays); // 10 × 2.5 × 1.3 = 32.5 → 33
        Assert.Equal(2.65, card.EaseFactor, 3);
    }

    [Fact]
    public void Review_Again_Lapses_Resets_And_Penalizes_Ease()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.IntervalDays = 10;
        card.Repetitions = 3;

        _scheduler.Apply(card, ReviewGrade.Again, Now);

        Assert.Equal(CardState.Learning, card.State);
        Assert.Equal(1, card.Lapses);
        Assert.Equal(0, card.Repetitions);
        Assert.Equal(0, card.IntervalDays);
        Assert.Equal(Now, card.Due);
        Assert.Equal(2.3, card.EaseFactor, 3);
    }

    [Fact]
    public void Relearned_Card_Starts_Over_At_1_Day()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.IntervalDays = 10;
        card.Repetitions = 5;

        _scheduler.Apply(card, ReviewGrade.Again, Now);
        _scheduler.Apply(card, ReviewGrade.Good, Now);

        Assert.Equal(CardState.Review, card.State);
        Assert.Equal(1, card.IntervalDays);
    }

    [Fact]
    public void Ease_Never_Falls_Below_Floor()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.IntervalDays = 10;
        card.Repetitions = 3;
        card.EaseFactor = 1.35;

        _scheduler.Apply(card, ReviewGrade.Hard, Now);

        Assert.Equal(Sm2Scheduler.MinEase, card.EaseFactor);
    }

    [Fact]
    public void Interval_Is_Capped_At_365_Days()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.IntervalDays = 300;
        card.Repetitions = 5;

        _scheduler.Apply(card, ReviewGrade.Good, Now);

        Assert.Equal(Sm2Scheduler.MaxIntervalDays, card.IntervalDays);
    }

    [Fact]
    public void Learning_Again_Stays_Due_Now_Without_Ease_Change()
    {
        var card = NewCard();
        _scheduler.Apply(card, ReviewGrade.Again, Now);

        Assert.Equal(CardState.Learning, card.State);
        Assert.Equal(Now, card.Due);
        Assert.Equal(2.5, card.EaseFactor);
        Assert.Equal(0, card.Lapses); // learning-stage Again is not a lapse
    }

    [Fact]
    public void Learning_Hard_Repeats_In_Session()
    {
        var card = NewCard();
        _scheduler.Apply(card, ReviewGrade.Hard, Now);

        Assert.Equal(CardState.Learning, card.State);
        Assert.Equal(Now, card.Due);
        Assert.Equal(0, card.Repetitions);
    }

    [Fact]
    public void NewCard_Easy_Graduates_At_4_Days_With_Ease_Bonus()
    {
        var card = NewCard();
        _scheduler.Apply(card, ReviewGrade.Easy, Now);

        Assert.Equal(CardState.Review, card.State);
        Assert.Equal(Sm2Scheduler.EasyGraduationDays, card.IntervalDays);
        Assert.Equal(2.65, card.EaseFactor, 3);
    }
}
