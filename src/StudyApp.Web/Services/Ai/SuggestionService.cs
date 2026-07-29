using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// The review inbox. Generated cards sit here until the user accepts them; only then do they
/// become real <see cref="Card"/>s and enter scheduling. Accepting carries the provenance
/// across, so months later it's still possible to ask "where did this card come from?".
/// </summary>
public class SuggestionService(IDbContextFactory<StudyDbContext> factory)
{
    public async Task<List<CardSuggestion>> GetPendingAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.CardSuggestions.AsNoTracking()
            .Where(s => s.CourseId == courseId && s.Status == SuggestionStatus.Pending)
            .Include(s => s.SourceMaterial)
            .Include(s => s.Unit)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<int> GetPendingCountAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.CardSuggestions
            .CountAsync(s => s.CourseId == courseId && s.Status == SuggestionStatus.Pending);
    }

    /// <summary>Accepts one suggestion into a deck, optionally with user edits applied first.</summary>
    public async Task AcceptAsync(Guid suggestionId, Guid deckId, string? editedFront = null, string? editedBack = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var suggestion = await db.CardSuggestions.FirstAsync(s => s.Id == suggestionId);

        var card = new Card
        {
            DeckId = deckId,
            Front = (editedFront ?? suggestion.Front).Trim(),
            Back = (editedBack ?? suggestion.Back).Trim(),
            UnitId = suggestion.UnitId,
            SourceMaterialId = suggestion.SourceMaterialId,
            SourceReference = suggestion.SourceReference,
        };
        db.Cards.Add(card);
        LinkTopic(db, card, suggestion);

        suggestion.Status = SuggestionStatus.Accepted;
        suggestion.AcceptedCardId = card.Id;
        if (editedFront is not null)
            suggestion.Front = editedFront.Trim();
        if (editedBack is not null)
            suggestion.Back = editedBack.Trim();

        await db.SaveChangesAsync();
    }

    public async Task RejectAsync(Guid suggestionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var suggestion = await db.CardSuggestions.FirstAsync(s => s.Id == suggestionId);
        suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync();
    }

    /// <summary>Accepts everything still pending in a batch — the "these all look right" path.</summary>
    public async Task<int> AcceptBatchAsync(Guid batchId, Guid deckId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var pending = await db.CardSuggestions
            .Where(s => s.BatchId == batchId && s.Status == SuggestionStatus.Pending)
            .ToListAsync();

        foreach (var suggestion in pending)
        {
            var card = new Card
            {
                DeckId = deckId,
                Front = suggestion.Front,
                Back = suggestion.Back,
                UnitId = suggestion.UnitId,
                SourceMaterialId = suggestion.SourceMaterialId,
                SourceReference = suggestion.SourceReference,
            };
            db.Cards.Add(card);
            LinkTopic(db, card, suggestion);
            suggestion.Status = SuggestionStatus.Accepted;
            suggestion.AcceptedCardId = card.Id;
        }

        await db.SaveChangesAsync();
        return pending.Count;
    }

    /// <summary>
    /// Carries a targeted suggestion's topic across to the new card, so a card written to fill a
    /// specific gap counts toward that gap immediately — without waiting on a coverage pass to
    /// rediscover what was already known when it was generated.
    /// </summary>
    private static void LinkTopic(StudyDbContext db, Card card, CardSuggestion suggestion)
    {
        if (suggestion.CourseTopicId is { } topicId)
            db.CardTopics.Add(new CardTopic { CardId = card.Id, CourseTopicId = topicId });
    }

    public async Task<int> RejectBatchAsync(Guid batchId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var pending = await db.CardSuggestions
            .Where(s => s.BatchId == batchId && s.Status == SuggestionStatus.Pending)
            .ToListAsync();
        foreach (var suggestion in pending)
            suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync();
        return pending.Count;
    }
}
