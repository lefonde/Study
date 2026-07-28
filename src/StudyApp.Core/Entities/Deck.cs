namespace StudyApp.Core.Entities;

public class Deck : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Chapter/lesson this deck belongs to, for unit- and course-level progress rollups.</summary>
    public Guid? UnitId { get; set; }
    public CourseUnit? Unit { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public List<Card> Cards { get; set; } = [];
}
