using StudyApp.Core.Entities;
using StudyApp.Core.Planning;
using StudyApp.Core.Scheduling;

namespace StudyApp.Core.Tests;

public class StudyPlanPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime InTenDays = Now.UtcDateTime.AddDays(10);

    private static StudyPlanPolicy Policy
    {
        get
        {
            var clock = new FakeTimeProvider(Now);
            return new StudyPlanPolicy(new ProgressPolicy(clock), clock);
        }
    }

    private static CourseTopic Topic(
        string name, TopicImportance importance = TopicImportance.Supporting, bool dismissed = false) =>
        new() { Name = name, Importance = importance, IsDismissed = dismissed };

    /// <summary>A graduated card whose next review falls <paramref name="dueInDays"/> from now.</summary>
    private static Card Card(double dueInDays, double intervalDays = 30) => new()
    {
        State = CardState.Review,
        IntervalDays = intervalDays,
        Due = Now.UtcDateTime.AddDays(dueInDays),
    };

    private static Card NewCard() => new() { State = CardState.New };

    private static TopicCards With(CourseTopic topic, params Card[] cards) => new(topic, cards);

    // --- classification ---

    [Fact]
    public void A_Topic_With_No_Cards_Needs_Cards_Written()
    {
        var plan = Policy.Build([With(Topic("Deadlock"))], InTenDays);

        Assert.Equal(StudyAction.WriteCards, plan.Items.Single().Action);
        Assert.Equal(1, plan.TopicsNeedingCards);
    }

    [Fact]
    public void Cards_Scheduled_Past_The_Target_Are_Already_Ready()
    {
        // Due in 30 days, deadline in 10: the scheduler has already placed these beyond it.
        var plan = Policy.Build([With(Topic("Deadlock"), Card(30), Card(40))], InTenDays);

        Assert.Equal(StudyAction.Ready, plan.Items.Single().Action);
        Assert.Equal(0, plan.CardsNeedingWork);
    }

    [Fact]
    public void A_Card_Due_Before_The_Target_Means_Review()
    {
        var plan = Policy.Build([With(Topic("Deadlock"), Card(30), Card(3))], InTenDays);

        var item = plan.Items.Single();
        Assert.Equal(StudyAction.Review, item.Action);
        Assert.Equal(2, item.CardCount);
        Assert.Equal(1, item.CardsNeedingWork);
    }

    [Fact]
    public void A_Card_Due_Exactly_On_The_Target_Still_Needs_Work()
    {
        // Boundary: due on the day itself is not "still fresh on the day".
        var plan = Policy.Build([With(Topic("Deadlock"), Card(10))], InTenDays);
        Assert.Equal(StudyAction.Review, plan.Items.Single().Action);
    }

    [Fact]
    public void Unseen_Cards_Count_As_Work_However_Many_There_Are()
    {
        // A topic with cards you've never opened is not covered in any useful sense.
        var plan = Policy.Build([With(Topic("Deadlock"), NewCard(), NewCard())], InTenDays);

        var item = plan.Items.Single();
        Assert.Equal(StudyAction.Review, item.Action);
        Assert.Equal(2, item.CardsNeedingWork);
        Assert.Equal(0, item.Mastery);
    }

    // --- ordering: the thing the plan is actually for ---

    [Fact]
    public void Importance_Outranks_Severity()
    {
        var plan = Policy.Build(
        [
            With(Topic("Trivia", TopicImportance.Peripheral)),                 // no cards at all
            With(Topic("Deadlock", TopicImportance.Core), Card(3)),            // merely needs review
        ], InTenDays);

        // A core topic that's slipping matters more than a peripheral one you've never touched.
        Assert.Equal(["Deadlock", "Trivia"], plan.Items.Select(i => i.Topic.Name));
    }

    [Fact]
    public void Within_A_Tier_Missing_Cards_Come_Before_Review()
    {
        var plan = Policy.Build(
        [
            With(Topic("Has cards", TopicImportance.Core), Card(3)),
            With(Topic("Has none", TopicImportance.Core)),
            With(Topic("All set", TopicImportance.Core), Card(40)),
        ], InTenDays);

        Assert.Equal(["Has none", "Has cards", "All set"], plan.Items.Select(i => i.Topic.Name));
    }

    [Fact]
    public void Equally_Placed_Topics_Are_Ordered_Weakest_First()
    {
        var plan = Policy.Build(
        [
            // Same action and tier; the shorter interval is the weaker memory.
            With(Topic("Stronger", TopicImportance.Core), Card(3, intervalDays: 20)),
            With(Topic("Weaker", TopicImportance.Core), Card(3, intervalDays: 2)),
        ], InTenDays);

        Assert.Equal(["Weaker", "Stronger"], plan.Items.Select(i => i.Topic.Name));
    }

    // --- edges ---

    [Fact]
    public void An_Assignment_Assessing_Nothing_Yields_An_Empty_Plan()
    {
        var plan = Policy.Build([], InTenDays);

        Assert.Empty(plan.Items);
        Assert.Equal(0, plan.TopicsNeedingCards);
    }

    [Fact]
    public void Dismissed_Topics_Are_Left_Out_Of_The_Plan()
    {
        var plan = Policy.Build(
        [
            With(Topic("Relevant", TopicImportance.Core)),
            With(Topic("Not for me", TopicImportance.Core, dismissed: true)),
        ], InTenDays);

        Assert.Equal(["Relevant"], plan.Items.Select(i => i.Topic.Name));
    }

    [Fact]
    public void Days_Remaining_Never_Goes_Negative()
    {
        // An overdue assignment has no days left; "-3 days" would read as a bug.
        var plan = Policy.Build([With(Topic("Deadlock"))], Now.UtcDateTime.AddDays(-3));
        Assert.Equal(0, plan.DaysRemaining);
    }

    [Fact]
    public void Days_Remaining_Is_Counted_From_Now()
    {
        Assert.Equal(10, Policy.Build([], InTenDays).DaysRemaining);
    }

    [Fact]
    public void Summary_Counts_Cover_Every_Topic()
    {
        var plan = Policy.Build(
        [
            With(Topic("None", TopicImportance.Core)),
            With(Topic("Weak", TopicImportance.Core), Card(2)),
            With(Topic("Fine", TopicImportance.Core), Card(40)),
        ], InTenDays);

        Assert.Equal(1, plan.TopicsNeedingCards);
        Assert.Equal(1, plan.TopicsNeedingReview);
        Assert.Equal(1, plan.TopicsReady);
        Assert.Equal(3, plan.Items.Count);
        Assert.Equal(["None"], plan.Gaps.Select(g => g.Topic.Name));
    }

    // --- foundations: topics pulled in through the prerequisite graph ---

    private static TopicCards Foundation(CourseTopic topic, string[] requiredBy, params Card[] cards) =>
        new(topic, cards, IsFoundation: true, RequiredBy: requiredBy);

    [Fact]
    public void Foundations_Are_Separated_From_What_Is_Actually_Assessed()
    {
        var plan = Policy.Build(
        [
            With(Topic("Banker's algorithm", TopicImportance.Core), Card(2)),
            Foundation(Topic("Safe states"), ["Banker's algorithm"], Card(2)),
            Foundation(Topic("Deadlock"), ["Banker's algorithm"]),
        ], InTenDays);

        Assert.Equal(["Banker's algorithm"], plan.Assessed.Select(i => i.Topic.Name));
        Assert.Equal(["Deadlock", "Safe states"], plan.Foundations.Select(i => i.Topic.Name).Order());
    }

    /// <summary>
    /// Items reads top to bottom in the order the plan is displayed, so a caller that ignores the
    /// grouping still gets a sensible list. Assessed first, then the existing importance ordering
    /// inside each group.
    /// </summary>
    [Fact]
    public void Assessed_Topics_Come_Before_Foundations_However_Important()
    {
        var plan = Policy.Build(
        [
            Foundation(Topic("Core foundation", TopicImportance.Core), ["Peripheral assessed"]),
            With(Topic("Peripheral assessed", TopicImportance.Peripheral)),
        ], InTenDays);

        Assert.Equal(["Peripheral assessed", "Core foundation"], plan.Items.Select(i => i.Topic.Name));
    }

    /// <summary>
    /// The case foundations exist for: an assignment looks nearly ready, while something it rests
    /// on has nothing behind it at all.
    /// </summary>
    [Fact]
    public void A_Foundation_With_No_Cards_Is_Reported_As_Blocking()
    {
        var plan = Policy.Build(
        [
            With(Topic("Banker's algorithm", TopicImportance.Core), Card(40)),
            Foundation(Topic("Safe states"), ["Banker's algorithm"]),
        ], InTenDays);

        var blocking = Assert.Single(plan.Items.Where(i => i.IsBlocking));
        Assert.Equal("Safe states", blocking.Topic.Name);
        Assert.Equal(["Banker's algorithm"], blocking.RequiredBy!);

        // The assessed topic on its own would have read as entirely ready.
        Assert.Equal(StudyAction.Ready, plan.Assessed.Single().Action);
    }

    [Fact]
    public void A_Covered_Foundation_Does_Not_Block()
    {
        var plan = Policy.Build(
        [
            With(Topic("Banker's algorithm", TopicImportance.Core), Card(40)),
            Foundation(Topic("Safe states"), ["Banker's algorithm"], Card(40)),
        ], InTenDays);

        Assert.DoesNotContain(plan.Items, i => i.IsBlocking);
    }

    /// <summary>
    /// Counts answer "what stands between me and this assignment", so a foundation that needs
    /// cards written counts just as much as an assessed topic that does.
    /// </summary>
    [Fact]
    public void Summary_Counts_Include_Foundations()
    {
        var plan = Policy.Build(
        [
            With(Topic("Assessed", TopicImportance.Core), Card(40)),
            Foundation(Topic("Underneath"), ["Assessed"]),
        ], InTenDays);

        Assert.Equal(1, plan.TopicsNeedingCards);
        Assert.Equal(1, plan.TopicsReady);
    }
}
