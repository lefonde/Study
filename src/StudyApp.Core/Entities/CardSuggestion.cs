namespace StudyApp.Core.Entities;

public enum SuggestionStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

/// <summary>
/// A generated card waiting for the user's judgement. Nothing the AI writes enters a deck —
/// and therefore the scheduler — without being approved here first, so a bad extraction or a
/// hallucinated definition can't silently poison months of review.
/// </summary>
public class CardSuggestion : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }

    /// <summary>Batch this was generated in, so the inbox can group and bulk-act on one run.</summary>
    public Guid BatchId { get; set; }

    /// <summary>Markdown with inline LaTeX, same authoring syntax as a hand-written card.</summary>
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";

    public Guid? UnitId { get; set; }
    public CourseUnit? Unit { get; set; }

    /// <summary>
    /// The topic this was written for, when generation was aimed at one. Carried across to a
    /// <see cref="CardTopic"/> on approval, so a targeted card counts toward the gap it was
    /// written to fill without a second AI pass to work out where it belongs.
    /// </summary>
    public Guid? CourseTopicId { get; set; }
    public CourseTopic? Topic { get; set; }

    public Guid? SourceMaterialId { get; set; }
    public Material? SourceMaterial { get; set; }
    /// <summary>Human-readable locator within the source, e.g. "p. 14".</summary>
    public string? SourceReference { get; set; }

    /// <summary>Why the model thought this was worth a card — shown in the inbox to justify keeping it.</summary>
    public string? Rationale { get; set; }

    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;
    /// <summary>Set once accepted, linking to the card that was created from it.</summary>
    public Guid? AcceptedCardId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
