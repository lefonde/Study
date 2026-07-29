namespace StudyApp.Core.Entities;

public enum JobKind
{
    Ingest = 0,
    GenerateCards = 1,
    /// <summary>Course-scoped, so it carries no MaterialId — it reads every extract at once.</summary>
    MapCourse = 2,
    /// <summary>Course-scoped: links cards to the topics they teach. Re-run as cards accumulate.</summary>
    MapCoverage = 3,
    /// <summary>
    /// Reviews a submitted solution. MaterialId points at the <i>submission</i>; the assignment
    /// it answers is reached through <see cref="Material.SubmissionForId"/>, so no second
    /// material slot is needed here.
    /// </summary>
    ReviewSolution = 4,
    /// <summary>
    /// Course-scoped: works out what each existing topic builds on.
    ///
    /// Separate from <see cref="MapCourse"/> because the two want opposite instructions. Mapping
    /// is a revision that must resist churn — faced with no new evidence it correctly proposes
    /// nothing — whereas this one exists precisely to fill gaps in a map that is already settled,
    /// which is the only way a course mapped before dependencies existed ever gets a path.
    /// </summary>
    MapPrerequisites = 5,
}

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>
/// One unit of AI work, tracked so the user can watch progress, read the failure when
/// something goes wrong, and see what each run actually cost. Token counts are recorded from
/// day one — spend on someone else's API key should never be a surprise.
/// </summary>
public class GenerationJob : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public Guid? MaterialId { get; set; }
    public Material? Material { get; set; }

    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    /// <summary>Progress note while running, or the error message when failed.</summary>
    public string? Message { get; set; }

    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>Batch produced by a GenerateCards job, linking to its CardSuggestions.</summary>
    public Guid? BatchId { get; set; }

    /// <summary>
    /// Topics this run is aimed at, when generation was launched to fill specific gaps rather
    /// than to work through a material. Empty for an untargeted run.
    /// </summary>
    public List<Guid> TargetTopicIds { get; set; } = [];

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
