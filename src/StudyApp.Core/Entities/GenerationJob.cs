namespace StudyApp.Core.Entities;

public enum JobKind
{
    Ingest = 0,
    GenerateCards = 1,
    /// <summary>Course-scoped, so it carries no MaterialId — it reads every extract at once.</summary>
    MapCourse = 2,
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

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
