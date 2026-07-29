namespace StudyApp.Core.Entities;

/// <summary>
/// How much a topic matters in this particular course.
///
/// Three tiers rather than a number: the evidence is "this appears in the exam" or "this is
/// defined but never assessed", which supports an ordering and not a score. A 0–100 weight would
/// invite precision the source material can't justify and would jitter on every re-map, making
/// a genuine shift in emphasis impossible to spot among the noise.
/// </summary>
public enum TopicImportance
{
    Peripheral = 0,
    Supporting = 1,
    Core = 2,
}

/// <summary>
/// How a material refers to a topic. <see cref="Assessed"/> is the load-bearing one: it is the
/// evidence that a topic is actually examinable rather than merely covered, and it is what the
/// study plan later uses to answer "what does this assignment require?".
/// </summary>
public enum TopicMention
{
    Referenced = 0,
    Defined = 1,
    Assessed = 2,
}

/// <summary>
/// One concept the course covers, derived from every ingested material rather than any single one.
///
/// Topics are long-lived: cards link to them, the user pins and dismisses them, and a re-map
/// revises them in place. That is why re-mapping proposes changes against existing rows instead
/// of rebuilding the set — a fresh derivation each time would churn names and orphan everything
/// attached to them.
/// </summary>
public class CourseTopic : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }

    /// <summary>Chapter/lesson this belongs to, when the material makes that clear.</summary>
    public Guid? UnitId { get; set; }
    public CourseUnit? Unit { get; set; }

    /// <summary>Canonical name, in the course's own language — never translated.</summary>
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";

    public TopicImportance Importance { get; set; }

    /// <summary>Why it sits at that tier, citing the evidence — e.g. "assessed in Maman 11 Q3".</summary>
    public string ImportanceReason { get; set; } = "";

    /// <summary>
    /// The user has set this tier by hand. A re-map must never propose changing it: the whole
    /// point of pinning is that your judgement outranks the model's on re-run.
    /// </summary>
    public bool ImportancePinned { get; set; }

    /// <summary>User said this isn't relevant. Kept, not deleted, so a re-map can't resurrect it.</summary>
    public bool IsDismissed { get; set; }

    /// <summary>When an applied revision last altered this, for the "what shifted" view.</summary>
    public DateTime? LastChangedAt { get; set; }
    public TopicImportance? PreviousImportance { get; set; }

    public List<TopicSource> Sources { get; set; } = [];

    /// <summary>Topics that have to be understood before this one. See <see cref="TopicPrerequisite"/>.</summary>
    public List<TopicPrerequisite> Prerequisites { get; set; } = [];

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Where a topic appears, and in what capacity. A real table rather than a JSON blob because the
/// reverse lookup — "which topics does this assignment assess?" — is the query the study plan is
/// built on.
/// </summary>
public class TopicSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseTopicId { get; set; }
    public CourseTopic? Topic { get; set; }

    public Guid MaterialId { get; set; }
    public Material? Material { get; set; }

    /// <summary>Human-readable locator within the material, e.g. "p. 14" or "Q3".</summary>
    public string? SourceReference { get; set; }

    public TopicMention Mention { get; set; }
}
