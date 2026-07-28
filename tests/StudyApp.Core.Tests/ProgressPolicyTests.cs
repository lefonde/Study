using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;

namespace StudyApp.Core.Tests;

public class ProgressPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private static ProgressPolicy PolicyAt(DateTimeOffset utcNow) => new(new FakeTimeProvider(utcNow));
    private static ProgressPolicy Policy => PolicyAt(Now);

    private static Card NewCard() => new() { State = CardState.New };

    private static Card LearningCard() =>
        new() { State = CardState.Learning, Due = Now.UtcDateTime, IntervalDays = 0 };

    /// <summary>A Review card exactly on schedule: last reviewed `intervalDays` ago, due exactly now.</summary>
    private static Card ReviewCard(double intervalDays, int lapses = 0) => new()
    {
        State = CardState.Review,
        IntervalDays = intervalDays,
        Due = Now.UtcDateTime,
        Lapses = lapses,
    };

    // --- Durability ---

    [Fact]
    public void New_And_Learning_Cards_Have_Zero_Durability()
    {
        Assert.Equal(0.0, ProgressPolicy.Durability(NewCard()));
        Assert.Equal(0.0, ProgressPolicy.Durability(LearningCard()));
    }

    [Fact]
    public void Durability_At_Maturity_Threshold_Is_Full()
    {
        Assert.Equal(1.0, ProgressPolicy.Durability(ReviewCard(ProgressPolicy.MaturityDays)));
    }

    [Fact]
    public void Durability_Scales_Linearly_Below_Maturity()
    {
        var half = ProgressPolicy.MaturityDays / 2.0;
        Assert.Equal(0.5, ProgressPolicy.Durability(ReviewCard(half)), precision: 6);
    }

    [Fact]
    public void Durability_Never_Exceeds_One_For_Long_Intervals()
    {
        Assert.Equal(1.0, ProgressPolicy.Durability(ReviewCard(365)));
    }

    // --- Retrievability ---

    [Fact]
    public void Retrievability_Is_Zero_For_Unstudied_Cards()
    {
        Assert.Equal(0.0, Policy.Retrievability(NewCard()));
        Assert.Equal(0.0, Policy.Retrievability(LearningCard()));
    }

    [Fact]
    public void Retrievability_Is_TargetValue_Exactly_On_Schedule()
    {
        // Due right now, reviewed IntervalDays ago -> elapsed == interval -> anchor point.
        var card = ReviewCard(10);
        Assert.Equal(ProgressPolicy.TargetRetrievability, Policy.Retrievability(card), precision: 6);
    }

    [Fact]
    public void Retrievability_Is_Full_Immediately_After_Review()
    {
        // Due 10 days from now (just reviewed) -> elapsed ~ 0.
        var card = new Card { State = CardState.Review, IntervalDays = 10, Due = Now.UtcDateTime.AddDays(10) };
        Assert.Equal(1.0, Policy.Retrievability(card), precision: 6);
    }

    [Fact]
    public void Retrievability_Decays_Past_The_Scheduled_Date()
    {
        // Due yesterday (overdue by one full interval) -> elapsed == 2x interval.
        var card = new Card { State = CardState.Review, IntervalDays = 5, Due = Now.UtcDateTime.AddDays(-5) };
        var r = Policy.Retrievability(card);
        Assert.True(r < ProgressPolicy.TargetRetrievability, "overdue card should read below the target");
        Assert.Equal(Math.Pow(ProgressPolicy.TargetRetrievability, 2), r, precision: 6);
    }

    [Fact]
    public void Retrievability_Longer_Interval_Same_Elapsed_Reads_Higher()
    {
        // Both overdue by the same 3 days, but the longer-interval card has forgotten less.
        var overdueShort = new Card { State = CardState.Review, IntervalDays = 3, Due = Now.UtcDateTime.AddDays(-3) };
        var overdueLong = new Card { State = CardState.Review, IntervalDays = 30, Due = Now.UtcDateTime.AddDays(-3) };
        Assert.True(Policy.Retrievability(overdueLong) > Policy.Retrievability(overdueShort));
    }

    // --- Buckets ---

    [Theory]
    [InlineData(CardBucket.Unseen)]
    public void New_State_Buckets_As_Unseen(CardBucket expected) =>
        Assert.Equal(expected, Policy.BucketOf(NewCard()));

    [Fact]
    public void Learning_State_Buckets_As_Learning() =>
        Assert.Equal(CardBucket.Learning, Policy.BucketOf(LearningCard()));

    [Fact]
    public void Interval_Just_Below_Maturity_Is_Young() =>
        Assert.Equal(CardBucket.Young, Policy.BucketOf(ReviewCard(ProgressPolicy.MaturityDays - 1)));

    [Fact]
    public void Interval_At_Maturity_Is_Mature() =>
        Assert.Equal(CardBucket.Mature, Policy.BucketOf(ReviewCard(ProgressPolicy.MaturityDays)));

    [Fact]
    public void Interval_Just_Above_Maturity_Is_Mature() =>
        Assert.Equal(CardBucket.Mature, Policy.BucketOf(ReviewCard(ProgressPolicy.MaturityDays + 1)));

    // --- Leeches & overdue ---

    [Fact]
    public void Leech_Threshold_Is_Eight_Lapses()
    {
        Assert.False(Policy.IsLeech(ReviewCard(10, lapses: ProgressPolicy.LeechLapses - 1)));
        Assert.True(Policy.IsLeech(ReviewCard(10, lapses: ProgressPolicy.LeechLapses)));
    }

    [Fact]
    public void Overdue_Checks_Due_Against_Now()
    {
        var overdue = new Card { State = CardState.Review, IntervalDays = 1, Due = Now.UtcDateTime.AddDays(-1) };
        var notYet = new Card { State = CardState.Review, IntervalDays = 1, Due = Now.UtcDateTime.AddDays(1) };
        Assert.True(Policy.IsOverdue(overdue));
        Assert.False(Policy.IsOverdue(notYet));
        Assert.False(Policy.IsOverdue(NewCard()));
    }

    // --- Aggregate ---

    [Fact]
    public void Aggregate_On_Empty_Set_Does_Not_Divide_By_Zero()
    {
        var report = Policy.Aggregate([]);
        Assert.Equal(ProgressReport.Empty, report);
    }

    [Fact]
    public void Aggregate_Counts_Every_Bucket_And_Averages_Correctly()
    {
        var cards = new[]
        {
            NewCard(),                                    // Unseen
            LearningCard(),                                // Learning
            ReviewCard(5),                                 // Young, durability 5/21
            ReviewCard(ProgressPolicy.MaturityDays),        // Mature, durability 1.0
        };

        var report = Policy.Aggregate(cards);

        Assert.Equal(4, report.Total);
        Assert.Equal(1, report.Unseen);
        Assert.Equal(1, report.Learning);
        Assert.Equal(1, report.Young);
        Assert.Equal(1, report.Mature);

        var expectedMastery = (0.0 + 0.0 + 5.0 / ProgressPolicy.MaturityDays + 1.0) / 4.0;
        Assert.Equal(expectedMastery, report.Mastery, precision: 6);
    }

    [Fact]
    public void Aggregate_Counts_Leeches_And_Overdue_Independently_Of_Bucket()
    {
        var leechAndOverdue = new Card
        {
            State = CardState.Review, IntervalDays = 3, Due = Now.UtcDateTime.AddDays(-1), Lapses = 8,
        };
        var report = Policy.Aggregate([leechAndOverdue]);

        Assert.Equal(1, report.Leeches);
        Assert.Equal(1, report.Overdue);
    }

    [Fact]
    public void Aggregate_Without_Logs_Leaves_Retention_Null()
    {
        var report = Policy.Aggregate([ReviewCard(10)]);
        Assert.Null(report.Retention);
    }

    [Fact]
    public void Aggregate_Retention_Is_Share_Of_Non_Again_Grades()
    {
        var logs = new List<ReviewLog>
        {
            new() { Grade = ReviewGrade.Good },
            new() { Grade = ReviewGrade.Good },
            new() { Grade = ReviewGrade.Again },
            new() { Grade = ReviewGrade.Easy },
        };
        var report = Policy.Aggregate([ReviewCard(10)], logs);
        Assert.Equal(0.75, report.Retention);
    }

    [Fact]
    public void RetentionWindowStart_Is_Thirty_Days_Before_Now_By_Default()
    {
        Assert.Equal(Now.UtcDateTime.AddDays(-30), Policy.RetentionWindowStartUtc());
    }

    // --- Forecast ---

    [Fact]
    public void Forecast_On_Empty_Set_Is_Zeroed()
    {
        var forecast = Policy.Forecast([], Now.UtcDateTime.AddDays(7));
        Assert.Equal(new ProgressForecast(0, 0, 0), forecast);
    }

    [Fact]
    public void Forecast_Card_Due_After_Target_Stays_Durable()
    {
        var target = Now.UtcDateTime.AddDays(7);
        var card = new Card { State = CardState.Review, IntervalDays = 30, Due = Now.UtcDateTime.AddDays(10) };

        var forecast = Policy.Forecast([card], target);

        Assert.Equal(1, forecast.AlreadyDurable);
        Assert.Equal(0, forecast.WillLapseBeforeTarget);
    }

    [Fact]
    public void Forecast_Card_Due_Before_Target_Will_Lapse()
    {
        var target = Now.UtcDateTime.AddDays(7);
        var card = new Card { State = CardState.Review, IntervalDays = 2, Due = Now.UtcDateTime.AddDays(2) };

        var forecast = Policy.Forecast([card], target);

        Assert.Equal(0, forecast.AlreadyDurable);
        Assert.Equal(1, forecast.WillLapseBeforeTarget);
    }

    [Fact]
    public void Forecast_New_Cards_Always_Count_As_Will_Lapse()
    {
        var forecast = Policy.Forecast([NewCard()], Now.UtcDateTime.AddDays(1));
        Assert.Equal(0, forecast.AlreadyDurable);
        Assert.Equal(1, forecast.WillLapseBeforeTarget);
    }

    // --- FSRS-safety: this test exists to fail loudly if EaseFactor ever creeps into the policy. ---

    [Fact]
    public void Durability_And_Retrievability_Are_Unaffected_By_EaseFactor()
    {
        var low = ReviewCard(10);
        low.EaseFactor = 1.3;
        var high = ReviewCard(10);
        high.EaseFactor = 4.0;

        Assert.Equal(ProgressPolicy.Durability(low), ProgressPolicy.Durability(high));
        Assert.Equal(Policy.Retrievability(low), Policy.Retrievability(high));
    }
}
