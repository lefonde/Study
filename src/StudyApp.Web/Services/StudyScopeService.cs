using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Planning;
using StudyApp.Core.Scheduling;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

/// <summary>
/// One thing you can choose to study. <paramref name="Name"/> may be markdown with LaTeX (topic
/// names are), so render it inline rather than as text. <paramref name="Depth"/> indents a lesson
/// under its chapter.
/// </summary>
public record ScopeRow(
    ReviewScope Scope,
    string Name,
    int Cards,
    int Due,
    int New,
    double Mastery,
    int Depth = 0);

/// <summary>
/// One hierarchy's worth of rows. <paramref name="Note"/> is where the group admits what it cannot
/// show — topics with no cards behind them, for instance — instead of quietly listing fewer rows.
/// </summary>
public record ScopeGroup(string Title, string Hint, List<ScopeRow> Rows, string? Note = null);

public record CourseScopes(
    Guid CourseId,
    string Name,
    string Color,
    int Cards,
    int Due,
    int UnlinkedCards,
    List<ScopeGroup> Groups);

/// <summary>
/// Everything the user could choose to study, laid out along the hierarchies the app already holds:
/// the learning path's stages, the chapter/lesson tree, the topic map, and the decks.
///
/// All of it comes from five queries for the whole page, however many courses there are, and every
/// count is then grouped in memory. That follows <c>ReviewService.GetHomeSummaryAsync</c>, which does
/// the same for the same reason: a query per row would mean sixty round trips to paint one page on a
/// forty-topic course.
///
/// Counts come from <see cref="DuePolicy"/> and <see cref="ProgressPolicy"/> rather than from
/// arithmetic of its own, so a row here can never disagree with the session it starts.
/// </summary>
public class StudyScopeService(
    IDbContextFactory<StudyDbContext> factory,
    DuePolicy duePolicy,
    ProgressPolicy progress)
{
    public async Task<List<CourseScopes>> GetAllAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        var courses = await db.Courses.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.Color })
            .OrderBy(c => c.Name)
            .ToListAsync();
        if (courses.Count == 0)
            return [];

        var decks = await db.Decks.AsNoTracking()
            .Select(d => new { d.Id, d.CourseId, d.Name, d.UnitId })
            .ToListAsync();
        var cards = await db.Cards.AsNoTracking().ToListAsync();
        var units = await db.CourseUnits.AsNoTracking().ToListAsync();
        var topics = await db.CourseTopics.AsNoTracking()
            .Include(t => t.Prerequisites)
            .Where(t => !t.IsDismissed)
            .OrderByDescending(t => t.Importance)
            .ThenBy(t => t.Name)
            .ToListAsync();
        var links = await db.CardTopics.AsNoTracking()
            .Select(ct => new { ct.CardId, ct.CourseTopicId })
            .ToListAsync();

        var deckById = decks.ToDictionary(d => d.Id);
        // Cards are fetched without their Deck navigation, so the deck's unit has to come from here.
        var deckUnitById = decks.ToDictionary(d => d.Id, d => d.UnitId);
        var cardsByTopic = links
            .GroupBy(l => l.CourseTopicId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.CardId).ToHashSet());
        var linkedCardIds = links.Select(l => l.CardId).ToHashSet();

        var result = new List<CourseScopes>();

        foreach (var course in courses)
        {
            var courseCards = cards
                .Where(c => deckById.TryGetValue(c.DeckId, out var d) && d.CourseId == course.Id)
                .ToList();
            if (courseCards.Count == 0)
                continue;

            var courseUnits = units.Where(u => u.CourseId == course.Id).ToList();
            var courseTopics = topics.Where(t => t.CourseId == course.Id).ToList();

            result.Add(new CourseScopes(
                course.Id,
                course.Name,
                course.Color,
                courseCards.Count,
                courseCards.Count(duePolicy.IsDueToday),
                courseCards.Count(c => !linkedCardIds.Contains(c.Id)),
                [
                    StageGroup(courseTopics, courseCards, cardsByTopic, course.Id),
                    ChapterGroup(courseUnits, courseCards, deckUnitById),
                    TopicGroup(courseTopics, courseCards, cardsByTopic),
                    DeckGroup(decks.Where(d => d.CourseId == course.Id).Select(d => (d.Id, d.Name)), courseCards),
                ]));
        }

        return result;
    }

    /// <summary>The learning path, one row per rung. Empty when the course has no map yet.</summary>
    private ScopeGroup StageGroup(
        List<CourseTopic> topics,
        List<Card> courseCards,
        Dictionary<Guid, HashSet<Guid>> cardsByTopic,
        Guid courseId)
    {
        // Built from the same input the Map tab's Path view uses, so the stage numbers match what is
        // drawn there — and so clicking "Stage 2" here studies what "Stage 2" shows there.
        var map = PrerequisiteGraph.Build(topics, PrerequisiteGraph.EdgesOf(topics));

        var rows = new List<ScopeRow>();
        foreach (var stage in map.Stages)
        {
            var ids = stage.Topics
                .SelectMany(t => cardsByTopic.GetValueOrDefault(t.Id, []))
                .ToHashSet();
            var stageCards = courseCards.Where(c => ids.Contains(c.Id)).ToList();
            if (stageCards.Count == 0)
                continue;

            rows.Add(Row(
                ReviewScope.ForStage(courseId, stage.Index),
                $"Stage {stage.Index + 1} · {stage.Topics.Count} topic(s)",
                stageCards));
        }

        return new ScopeGroup(
            "Stages",
            "Work through the learning path in order — nothing here depends on a later stage.",
            rows,
            topics.Count == 0
                ? "This course hasn't been mapped yet. Map it, then work out the learning path."
                : !map.HasAnyEdges
                    ? "No prerequisites are recorded yet, so every topic sits at stage 1. "
                      + "Run 🧭 Work out the learning path on the course."
                    : null);
    }

    /// <summary>
    /// The syllabus tree, lessons indented under their chapter. A chapter's row counts its lessons'
    /// cards too — see <see cref="UnitTree.WithDescendants"/> — so the same card is deliberately
    /// counted on both the chapter row and its lesson's.
    /// </summary>
    private ScopeGroup ChapterGroup(
        List<CourseUnit> courseUnits,
        List<Card> courseCards,
        Dictionary<Guid, Guid?> deckUnitById)
    {
        var rows = new List<ScopeRow>();
        var unitOf = courseCards.ToDictionary(c => c.Id, c => EffectiveUnit(c, deckUnitById));

        void Walk(Guid? parentId, int depth)
        {
            foreach (var unit in courseUnits.Where(u => u.ParentId == parentId).OrderBy(u => u.Order))
            {
                var ids = UnitTree.WithDescendants(courseUnits, unit.Id);
                var unitCards = courseCards
                    .Where(c => unitOf[c.Id] is { } u && ids.Contains(u))
                    .ToList();
                if (unitCards.Count > 0)
                    rows.Add(Row(ReviewScope.ForUnit(unit.Id), unit.Title, unitCards, depth));
                Walk(unit.Id, depth + 1);
            }
        }

        Walk(null, 0);

        var untagged = courseCards.Count(c => unitOf[c.Id] is null);
        return new ScopeGroup(
            "Chapters & lessons",
            "The course's own structure. A chapter includes everything under it.",
            rows,
            untagged > 0
                ? $"{untagged} card(s) aren't assigned to a chapter, so they appear under no row here."
                : null);
    }

    private ScopeGroup TopicGroup(
        List<CourseTopic> topics,
        List<Card> courseCards,
        Dictionary<Guid, HashSet<Guid>> cardsByTopic)
    {
        var rows = new List<ScopeRow>();
        var empty = 0;

        foreach (var topic in topics)
        {
            var ids = cardsByTopic.GetValueOrDefault(topic.Id, []);
            var topicCards = courseCards.Where(c => ids.Contains(c.Id)).ToList();
            if (topicCards.Count == 0)
            {
                empty++;
                continue;
            }
            rows.Add(Row(ReviewScope.ForTopic(topic.Id), topic.Name, topicCards));
        }

        return new ScopeGroup(
            "Topics",
            "The finest grain — one concept at a time.",
            rows,
            empty > 0
                ? $"{empty} mapped topic(s) have no cards yet, so they can't be studied. "
                  + "The Map tab's coverage view is where to close that."
                : null);
    }

    private ScopeGroup DeckGroup(
        IEnumerable<(Guid Id, string Name)> decks, List<Card> courseCards)
    {
        var rows = decks
            .Select(d => (d, Cards: courseCards.Where(c => c.DeckId == d.Id).ToList()))
            .Where(x => x.Cards.Count > 0)
            .OrderBy(x => x.d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => Row(ReviewScope.ForDeck(x.d.Id), x.d.Name, x.Cards))
            .ToList();

        return new ScopeGroup("Decks", "How the cards are filed.", rows);
    }

    /// <summary>
    /// The unit a card counts toward: its own tag if it has one, otherwise its deck's. The same rule
    /// <c>ProgressService.GetUnitProgressAsync</c> applies, because a chapter must contain the same
    /// cards whether you are reading its progress or studying it.
    /// </summary>
    private static Guid? EffectiveUnit(Card card, Dictionary<Guid, Guid?> deckUnitById) =>
        card.UnitId ?? deckUnitById.GetValueOrDefault(card.DeckId);

    private ScopeRow Row(ReviewScope scope, string name, List<Card> cards, int depth = 0) =>
        new(scope,
            name,
            cards.Count,
            cards.Count(duePolicy.IsDueToday),
            cards.Count(c => c.State == CardState.New),
            progress.Aggregate(cards).Mastery,
            depth);
}
