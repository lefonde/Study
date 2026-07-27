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
}
