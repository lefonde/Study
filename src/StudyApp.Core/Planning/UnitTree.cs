using StudyApp.Core.Entities;

namespace StudyApp.Core.Planning;

/// <summary>
/// Navigation of the course's chapter/lesson tree.
///
/// Pure and here in Core for the same reason the other policies are: it is arithmetic over data the
/// app already holds, and "which units does this chapter contain" is a question two different
/// callers must answer identically.
/// </summary>
public static class UnitTree
{
    /// <summary>
    /// A unit and every unit beneath it, in no particular order.
    ///
    /// Reviewing a chapter has to include its lessons' cards — asking to study chapter 3 and being
    /// handed only the handful of cards attached to the chapter node itself, while its four lessons
    /// sit untouched, is not what anyone means. Progress reporting deliberately does <i>not</i> do
    /// this (see <c>ProgressService.GetUnitProgressAsync</c>): the Structure tab lists every unit as
    /// its own row, so rolling descendants up there would count the same card twice on the page.
    ///
    /// Walked iteratively with a visited set. The tree is two levels deep by design, but a
    /// <see cref="CourseUnit.ParentId"/> is a plain nullable Guid with nothing stopping a cycle from
    /// reaching the database, and a stack overflow while building a study session would be a poor
    /// way to find out.
    /// </summary>
    public static HashSet<Guid> WithDescendants(IEnumerable<CourseUnit> all, Guid rootId)
    {
        var childrenOf = all
            .Where(u => u.ParentId is not null)
            .GroupBy(u => u.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(u => u.Id).ToList());

        var found = new HashSet<Guid> { rootId };
        var pending = new Stack<Guid>([rootId]);

        while (pending.Count > 0)
        {
            if (!childrenOf.TryGetValue(pending.Pop(), out var children))
                continue;

            foreach (var child in children)
                if (found.Add(child))
                    pending.Push(child);
        }

        return found;
    }
}
