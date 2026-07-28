namespace StudyApp.Core.Entities;

/// <summary>
/// How strongly one kind of material signals what actually matters in a course.
///
/// Per-course and user-editable, because nothing in the material itself reveals this: a course
/// examined by a single final and a course graded on weekly assignments can ship identical
/// textbooks, and a topic's importance depends entirely on which one you're taking. These
/// weights are what the course-mapping stage uses to decide whether a topic is Core or
/// incidental, so getting them wrong quietly skews everything downstream.
/// </summary>
public class AssessmentWeight
{
    public MaterialKind Kind { get; set; }

    /// <summary>0–100. Relative, not a percentage of anything — only the ordering matters.</summary>
    public int Weight { get; set; }

    /// <summary>
    /// The starting profile, applied to every new course: what a typical course looks like
    /// before the student says otherwise. Assessment materials outrank reference materials
    /// because they are evidence of what gets tested rather than of what merely gets covered.
    /// </summary>
    public static List<AssessmentWeight> Defaults() =>
    [
        new() { Kind = MaterialKind.Exam, Weight = 100 },
        new() { Kind = MaterialKind.HomeAssignment, Weight = 80 },
        new() { Kind = MaterialKind.Syllabus, Weight = 60 },
        new() { Kind = MaterialKind.LectureNotes, Weight = 40 },
        new() { Kind = MaterialKind.BookChapter, Weight = 30 },
        new() { Kind = MaterialKind.HandwrittenNotes, Weight = 30 },
        new() { Kind = MaterialKind.Screenshot, Weight = 10 },
        new() { Kind = MaterialKind.Other, Weight = 10 },
    ];

    /// <summary>
    /// Fills in any <see cref="MaterialKind"/> the stored profile is missing, preserving the
    /// weights already set. Without this, adding a new MaterialKind later would leave every
    /// existing course silently unable to weigh it.
    /// </summary>
    public static List<AssessmentWeight> Complete(IEnumerable<AssessmentWeight> stored)
    {
        // Grouped rather than ToDictionary: a repeated kind must not throw. Reading a profile is
        // on the path that renders a course page, so a malformed one should degrade to sane
        // weights, never take the page down with it.
        var byKind = stored
            .GroupBy(w => w.Kind)
            .ToDictionary(g => g.Key, g => g.First());

        return Defaults()
            .Select(d => byKind.TryGetValue(d.Kind, out var existing) ? existing : d)
            .ToList();
    }
}
