using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Import;
using StudyApp.Core.Scheduling;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services;

public record DeckStats(Guid DeckId, string Name, int Total, int Due, int New, Guid? UnitId);

public class DeckService(IDbContextFactory<StudyDbContext> factory, DuePolicy duePolicy)
{
    public async Task<List<DeckStats>> GetStatsForCourseAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cutoff = duePolicy.TodayCutoffUtc();
        return await db.Decks.AsNoTracking()
            .Where(d => d.CourseId == courseId)
            .OrderBy(d => d.Name)
            .Select(d => new DeckStats(
                d.Id,
                d.Name,
                d.Cards.Count,
                d.Cards.Count(c => c.State != CardState.New && c.Due < cutoff),
                d.Cards.Count(c => c.State == CardState.New),
                d.UnitId))
            .ToListAsync();
    }

    public async Task<Deck?> GetAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Decks.AsNoTracking()
            .Include(d => d.Course)
            .Include(d => d.Cards.OrderByDescending(c => c.CreatedAt))
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<Deck> CreateAsync(Guid courseId, string name)
    {
        await using var db = await factory.CreateDbContextAsync();
        var deck = new Deck { CourseId = courseId, Name = name.Trim() };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        return deck;
    }

    public async Task RenameAsync(Guid id, string name)
    {
        await using var db = await factory.CreateDbContextAsync();
        var deck = await db.Decks.FirstAsync(d => d.Id == id);
        deck.Name = name.Trim();
        await db.SaveChangesAsync();
    }

    /// <summary>Files a deck under a chapter/lesson, or clears it — the basis of the unit-level progress rollup.</summary>
    public async Task SetUnitAsync(Guid id, Guid? unitId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var deck = await db.Decks.FirstAsync(d => d.Id == id);
        deck.UnitId = unitId;
        await db.SaveChangesAsync();
    }

    public async Task SoftDeleteAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var deck = await db.Decks.Include(d => d.Cards).FirstAsync(d => d.Id == id);
        deck.IsDeleted = true;
        foreach (var card in deck.Cards)
            card.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    public async Task<Card> AddCardAsync(Guid deckId, string front, string back)
    {
        await using var db = await factory.CreateDbContextAsync();
        var card = new Card { DeckId = deckId, Front = front.Trim(), Back = back.Trim() };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return card;
    }

    public async Task UpdateCardAsync(Guid cardId, string front, string back)
    {
        await using var db = await factory.CreateDbContextAsync();
        var card = await db.Cards.FirstAsync(c => c.Id == cardId);
        card.Front = front.Trim();
        card.Back = back.Trim();
        await db.SaveChangesAsync();
    }

    public async Task SoftDeleteCardAsync(Guid cardId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var card = await db.Cards.FirstAsync(c => c.Id == cardId);
        card.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    public async Task<List<string>> GetFrontsAsync(Guid deckId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Cards.AsNoTracking()
            .Where(c => c.DeckId == deckId)
            .Select(c => c.Front)
            .ToListAsync();
    }

    /// <summary>Imports parsed cards; duplicates are skipped unless explicitly included.</summary>
    public async Task<int> ImportAsync(Guid deckId, IEnumerable<ParsedCard> cards, bool includeDuplicates = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        var toAdd = cards
            .Where(c => includeDuplicates || !c.IsDuplicate)
            .Select(c => new Card { DeckId = deckId, Front = c.Front, Back = c.Back })
            .ToList();
        db.Cards.AddRange(toAdd);
        await db.SaveChangesAsync();
        return toAdd.Count;
    }
}
