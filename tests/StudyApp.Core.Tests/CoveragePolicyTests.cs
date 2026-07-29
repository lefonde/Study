using StudyApp.Core.Entities;
using StudyApp.Core.Planning;

namespace StudyApp.Core.Tests;

public class CoveragePolicyTests
{
    private static TopicCoverage Cov(
        string name, int cards, TopicImportance importance = TopicImportance.Supporting,
        bool dismissed = false) =>
        new(new CourseTopic { Name = name, Importance = importance, IsDismissed = dismissed }, cards);

    [Fact]
    public void Empty_Course_Reports_Empty()
    {
        Assert.Equal(CoverageReport.Empty, CoveragePolicy.Summarise([]));
    }

    [Fact]
    public void Coverage_Counts_Topics_With_At_Least_One_Card()
    {
        var report = CoveragePolicy.Summarise([
            Cov("Deadlock", 3),
            Cov("Scheduling", 1),
            Cov("Paging", 0),
            Cov("Semaphores", 0),
        ]);

        Assert.Equal(4, report.TotalTopics);
        Assert.Equal(2, report.CoveredTopics);
        Assert.Equal(0.5, report.Coverage);
    }

    /// <summary>
    /// The case the whole feature exists for: overall coverage looks healthy while the topics the
    /// course actually assesses have nothing behind them.
    /// </summary>
    [Fact]
    public void Core_Coverage_Can_Be_Far_Worse_Than_Overall()
    {
        var report = CoveragePolicy.Summarise([
            Cov("Deadlock", 0, TopicImportance.Core),
            Cov("Banker's algorithm", 0, TopicImportance.Core),
            Cov("Trivia A", 5, TopicImportance.Peripheral),
            Cov("Trivia B", 5, TopicImportance.Peripheral),
            Cov("Trivia C", 5, TopicImportance.Peripheral),
        ]);

        Assert.Equal(0.6, report.Coverage);          // reassuring
        Assert.Equal(0.0, report.CoreCoverage);      // and completely wrong
        Assert.Equal(2, report.UncoveredCoreTopics);
    }

    [Fact]
    public void Dismissed_Topics_Leave_The_Denominator_Entirely()
    {
        var report = CoveragePolicy.Summarise([
            Cov("Deadlock", 1, TopicImportance.Core),
            Cov("Not relevant to me", 0, TopicImportance.Core, dismissed: true),
        ]);

        // Dismissing must not leave a permanent dent in coverage.
        Assert.Equal(1, report.TotalTopics);
        Assert.Equal(1.0, report.Coverage);
        Assert.Equal(1.0, report.CoreCoverage);
    }

    [Fact]
    public void A_Course_With_No_Core_Topics_Is_Fully_Core_Covered()
    {
        // 0/0 reported as 0% would read as a failure; there is simply nothing outstanding.
        var report = CoveragePolicy.Summarise([Cov("Aside", 0, TopicImportance.Peripheral)]);

        Assert.Equal(0, report.CoreTopics);
        Assert.Equal(1.0, report.CoreCoverage);
        Assert.Equal(0.0, report.Coverage);
    }

    [Fact]
    public void Fully_Covered_Course_Reports_One()
    {
        var report = CoveragePolicy.Summarise([
            Cov("Deadlock", 2, TopicImportance.Core),
            Cov("Scheduling", 1),
        ]);

        Assert.Equal(1.0, report.Coverage);
        Assert.Equal(1.0, report.CoreCoverage);
        Assert.Equal(0, report.UncoveredCoreTopics);
    }

    // --- gaps ---

    [Fact]
    public void Gaps_Are_Uncovered_Topics_Most_Important_First()
    {
        var gaps = CoveragePolicy.Gaps([
            Cov("Aside", 0, TopicImportance.Peripheral),
            Cov("Deadlock", 0, TopicImportance.Core),
            Cov("Scheduling", 0, TopicImportance.Supporting),
            Cov("Covered", 4, TopicImportance.Core),
        ]);

        Assert.Equal(["Deadlock", "Scheduling", "Aside"], gaps.Select(g => g.Topic.Name));
    }

    [Fact]
    public void Gaps_Exclude_Dismissed_Topics()
    {
        var gaps = CoveragePolicy.Gaps([
            Cov("Deadlock", 0, TopicImportance.Core),
            Cov("Ignored", 0, TopicImportance.Core, dismissed: true),
        ]);

        Assert.Equal(["Deadlock"], gaps.Select(g => g.Topic.Name));
    }

    [Fact]
    public void Equally_Important_Gaps_Are_Ordered_By_Name()
    {
        var gaps = CoveragePolicy.Gaps([
            Cov("Zebra", 0, TopicImportance.Core),
            Cov("alpha", 0, TopicImportance.Core),
        ]);

        // Case-insensitive, so the list doesn't jump around on capitalisation.
        Assert.Equal(["alpha", "Zebra"], gaps.Select(g => g.Topic.Name));
    }
}
