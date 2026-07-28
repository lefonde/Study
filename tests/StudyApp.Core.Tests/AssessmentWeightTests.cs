using StudyApp.Core.Entities;

namespace StudyApp.Core.Tests;

public class AssessmentWeightTests
{
    [Fact]
    public void Defaults_Cover_Every_Material_Kind()
    {
        // A kind missing from the profile would be unweighable, so this must never drift.
        var covered = AssessmentWeight.Defaults().Select(w => w.Kind).ToHashSet();
        Assert.Equal(Enum.GetValues<MaterialKind>().ToHashSet(), covered);
    }

    [Fact]
    public void Defaults_Rank_Assessment_Above_Reference_Material()
    {
        var byKind = AssessmentWeight.Defaults().ToDictionary(w => w.Kind, w => w.Weight);

        Assert.True(byKind[MaterialKind.Exam] > byKind[MaterialKind.HomeAssignment]);
        Assert.True(byKind[MaterialKind.HomeAssignment] > byKind[MaterialKind.LectureNotes]);
        Assert.True(byKind[MaterialKind.LectureNotes] > byKind[MaterialKind.Screenshot]);
    }

    /// <summary>
    /// The case every existing course hits: the column was added later, so stored profiles are
    /// empty and must fall back to defaults rather than leaving everything unweighted.
    /// </summary>
    [Fact]
    public void Empty_Stored_Profile_Falls_Back_To_Defaults()
    {
        var completed = AssessmentWeight.Complete([]);
        Assert.Equal(AssessmentWeight.Defaults().Count, completed.Count);
        Assert.Equal(100, completed.Single(w => w.Kind == MaterialKind.Exam).Weight);
    }

    [Fact]
    public void Stored_Weights_Win_Over_Defaults()
    {
        var stored = new List<AssessmentWeight>
        {
            new() { Kind = MaterialKind.Exam, Weight = 5 },
            new() { Kind = MaterialKind.LectureNotes, Weight = 95 },
        };

        var completed = AssessmentWeight.Complete(stored);

        // The user's inversion survives — this is a course graded on lectures, not its final.
        Assert.Equal(5, completed.Single(w => w.Kind == MaterialKind.Exam).Weight);
        Assert.Equal(95, completed.Single(w => w.Kind == MaterialKind.LectureNotes).Weight);
    }

    [Fact]
    public void Missing_Kinds_Are_Filled_Without_Disturbing_Present_Ones()
    {
        var stored = new List<AssessmentWeight> { new() { Kind = MaterialKind.Exam, Weight = 42 } };

        var completed = AssessmentWeight.Complete(stored);

        Assert.Equal(Enum.GetValues<MaterialKind>().Length, completed.Count);
        Assert.Equal(42, completed.Single(w => w.Kind == MaterialKind.Exam).Weight);
        Assert.Equal(80, completed.Single(w => w.Kind == MaterialKind.HomeAssignment).Weight);
    }

    /// <summary>
    /// Regression: a duplicated kind used to throw, taking the whole course page down with a 500.
    /// It happened for real — a property initialiser on Course.AssessmentWeights meant EF appended
    /// the JSON rows to a list that already held the defaults, doubling every kind on load.
    /// </summary>
    [Fact]
    public void Duplicate_Kinds_Do_Not_Throw()
    {
        var stored = new List<AssessmentWeight>
        {
            new() { Kind = MaterialKind.Exam, Weight = 70 },
            new() { Kind = MaterialKind.Exam, Weight = 100 },
            new() { Kind = MaterialKind.BookChapter, Weight = 15 },
        };

        var completed = AssessmentWeight.Complete(stored);

        Assert.Equal(Enum.GetValues<MaterialKind>().Length, completed.Count);
        Assert.Equal(70, completed.Single(w => w.Kind == MaterialKind.Exam).Weight);  // first wins
        Assert.Equal(15, completed.Single(w => w.Kind == MaterialKind.BookChapter).Weight);
    }

    [Fact]
    public void Result_Is_Always_In_A_Stable_Order()
    {
        // The editor renders this list directly; rows must not reshuffle between loads.
        var shuffled = AssessmentWeight.Defaults().OrderByDescending(w => w.Kind).ToList();
        Assert.Equal(
            AssessmentWeight.Complete([]).Select(w => w.Kind),
            AssessmentWeight.Complete(shuffled).Select(w => w.Kind));
    }
}
