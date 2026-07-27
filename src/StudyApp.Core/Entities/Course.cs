namespace StudyApp.Core.Entities;

public class Course : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4f6df5";
    public string NotesMarkdown { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>The student's current position in the course structure — "where I am".</summary>
    public Guid? CurrentUnitId { get; set; }
    public CourseUnit? CurrentUnit { get; set; }

    public List<Deck> Decks { get; set; } = [];
    public List<CourseUnit> Units { get; set; } = [];
    public List<Material> Materials { get; set; } = [];
}
