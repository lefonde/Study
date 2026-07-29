using StudyApp.Core.Entities;
using StudyApp.Core.Scheduling;

namespace StudyApp.Core.Planning;

/// <summary>What to do about one topic before a deadline.</summary>
public enum StudyAction
{
    /// <summary>Nothing to study — the topic has no cards at all.</summary>
    WriteCards = 0,
    /// <summary>Has cards, but they won't hold until the target date without work.</summary>
    Review = 1,
    /// <summary>Covered, and already scheduled to still be durable on the day.</summary>
    Ready = 2,
}

/// <summary>A topic's place in the plan, with the numbers behind it.</summary>
public record TopicPlanItem(
    CourseTopic Topic,
    StudyAction Action,
    int CardCount,
    int CardsNeedingWork,
    double Mastery,
    double RecallNow,
    /// <summary>The assignment doesn't assess this — something it assesses depends on it.</summary>
    bool IsFoundation = false,
    /// <summary>Assessed topics this one is underneath, for "blocks X".</summary>
    IReadOnlyList<string>? RequiredBy = null)
{
    /// <summary>A foundation with nothing behind it stops the topics above it being learnable.</summary>
    public bool IsBlocking => IsFoundation && Action == StudyAction.WriteCards;
}

/// <summary>What stands between you and an assignment.</summary>
public record StudyPlan(
    DateTime TargetDateUtc,
    int DaysRemaining,
    IReadOnlyList<TopicPlanItem> Items)
{
    public static StudyPlan Empty { get; } = new(default, 0, []);

    public int TopicsNeedingCards => Items.Count(i => i.Action == StudyAction.WriteCards);
    public int TopicsNeedingReview => Items.Count(i => i.Action == StudyAction.Review);
    public int TopicsReady => Items.Count(i => i.Action == StudyAction.Ready);
    public int CardsNeedingWork => Items.Sum(i => i.CardsNeedingWork);

    /// <summary>Topics the assignment assesses that you have nothing at all for.</summary>
    public IEnumerable<TopicPlanItem> Gaps => Items.Where(i => i.Action == StudyAction.WriteCards);

    /// <summary>What the assignment's own material tests.</summary>
    public IEnumerable<TopicPlanItem> Assessed => Items.Where(i => !i.IsFoundation);

    /// <summary>What those rest on, pulled in through the prerequisite graph.</summary>
    public IEnumerable<TopicPlanItem> Foundations => Items.Where(i => i.IsFoundation);
}

/// <summary>
/// The topic and the cards that teach it — the input the plan is built from.
///
/// <paramref name="IsFoundation"/> and <paramref name="RequiredBy"/> are defaulted so the common
/// case reads unchanged; they are set only when a topic was pulled in as a prerequisite of
/// something the assignment actually assesses.
/// </summary>
public record TopicCards(
    CourseTopic Topic,
    IReadOnlyList<Card> Cards,
    bool IsFoundation = false,
    IReadOnlyList<string>? RequiredBy = null);

/// <summary>
/// Turns "Maman 12 is in nine days" into a list of what to actually do.
///
/// Deliberately <b>not</b> an AI call. Once the map records which topics an assignment assesses
/// and cards are linked to topics, this is a pure function of data the app already holds — so it
/// costs nothing, is never stale, and can be unit-tested against hand-computed expectations. An
/// LLM here would add latency, expense and uncertainty to a question arithmetic already answers.
///
/// Ordering puts importance first and severity second: every Core topic outranks every
/// Supporting one, and within a tier the topics you have nothing for come before the ones that
/// merely need review. A peripheral topic with no cards is genuinely less urgent than a core
/// topic that's slipping, and sorting by severity alone would claim otherwise.
/// </summary>
public class StudyPlanPolicy(ProgressPolicy progress, TimeProvider timeProvider)
{
    public StudyPlan Build(IEnumerable<TopicCards> topics, DateTime targetDateUtc)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var items = topics
            .Where(t => !t.Topic.IsDismissed)
            .Select(t => Assess(t, targetDateUtc))
            // Assessed topics before foundations, so Items reads top to bottom in the order the
            // plan is displayed. Within each group the existing importance → severity → mastery
            // ordering is unchanged.
            .OrderBy(i => i.IsFoundation)
            .ThenByDescending(i => i.Topic.Importance)
            .ThenBy(i => i.Action)
            .ThenBy(i => i.Mastery)
            .ThenBy(i => i.Topic.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Floored at zero: an overdue assignment has no negative days left, and "in -3 days"
        // reads as a bug rather than as information.
        var daysRemaining = Math.Max(0, (int)Math.Ceiling((targetDateUtc - now).TotalDays));

        return new StudyPlan(targetDateUtc, daysRemaining, items);
    }

    private TopicPlanItem Assess(TopicCards topic, DateTime targetDateUtc)
    {
        var cards = topic.Cards;
        if (cards.Count == 0)
        {
            return new TopicPlanItem(
                topic.Topic, StudyAction.WriteCards, 0, 0, 0, 0,
                topic.IsFoundation, topic.RequiredBy);
        }

        var report = progress.Aggregate(cards);
        var needingWork = cards.Count(c => NeedsWorkBefore(c, targetDateUtc));

        return new TopicPlanItem(
            topic.Topic,
            needingWork == 0 ? StudyAction.Ready : StudyAction.Review,
            cards.Count,
            needingWork,
            report.Mastery,
            report.RecallNow,
            topic.IsFoundation,
            topic.RequiredBy);
    }

    /// <summary>
    /// A card needs work before the target if it hasn't graduated yet, or if its current schedule
    /// brings it due on or before the day. A card due after the target is one the scheduler has
    /// already placed beyond the deadline — it will still be fresh then without being touched.
    ///
    /// Public because it is also the right filter for an assignment-scoped review session:
    /// studying for Thursday means studying what won't hold until Thursday, which is a strictly
    /// larger set than what happens to be due today.
    /// </summary>
    public static bool NeedsWorkBefore(Card card, DateTime targetDateUtc) =>
        card.State is CardState.New or CardState.Learning
        || card.Due is not { } due
        || due <= targetDateUtc;
}
