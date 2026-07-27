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
/// An uploaded source document (PDF, image, scan). The raw file is read exactly once, by
/// ingestion, which produces the reusable <see cref="MaterialExtract"/>; everything after
/// that works from the extract instead.
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

    /// <summary>The AI-ready form of this file, once ingested. Null until then.</summary>
    public MaterialExtract? Extract { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
