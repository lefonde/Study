using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Planning;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Links cards to the topics they teach, and reports how much of a course actually has cards.
///
/// The linking pass only ever sends cards that aren't linked yet, so running it again after
/// adding a handful of cards costs a handful of cards — not the whole deck. It is also the only
/// route that works for hand-written cards: AI-generated ones arrive already carrying the topic
/// they were written for.
/// </summary>
public class CoverageService(
    IDbContextFactory<StudyDbContext> factory,
    ClaudeService claude)
{
    /// <summary>Cards per request. Enough to be cheap in one pass, small enough to stay reliable.</summary>
    private const int MaxCardsPerRun = 200;

    public async Task RunAsync(GenerationJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var topics = await db.CourseTopics.AsNoTracking()
            .Where(t => t.CourseId == job.CourseId && !t.IsDismissed)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        if (topics.Count == 0)
            throw new InvalidOperationException(
                "This course has no topics yet — map the course first, then link its cards.");

        // Already-linked cards are skipped: re-sending them would pay again for an answer that
        // hasn't changed, and this job is meant to be run repeatedly as cards accumulate.
        var linkedCardIds = await db.CardTopics.AsNoTracking()
            .Where(ct2 => ct2.Topic!.CourseId == job.CourseId)
            .Select(ct2 => ct2.CardId)
            .Distinct()
            .ToListAsync(ct);

        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.Deck!.CourseId == job.CourseId && !linkedCardIds.Contains(c.Id))
            .OrderBy(c => c.CreatedAt)
            .Take(MaxCardsPerRun)
            .Select(c => new { c.Id, c.Front, c.Back })
            .ToListAsync(ct);

        if (cards.Count == 0)
        {
            job.Message = "Every card is already linked to its topics.";
            db.GenerationJobs.Attach(job);
            db.Entry(job).State = EntityState.Modified;
            await db.SaveChangesAsync(ct);
            return;
        }

        var topicRefs = topics.Select((t, i) => (Topic: t, Ref: $"T{i + 1}")).ToList();
        var cardRefs = cards.Select((c, i) => (Card: c, Ref: $"C{i + 1}")).ToList();

        var topicText = new StringBuilder();
        foreach (var (topic, reference) in topicRefs)
            topicText.AppendLine($"- [{reference}] {topic.Name}: {topic.Summary}");

        var cardText = new StringBuilder();
        foreach (var (card, reference) in cardRefs)
            cardText.AppendLine($"- [{reference}] {Flatten(card.Front)} => {Flatten(card.Back)}");

        var result = await claude.MapCoverageAsync(topicText.ToString(), cardText.ToString(), ct);

        var topicByRef = topicRefs.ToDictionary(x => x.Ref, x => x.Topic.Id, StringComparer.OrdinalIgnoreCase);
        var cardByRef = cardRefs.ToDictionary(x => x.Ref, x => x.Card.Id, StringComparer.OrdinalIgnoreCase);

        var links = new List<CardTopic>();
        foreach (var link in result.Value.Links ?? [])
        {
            if (link.CardRef is null || !cardByRef.TryGetValue(link.CardRef.Trim(), out var cardId))
                continue;

            foreach (var topicRef in link.TopicRefs ?? [])
            {
                if (topicRef is null || !topicByRef.TryGetValue(topicRef.Trim(), out var topicId))
                    continue;
                if (links.Any(l => l.CardId == cardId && l.CourseTopicId == topicId))
                    continue;
                links.Add(new CardTopic { CardId = cardId, CourseTopicId = topicId });
            }
        }

        db.CardTopics.AddRange(links);

        job.InputTokens = result.InputTokens;
        job.OutputTokens = result.OutputTokens;
        var unmatched = cards.Count - links.Select(l => l.CardId).Distinct().Count();
        job.Message = $"Linked {links.Count} card-topic pair(s) across {cards.Count} card(s)"
                      + (unmatched > 0 ? $"; {unmatched} matched no topic." : ".");

        db.GenerationJobs.Attach(job);
        db.Entry(job).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Collapses a card face to a single line. Cards carry markdown and LaTeX; the newlines add
    /// nothing to a matching decision and would just inflate the prompt.
    /// </summary>
    private static string Flatten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 300 ? single : single[..300] + "…";
    }

    /// <summary>Per-topic card counts plus the rolled-up report, for the Map tab.</summary>
    public async Task<(List<TopicCoverage> PerTopic, CoverageReport Report)> GetCoverageAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var topics = await db.CourseTopics.AsNoTracking()
            .Where(t => t.CourseId == courseId)
            .ToListAsync();

        var counts = await db.CardTopics.AsNoTracking()
            .Where(ct => ct.Topic!.CourseId == courseId)
            .GroupBy(ct => ct.CourseTopicId)
            .Select(g => new { TopicId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TopicId, x => x.Count);

        var perTopic = topics
            .Select(t => new TopicCoverage(t, counts.GetValueOrDefault(t.Id)))
            .ToList();

        return (perTopic, CoveragePolicy.Summarise(perTopic));
    }

    /// <summary>How many cards are still waiting to be linked — what "Link cards" would cover.</summary>
    public async Task<int> CountUnlinkedCardsAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var linked = db.CardTopics.Where(ct => ct.Topic!.CourseId == courseId).Select(ct => ct.CardId);
        return await db.Cards
            .Where(c => c.Deck!.CourseId == courseId && !linked.Contains(c.Id))
            .CountAsync();
    }
}
