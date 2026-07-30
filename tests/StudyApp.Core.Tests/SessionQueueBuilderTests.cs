using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;

namespace StudyApp.Core.Tests;

public class SessionQueueBuilderTests
{
    private static readonly DateTime Cutoff = new(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);

    private static Card New() => new() { State = CardState.New };
    private static Card DueCard(int daysBeforeCutoff = 1) =>
        new() { State = CardState.Review, Due = Cutoff.AddDays(-daysBeforeCutoff) };
    private static Card NotYetDue() =>
        new() { State = CardState.Review, Due = Cutoff.AddDays(3) };

    [Fact]
    public void New_Cards_Are_Capped_Per_Session()
    {
        var cards = Enumerable.Range(0, 30).Select(_ => New()).ToList();
        var queue = SessionQueueBuilder.Build(cards, Cutoff);

        Assert.Equal(SessionQueueBuilder.MaxNewPerSession, queue.Count);
    }

    [Fact]
    public void Due_Reviews_Come_Before_New_Cards()
    {
        var cards = new List<Card> { New(), DueCard(), New(), DueCard() };
        var queue = SessionQueueBuilder.Build(cards, Cutoff, rng: new Random(42));

        Assert.Equal(4, queue.Count);
        Assert.All(queue.Take(2), c => Assert.Equal(CardState.Review, c.State));
        Assert.All(queue.Skip(2), c => Assert.Equal(CardState.New, c.State));
    }

    [Fact]
    public void Cards_Not_Yet_Due_Are_Excluded()
    {
        var queue = SessionQueueBuilder.Build([DueCard(), NotYetDue()], Cutoff);
        Assert.Single(queue);
    }

    [Fact]
    public void Deleted_Cards_Are_Excluded()
    {
        var deleted = DueCard();
        deleted.IsDeleted = true;
        var queue = SessionQueueBuilder.Build([deleted, DueCard()], Cutoff);
        Assert.Single(queue);
    }

    [Fact]
    public void Due_Reviews_Are_Not_Capped()
    {
        var cards = Enumerable.Range(0, 50).Select(_ => DueCard()).ToList();
        var queue = SessionQueueBuilder.Build(cards, Cutoff);
        Assert.Equal(50, queue.Count);
    }

    // --- practice: drilling a narrow scope past what's due ---

    private static ProgressPolicy Progress() =>
        new(new FakeTimeProvider(new DateTimeOffset(Cutoff, TimeSpan.Zero)));

    /// <summary>A card whose interval is nearly elapsed reads weaker than one just reviewed.</summary>
    private static Card EarlyCard(double intervalDays, double elapsedFraction) => new()
    {
        State = CardState.Review,
        IntervalDays = intervalDays,
        Due = Cutoff.AddDays(intervalDays * (1 - elapsedFraction)),
    };

    [Fact]
    public void Practice_Keeps_The_Cards_A_Normal_Session_Drops()
    {
        var queue = SessionQueueBuilder.BuildPractice([DueCard(), NotYetDue()], Cutoff, Progress());
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Practice_Puts_Due_Cards_Before_Early_Ones()
    {
        var due = DueCard();
        var queue = SessionQueueBuilder.BuildPractice(
            [NotYetDue(), due, NotYetDue()], Cutoff, Progress(), rng: new Random(42));

        Assert.Same(due, queue[0]);
    }

    /// <summary>
    /// The order that makes a drill worth doing: the memory most likely to have decayed comes first.
    /// </summary>
    [Fact]
    public void Practice_Orders_Early_Cards_Weakest_First()
    {
        var fresh = EarlyCard(intervalDays: 20, elapsedFraction: 0.05);
        var faded = EarlyCard(intervalDays: 20, elapsedFraction: 0.9);

        var queue = SessionQueueBuilder.BuildPractice([fresh, faded], Cutoff, Progress());

        Assert.Same(faded, queue[0]);
        Assert.Same(fresh, queue[1]);
    }

    [Fact]
    public void Practice_Still_Caps_New_Cards()
    {
        var cards = Enumerable.Range(0, 30).Select(_ => New()).ToList();
        var queue = SessionQueueBuilder.BuildPractice(cards, Cutoff, Progress());

        Assert.Equal(SessionQueueBuilder.MaxNewPerSession, queue.Count);
    }

    [Fact]
    public void Practice_Excludes_Deleted_Cards()
    {
        var deleted = NotYetDue();
        deleted.IsDeleted = true;

        var queue = SessionQueueBuilder.BuildPractice([deleted, NotYetDue()], Cutoff, Progress());
        Assert.Single(queue);
    }

    /// <summary>
    /// A practice grade never moves the card into Learning, so the state-based signal a normal
    /// session relies on is absent — which would silently drop the card you just got wrong.
    /// </summary>
    [Fact]
    public void A_Practice_Again_Requeues_Even_Though_The_State_Never_Changed()
    {
        var card = NotYetDue();

        Assert.True(SessionQueueBuilder.Requeues(card, ReviewGrade.Again, practice: true));
        Assert.False(SessionQueueBuilder.Requeues(card, ReviewGrade.Good, practice: true));
    }

    [Fact]
    public void A_Normal_Grade_Requeues_On_State_Not_Grade()
    {
        var learning = new Card { State = CardState.Learning };
        var graduated = new Card { State = CardState.Review };

        Assert.True(SessionQueueBuilder.Requeues(learning, ReviewGrade.Again, practice: false));
        Assert.False(SessionQueueBuilder.Requeues(graduated, ReviewGrade.Again, practice: false));
    }
}
