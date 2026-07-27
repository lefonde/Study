namespace StudyApp.Core.Entities;

public enum MaterialKind
{
    Syllabus = 0,
    BookChapter = 1,
    LectureNotes = 2,
    HandwrittenNotes = 3,
    HomeAssignment = 4,
    Exam = 5,
    Screenshot = 6,
    Other = 7,
}

public enum MaterialStatus
{
    Uploaded = 0,
    Ingested = 1,
    Failed = 2,
}

/// <summary>
/// An uploaded source document (PDF, image, scan). Ingestion (AI extraction) is a later
/// phase — v0.2 only stores and organizes the raw file.
/// </summary>
public class Material : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public Guid? UnitId { get; set; }
    public CourseUnit? Unit { get; set; }

    public MaterialKind Kind { get; set; } = MaterialKind.Other;
    public string Title { get; set; } = "";
    /// <summary>Path relative to the course's material storage directory.</summary>
    public string FilePath { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>Set only for HomeAssignment materials.</summary>
    public DateTime? DueDate { get; set; }
    public MaterialStatus Status { get; set; } = MaterialStatus.Uploaded;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
