using StudyApp.Core.Entities;
using StudyApp.Core.Planning;

namespace StudyApp.Core.Tests;

public class UnitTreeTests
{
    private static CourseUnit Unit(string title, Guid? parentId = null) => new()
    {
        Title = title,
        ParentId = parentId,
        Kind = parentId is null ? CourseUnitKind.Chapter : CourseUnitKind.Lesson,
    };

    /// <summary>A set has no order of its own; compare contents deterministically.</summary>
    private static void AssertSame(IEnumerable<Guid> expected, IEnumerable<Guid> actual) =>
        Assert.Equal(expected.Order(), actual.Order());

    [Fact]
    public void A_Chapter_Includes_Itself_And_Its_Lessons()
    {
        var chapter = Unit("Chapter 3");
        var lessonA = Unit("3.1", chapter.Id);
        var lessonB = Unit("3.2", chapter.Id);

        AssertSame(
            [chapter.Id, lessonA.Id, lessonB.Id],
            UnitTree.WithDescendants([chapter, lessonA, lessonB], chapter.Id));
    }

    [Fact]
    public void A_Sibling_Chapters_Lessons_Are_Excluded()
    {
        var wanted = Unit("Chapter 3");
        var lesson = Unit("3.1", wanted.Id);
        var other = Unit("Chapter 4");
        var othersLesson = Unit("4.1", other.Id);

        AssertSame(
            [wanted.Id, lesson.Id],
            UnitTree.WithDescendants([wanted, lesson, other, othersLesson], wanted.Id));
    }

    [Fact]
    public void A_Lesson_Is_Just_Itself()
    {
        var chapter = Unit("Chapter 3");
        var lesson = Unit("3.1", chapter.Id);

        AssertSame([lesson.Id], UnitTree.WithDescendants([chapter, lesson], lesson.Id));
    }

    [Fact]
    public void Nesting_Deeper_Than_Two_Levels_Still_Resolves()
    {
        var top = Unit("Part I");
        var middle = Unit("Chapter 3", top.Id);
        var leaf = Unit("3.1", middle.Id);

        AssertSame([top.Id, middle.Id, leaf.Id], UnitTree.WithDescendants([top, middle, leaf], top.Id));
    }

    /// <summary>
    /// ParentId is a plain nullable Guid with nothing preventing a loop from reaching the database.
    /// Finding that out via a stack overflow while building a study session would be poor.
    /// </summary>
    [Fact]
    public void A_Cyclic_Parent_Chain_Terminates()
    {
        var a = Unit("A");
        var b = Unit("B", a.Id);
        a.ParentId = b.Id;

        AssertSame([a.Id, b.Id], UnitTree.WithDescendants([a, b], a.Id));
    }

    [Fact]
    public void An_Unknown_Unit_Resolves_To_Itself_Alone()
    {
        var stranger = Guid.NewGuid();
        AssertSame([stranger], UnitTree.WithDescendants([Unit("Chapter 1")], stranger));
    }
}
