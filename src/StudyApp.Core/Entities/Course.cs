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

    /// <summary>
    /// How much each kind of material counts toward "what matters here" — see
    /// <see cref="AssessmentWeight"/>. Editable per course.
    ///
    /// Deliberately NOT initialised to <see cref="AssessmentWeight.Defaults"/>: EF materialises a
    /// Course by running this initialiser and then *appending* the rows from the JSON column to
    /// whatever it already holds, so a default here silently doubles every kind on load. New
    /// courses get their defaults from <c>CourseService.CreateAsync</c> instead, and reads go
    /// through <see cref="AssessmentWeight.Complete"/>.
    /// </summary>
    public List<AssessmentWeight> AssessmentWeights { get; set; } = [];

    public List<Deck> Decks { get; set; } = [];
    public List<CourseUnit> Units { get; set; } = [];
    public List<Material> Materials { get; set; } = [];
}
