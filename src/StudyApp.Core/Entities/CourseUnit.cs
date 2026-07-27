namespace StudyApp.Core.Entities;

public enum CourseUnitKind
{
    Chapter = 0,
    Lesson = 1,
}

/// <summary>
/// A node in the course's chapter/lesson tree, in syllabus order. Chapters may contain
/// lessons (ParentId points at the chapter); top-level nodes have ParentId == null.
/// </summary>
public class CourseUnit : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public Guid? ParentId { get; set; }
    public CourseUnit? Parent { get; set; }
    public CourseUnitKind Kind { get; set; } = CourseUnitKind.Chapter;
    public string Title { get; set; } = "";
    public int Order { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public List<CourseUnit> Children { get; set; } = [];
}
