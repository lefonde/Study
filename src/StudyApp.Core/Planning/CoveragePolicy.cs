using StudyApp.Core.Entities;

namespace StudyApp.Core.Planning;

/// <summary>One topic and how many cards address it.</summary>
public record TopicCoverage(CourseTopic Topic, int CardCount)
{
    public bool IsCovered => CardCount > 0;
}

/// <summary>
/// How much of a course actually has cards. Reported <b>beside</b> mastery, never merged into it —
/// see <see cref="CoveragePolicy"/>.
/// </summary>
public record CoverageReport(
    int TotalTopics,
    int CoveredTopics,
    int CoreTopics,
    int CoveredCoreTopics,
    double Coverage,
    double CoreCoverage)
{
    public static CoverageReport Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public int UncoveredCoreTopics => CoreTopics - CoveredCoreTopics;
}

/// <summary>
/// The second half of an honest progress picture.
///
/// Mastery answers "how well do I know my cards?". It cannot answer "do my cards cover the
/// course?", and quietly reads 100% for a chapter with three cards in it — which was the known
/// limitation when progress tracking shipped. Coverage answers the second question, and the two
/// are deliberately kept apart: blending them into one percentage would hide which half is
/// actually the problem, and they call for opposite responses. Low mastery means study; low
/// coverage means write more cards.
///
/// <see cref="CoverageReport.CoreCoverage"/> is the number worth acting on. Overall coverage
/// counts peripheral topics you may never be tested on, so it can look reassuring while the
/// topics the course actually assesses have nothing behind them.
/// </summary>
public static class CoveragePolicy
{
    /// <summary>
    /// Dismissed topics are excluded entirely. Saying a topic isn't relevant to you should
    /// remove it from the denominator, not leave it permanently counting against your coverage.
    /// </summary>
    public static CoverageReport Summarise(IEnumerable<TopicCoverage> coverage)
    {
        var counted = coverage.Where(c => !c.Topic.IsDismissed).ToList();
        if (counted.Count == 0)
            return CoverageReport.Empty;

        var core = counted.Where(c => c.Topic.Importance == TopicImportance.Core).ToList();
        var covered = counted.Count(c => c.IsCovered);
        var coveredCore = core.Count(c => c.IsCovered);

        return new CoverageReport(
            TotalTopics: counted.Count,
            CoveredTopics: covered,
            CoreTopics: core.Count,
            CoveredCoreTopics: coveredCore,
            Coverage: (double)covered / counted.Count,
            // A course with no core topics is fully covered on that axis rather than 0/0 — there
            // is nothing outstanding, and reporting 0% would read as a failure that isn't one.
            CoreCoverage: core.Count == 0 ? 1.0 : (double)coveredCore / core.Count);
    }

    /// <summary>
    /// Topics with no cards, most important first — the actionable list, and the input to
    /// gap-targeted generation.
    /// </summary>
    public static IReadOnlyList<TopicCoverage> Gaps(IEnumerable<TopicCoverage> coverage) =>
        coverage
            .Where(c => !c.Topic.IsDismissed && !c.IsCovered)
            .OrderByDescending(c => c.Topic.Importance)
            .ThenBy(c => c.Topic.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
