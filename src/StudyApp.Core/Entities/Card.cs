namespace StudyApp.Core.Entities;

public enum CardState
{
    New = 0,
    Learning = 1,
    Review = 2,
}

public class Card : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeckId { get; set; }
    public Deck? Deck { get; set; }
    /// <summary>Markdown, with inline LaTeX (\(...\) / \[...\]) supported.</summary>
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";

    /// <summary>Chapter/lesson this card belongs to, if tagged.</summary>
    public Guid? UnitId { get; set; }
    public CourseUnit? Unit { get; set; }
    /// <summary>The material this card was derived from, if AI-generated (v0.3+).</summary>
    public Guid? SourceMaterialId { get; set; }
    public Material? SourceMaterial { get; set; }
    /// <summary>Human-readable locator within the source material, e.g. "p. 14" or "§2.3".</summary>
    public string? SourceReference { get; set; }

    // Scheduling state, owned by IScheduler implementations.
    public CardState State { get; set; } = CardState.New;
    /// <summary>UTC. Null until the card has been scheduled at least once.</summary>
    public DateTime? Due { get; set; }
    public double IntervalDays { get; set; }
    public double EaseFactor { get; set; } = 2.5;
    /// <summary>Consecutive successful reviews since the last lapse.</summary>
    public int Repetitions { get; set; }
    public int Lapses { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
