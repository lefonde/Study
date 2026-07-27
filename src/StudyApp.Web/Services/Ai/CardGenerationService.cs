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
            ct);

        var batchId = Guid.NewGuid();
        foreach (var card in result.Value.Cards)
        {
            if (string.IsNullOrWhiteSpace(card.Front) || string.IsNullOrWhiteSpace(card.Back))
                continue;

            db.CardSuggestions.Add(new CardSuggestion
            {
                CourseId = material.CourseId,
                BatchId = batchId,
                Front = card.Front.Trim(),
                Back = card.Back.Trim(),
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
}
