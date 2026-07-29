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
    /// <summary>
    /// The student's own answer to an assignment, uploaded for review.
    ///
    /// Deliberately excluded from the course map and from the generation glossary: a submission
    /// records what the student thinks, not what the course teaches, and letting it into either
    /// would make their own mistakes authoritative.
    /// </summary>
    Submission = 8,
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

    /// <summary>
    /// When this assignment was handed in. Null means still outstanding.
    ///
    /// A timestamp rather than a flag: the map shows a submitted assignment as a milestone you
    /// passed on a date, which is more use than merely knowing it is no longer pending.
    /// </summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>
    /// For a <see cref="MaterialKind.Submission"/>, the assignment it answers. Null otherwise.
    ///
    /// This is also how a review job finds its second input: the job points at the submission and
    /// walks back through here for the questions, so no second material slot is needed on the job.
    /// </summary>
    public Guid? SubmissionForId { get; set; }
    public Material? SubmissionFor { get; set; }

    public MaterialStatus Status { get; set; } = MaterialStatus.Uploaded;

    /// <summary>The AI-ready form of this file, once ingested. Null until then.</summary>
    public MaterialExtract? Extract { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
