namespace StudyApp.Core.Entities;

public enum ReviewVerdict
{
    Correct = 0,
    /// <summary>Right idea, but something in the working or the conclusion is wrong.</summary>
    Partly = 1,
    Incorrect = 2,
    /// <summary>The question was not answered at all.</summary>
    Missing = 3,
}

/// <summary>
/// What the model made of one question in a submitted solution.
///
/// <see cref="CourseTopicId"/> is the point of the whole feature: a wrong answer becomes a
/// concrete statement about a topic, which the map can show and gap-targeted generation can act
/// on. Without it this would be prose you read once and forget.
/// </summary>
public class ReviewFinding
{
    /// <summary>The question as the assignment labels it — "Q3(b)", "שאלה 2".</summary>
    public string Question { get; set; } = "";
    public ReviewVerdict Verdict { get; set; }

    /// <summary>What is wrong and why, or what was done well. Markdown.</summary>
    public string Comment { get; set; } = "";

    /// <summary>The topic this question turns on, once resolved from the model's short ref.</summary>
    public Guid? CourseTopicId { get; set; }
}

/// <summary>
/// Feedback on one uploaded solution.
///
/// There is deliberately no score or predicted grade. It is the obvious thing to add and the
/// wrong one — the same argument that keeps coverage and mastery from being blended into a single
/// number. A figure invented by a model that has not seen the marking scheme would carry an
/// authority it has not earned, and per-question verdicts say the same thing honestly.
/// </summary>
public class SubmissionReview : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The submission being reviewed.</summary>
    public Guid MaterialId { get; set; }
    public Material? Material { get; set; }

    public Guid? JobId { get; set; }

    /// <summary>A paragraph: what went well, what to work on. Markdown.</summary>
    public string Summary { get; set; } = "";

    public List<ReviewFinding> Findings { get; set; } = [];

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    public int CountOf(ReviewVerdict verdict) => Findings.Count(f => f.Verdict == verdict);

    /// <summary>Topics the student got something wrong on — the actionable output.</summary>
    public IEnumerable<Guid> WeakTopicIds => Findings
        .Where(f => f.Verdict is ReviewVerdict.Partly or ReviewVerdict.Incorrect or ReviewVerdict.Missing)
        .Select(f => f.CourseTopicId)
        .OfType<Guid>()
        .Distinct();
}
