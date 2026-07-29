using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Stage 2: writes card suggestions from an already-ingested material. Reads the stored
/// extract, never the original file — so generating again over the same chapter costs a
/// fraction of the first ingestion and takes seconds rather than minutes.
/// </summary>
public class CardGenerationService(
    IDbContextFactory<StudyDbContext> factory,
    ClaudeService claude)
{
    public const int DefaultTargetCount = 15;

    public async Task RunAsync(GenerationJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Targeted runs carry topics instead of a material, and draw their source text from
        // whatever those topics cite — see GenerateForTopicsAsync.
        if (job.TargetTopicIds.Count > 0)
        {
            await GenerateForTopicsAsync(db, job, ct);
            return;
        }

        var material = await db.Materials
            .Include(m => m.Extract)
            .FirstOrDefaultAsync(m => m.Id == job.MaterialId, ct)
            ?? throw new InvalidOperationException("The material was deleted before generation ran.");

        if (material.Extract is not { Sections.Count: > 0 } extract)
            throw new InvalidOperationException(
                "This material hasn't been ingested yet — run ingestion first.");

        // The course's shared vocabulary, pooled from every extract in it, so cards for one
        // chapter still use terminology established elsewhere in the course.
        //
        // Flattened in memory rather than in SQL: Terms is a JSON column, and unnesting one
        // inside a query needs a lateral join (CROSS APPLY), which SQLite does not support.
        // A course's term lists are small, so fetching them whole costs nothing.
        //
        // AsNoTracking is required, not optional: Terms is an owned collection, and EF refuses
        // to track owned entities projected without their owner.
        var termLists = await db.MaterialExtracts
            .AsNoTracking()
            .Where(e => e.Material!.CourseId == material.CourseId)
            .Select(e => e.Terms)
            .ToListAsync(ct);

        var glossary = termLists
            .SelectMany(terms => terms)
            .Select(t => $"{t.Term} — {t.Definition}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        // Existing fronts across the course, so the model writes gaps rather than repeats.
        var existingFronts = await db.Cards
            .Where(c => c.Deck!.CourseId == material.CourseId)
            .Select(c => c.Front)
            .ToListAsync(ct);

        // Pending suggestions count as "already proposed" too — otherwise two runs over the
        // same chapter fill the inbox with duplicates of each other.
        var pendingFronts = await db.CardSuggestions
            .Where(s => s.CourseId == material.CourseId && s.Status == SuggestionStatus.Pending)
            .Select(s => s.Front)
            .ToListAsync(ct);

        var result = await claude.GenerateCardsAsync(
            extract.ToMarkdown(),
            glossary,
            [.. existingFronts, .. pendingFronts],
            DefaultTargetCount,
            ct: ct);

        var batchId = Guid.NewGuid();
        foreach (var card in result.Value.Cards)
        {
            if (string.IsNullOrWhiteSpace(card.Front) || string.IsNullOrWhiteSpace(card.Back))
                continue;

            db.CardSuggestions.Add(new CardSuggestion
            {
                CourseId = material.CourseId,
                BatchId = batchId,
                Front = ClaudeService.NormalizeMarkdown(card.Front).Trim(),
                Back = ClaudeService.NormalizeMarkdown(card.Back).Trim(),
                UnitId = material.UnitId,
                SourceMaterialId = material.Id,
                SourceReference = card.SourceReference,
                Rationale = card.Rationale,
            });
        }

        job.BatchId = batchId;
        job.InputTokens = result.InputTokens;
        job.OutputTokens = result.OutputTokens;
        job.Message = $"{result.Value.Cards.Count} card(s) ready for review.";

        db.GenerationJobs.Attach(job);
        db.Entry(job).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes cards for specific topics that currently have none — the closing half of the loop
    /// that starts with the map showing a gap.
    ///
    /// Source text comes from whatever material the topics cite (their <see cref="TopicSource"/>
    /// rows), so a run aimed at three topics reads only the extracts that actually discuss them
    /// instead of the whole course.
    /// </summary>
    private async Task GenerateForTopicsAsync(StudyDbContext db, GenerationJob job, CancellationToken ct)
    {
        var topics = await db.CourseTopics.AsNoTracking()
            .Include(t => t.Sources)
            .Where(t => job.TargetTopicIds.Contains(t.Id) && t.CourseId == job.CourseId)
            .OrderByDescending(t => t.Importance)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        if (topics.Count == 0)
            throw new InvalidOperationException("Those topics no longer exist.");

        var materialIds = topics.SelectMany(t => t.Sources).Select(s => s.MaterialId).Distinct().ToList();
        var extracts = await db.MaterialExtracts.AsNoTracking()
            .Include(e => e.Material)
            .Where(e => materialIds.Contains(e.MaterialId))
            .ToListAsync(ct);

        if (extracts.Count == 0)
            throw new InvalidOperationException(
                "The material these topics came from is no longer available — re-ingest it first.");

        var topicRefs = topics.Select((t, i) => (Topic: t, Ref: $"T{i + 1}")).ToList();
        var topicText = string.Join("\n", topicRefs.Select(x =>
            $"- [{x.Ref}] {x.Topic.Name} ({x.Topic.Importance.ToString().ToLowerInvariant()}): {x.Topic.Summary}"));

        var sourceText = string.Join("\n\n", extracts.Select(e =>
            $"### {e.Material?.Title}\n{e.ToMarkdown()}"));

        var glossary = extracts
            .SelectMany(e => e.Terms)
            .Select(t => $"{t.Term} — {t.Definition}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        var existingFronts = await db.Cards
            .Where(c => c.Deck!.CourseId == job.CourseId)
            .Select(c => c.Front)
            .ToListAsync(ct);
        var pendingFronts = await db.CardSuggestions
            .Where(s => s.CourseId == job.CourseId && s.Status == SuggestionStatus.Pending)
            .Select(s => s.Front)
            .ToListAsync(ct);

        // Scaled to the gap: a handful of cards each, rather than a fixed batch that would
        // over-serve one topic and starve the rest.
        var targetCount = Math.Clamp(topics.Count * 4, 5, 40);

        var result = await claude.GenerateCardsAsync(
            sourceText, glossary, [.. existingFronts, .. pendingFronts], targetCount, topicText, ct: ct);

        var topicByRef = topicRefs.ToDictionary(x => x.Ref, x => x.Topic.Id, StringComparer.OrdinalIgnoreCase);
        var batchId = Guid.NewGuid();
        var written = 0;

        foreach (var card in result.Value.Cards)
        {
            if (string.IsNullOrWhiteSpace(card.Front) || string.IsNullOrWhiteSpace(card.Back))
                continue;

            Guid? topicId = null;
            if (!string.IsNullOrWhiteSpace(card.TopicRef)
                && topicByRef.TryGetValue(card.TopicRef.Trim(), out var resolved))
            {
                topicId = resolved;
            }

            db.CardSuggestions.Add(new CardSuggestion
            {
                CourseId = job.CourseId,
                BatchId = batchId,
                Front = ClaudeService.NormalizeMarkdown(card.Front).Trim(),
                Back = ClaudeService.NormalizeMarkdown(card.Back).Trim(),
                CourseTopicId = topicId,
                UnitId = topics.FirstOrDefault(t => t.Id == topicId)?.UnitId,
                SourceReference = card.SourceReference,
                Rationale = card.Rationale,
            });
            written++;
        }

        var unserved = topics.Count - result.Value.Cards
            .Select(c => c.TopicRef?.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r) && topicByRef.ContainsKey(r!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        job.BatchId = batchId;
        job.InputTokens = result.InputTokens;
        job.OutputTokens = result.OutputTokens;
        job.Message = $"{written} card(s) ready for review across {topics.Count} topic(s)"
                      + (unserved > 0 ? $"; {unserved} topic(s) had no usable source material." : ".");

        db.GenerationJobs.Attach(job);
        db.Entry(job).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }
}
