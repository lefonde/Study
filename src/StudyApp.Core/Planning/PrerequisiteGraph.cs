using StudyApp.Core.Entities;

namespace StudyApp.Core.Planning;

/// <summary>One dependency: <paramref name="TopicId"/> needs <paramref name="PrerequisiteId"/> first.</summary>
public record TopicEdge(Guid TopicId, Guid PrerequisiteId);

/// <summary>One rung of the learning path. <paramref name="Index"/> is zero-based.</summary>
public record TopicStage(int Index, IReadOnlyList<CourseTopic> Topics);

/// <summary>
/// A cell held open in a stage that an edge only passes over, so a long link can be drawn through
/// empty space instead of behind the cards in between.
///
/// The renderer has to place something at every lane: a CSS grid does not create a row nothing is
/// placed in, so without a placeholder the reservation would exist only in the row numbering and
/// the bands would silently shift.
/// </summary>
public record EdgeLane(Guid TopicId, Guid PrerequisiteId, int Stage, int Row);

/// <summary>
/// The course laid out as an order you can follow, plus the edges that produced it.
/// <paramref name="Edges"/> is the visible edge set — dismissed topics have already been spliced
/// out — so a renderer can draw exactly what it is given without re-deriving anything.
///
/// <paramref name="RowByTopic"/> is the other half of the layout, and the renderer must honour it:
/// a row is the same vertical band in <i>every</i> stage, which is what makes a link between two
/// stages a short horizontal line rather than a long diagonal. Rows a stage leaves empty are
/// usually lanes held open for an edge passing over that stage.
/// </summary>
public record ProgressionMap(
    IReadOnlyList<TopicStage> Stages,
    IReadOnlyList<TopicEdge> Edges,
    IReadOnlyDictionary<Guid, int> StageByTopic,
    IReadOnlyDictionary<Guid, int> RowByTopic,
    int RowCount,
    IReadOnlyList<EdgeLane> Lanes)
{
    public static ProgressionMap Empty { get; } =
        new([], [], new Dictionary<Guid, int>(), new Dictionary<Guid, int>(), 0, []);

    /// <summary>True when nothing depends on anything — the map is a flat list, not a path.</summary>
    public bool HasAnyEdges => Edges.Count > 0;

    public IReadOnlyList<Guid> PrerequisitesOf(Guid topicId) =>
        [.. Edges.Where(e => e.TopicId == topicId).Select(e => e.PrerequisiteId)];

    public IReadOnlyList<Guid> UnlockedBy(Guid topicId) =>
        [.. Edges.Where(e => e.PrerequisiteId == topicId).Select(e => e.TopicId)];
}

/// <summary>
/// Turns topics and their dependencies into stages: what to learn first, what that unlocks, and
/// what leans on everything before it.
///
/// Pure, and in Core beside <see cref="CoveragePolicy"/> and <see cref="StudyPlanPolicy"/> for the
/// same reason they are — it is arithmetic over data the app already holds, so it costs nothing,
/// is never stale, and can be tested against a graph drawn by hand.
/// </summary>
public static class PrerequisiteGraph
{
    /// <summary>
    /// The edge set carried by a loaded set of topics, ready for <see cref="Build"/>.
    ///
    /// Every caller that has topics in hand needs this same projection off
    /// <see cref="CourseTopic.Prerequisites"/>, and three copies of it is three chances for one of
    /// them to drift. Requires the topics to have been loaded with their prerequisites included —
    /// <c>CourseMapService.GetTopicsAsync</c> does.
    /// </summary>
    public static List<TopicEdge> EdgesOf(IEnumerable<CourseTopic> topics) =>
        [.. topics
            .SelectMany(t => t.Prerequisites)
            .Select(p => new TopicEdge(p.CourseTopicId, p.PrerequisiteTopicId))];

    /// <summary>
    /// Lays topics out in stages by longest path: a topic sits one stage after the deepest thing
    /// it needs. Longest path rather than shortest, because a topic is only genuinely reachable
    /// once <i>every</i> prerequisite is — taking the shortest would place it before something it
    /// depends on.
    /// </summary>
    public static ProgressionMap Build(IEnumerable<CourseTopic> topics, IEnumerable<TopicEdge> edges)
    {
        var all = topics.ToList();
        if (all.Count == 0)
            return ProgressionMap.Empty;

        var visible = all.Where(t => !t.IsDismissed).ToDictionary(t => t.Id);
        var dismissed = all.Where(t => t.IsDismissed).Select(t => t.Id).ToHashSet();

        var live = Bypass(edges, dismissed)
            .Where(e => visible.ContainsKey(e.TopicId) && visible.ContainsKey(e.PrerequisiteId))
            .Distinct()
            .ToList();

        var prerequisitesOf = live
            .GroupBy(e => e.TopicId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.PrerequisiteId).ToList());

        var depths = new Dictionary<Guid, int>();
        foreach (var id in visible.Keys)
            StageOf(id, prerequisitesOf, depths, []);

        // Depths are already gap-free in a DAG — a topic at depth k has a parent at k-1, and so on
        // down to 0 — but a cycle can leave holes, and a renderer showing "stage 1, stage 3" would
        // look broken. Compacting makes contiguity a property of the result rather than of the
        // input, so nothing downstream has to care.
        var compacted = depths.Values.Distinct().Order()
            .Select((depth, index) => (depth, index))
            .ToDictionary(x => x.depth, x => x.index);

        var stageByTopic = depths.ToDictionary(kv => kv.Key, kv => compacted[kv.Value]);

        // Ordering within a stage, and the row each topic occupies. Deliberately not importance
        // order any more: position has to answer to the edges, or the drawn path cannot be
        // followed however much room the page has. See LayeredLayout.
        var layout = LayeredLayout.Arrange(visible, live, stageByTopic);

        var stages = Enumerable.Range(0, stageByTopic.Values.DefaultIfEmpty(-1).Max() + 1)
            .Select(index => new TopicStage(
                index,
                layout.TopicsByStage.GetValueOrDefault(index, [])))
            .ToList();

        return new ProgressionMap(
            stages, live, stageByTopic, layout.RowByTopic, layout.RowCount, layout.Lanes);
    }

    /// <summary>
    /// How many drawn links cross each other in this layout. Public so the quality of the layout
    /// can be asserted as a number rather than judged by eye — the whole point of the ordering
    /// work is to drive this down.
    /// </summary>
    public static int CountCrossings(ProgressionMap map)
    {
        var placed = map.Edges
            .Where(e => map.RowByTopic.ContainsKey(e.TopicId)
                        && map.RowByTopic.ContainsKey(e.PrerequisiteId))
            .Select(e => (
                FromStage: map.StageByTopic[e.PrerequisiteId],
                FromRow: map.RowByTopic[e.PrerequisiteId],
                ToStage: map.StageByTopic[e.TopicId],
                ToRow: map.RowByTopic[e.TopicId]))
            .ToList();

        var total = 0;
        for (var a = 0; a < placed.Count; a++)
            for (var b = a + 1; b < placed.Count; b++)
            {
                var (fs1, fr1, ts1, tr1) = placed[a];
                var (fs2, fr2, ts2, tr2) = placed[b];

                // Only links running between the same pair of stages can be compared this way;
                // anything else is a different span and its crossings are a routing question.
                if (fs1 != fs2 || ts1 != ts2)
                    continue;

                if ((fr1 - fr2) * (tr1 - tr2) < 0)
                    total++;
            }

        return total;
    }

    /// <summary>
    /// Would adding this edge create a loop? True when the proposed prerequisite already depends
    /// on the topic, directly or through others — which would mean each has to be learned first.
    /// Also true for a self-edge.
    /// </summary>
    public static bool WouldCycle(IEnumerable<TopicEdge> edges, Guid topicId, Guid prerequisiteId)
    {
        if (topicId == prerequisiteId)
            return true;

        // The new edge closes a loop exactly when the topic is already an ancestor of its
        // proposed prerequisite.
        return Ancestors(edges, [prerequisiteId]).Contains(topicId);
    }

    /// <summary>
    /// Everything the given topics rest on, transitively, excluding the seeds themselves.
    ///
    /// This is what lets an assignment's plan reach past what it literally assesses: assessing a
    /// topic means needing everything underneath it too.
    /// </summary>
    public static IReadOnlyList<Guid> Ancestors(IEnumerable<TopicEdge> edges, IEnumerable<Guid> seeds)
    {
        var prerequisitesOf = edges
            .GroupBy(e => e.TopicId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.PrerequisiteId).ToList());

        var seedSet = seeds.ToHashSet();
        var found = new HashSet<Guid>();
        var queue = new Queue<Guid>(seedSet);

        while (queue.Count > 0)
        {
            if (!prerequisitesOf.TryGetValue(queue.Dequeue(), out var parents))
                continue;

            foreach (var parent in parents.Where(p => !seedSet.Contains(p) && found.Add(p)))
                queue.Enqueue(parent);
        }

        return [.. found];
    }

    /// <summary>
    /// Everything that rests on the given topics, transitively, excluding the seeds themselves.
    ///
    /// The mirror of <see cref="Ancestors"/>. Together they give the full neighbourhood of a topic,
    /// which is what isolate mode narrows the map to.
    /// </summary>
    public static IReadOnlyList<Guid> Descendants(IEnumerable<TopicEdge> edges, IEnumerable<Guid> seeds)
    {
        var dependentsOf = edges
            .GroupBy(e => e.PrerequisiteId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.TopicId).ToList());

        var seedSet = seeds.ToHashSet();
        var found = new HashSet<Guid>();
        var queue = new Queue<Guid>(seedSet);

        while (queue.Count > 0)
        {
            if (!dependentsOf.TryGetValue(queue.Dequeue(), out var children))
                continue;

            foreach (var child in children.Where(c => !seedSet.Contains(c) && found.Add(c)))
                queue.Enqueue(child);
        }

        return [.. found];
    }

    /// <summary>
    /// Reconnects around dismissed topics rather than cutting the chain at them.
    ///
    /// Dismissing a topic says "not relevant to me", not "nothing after this matters". If B is
    /// dismissed in A → B → C, then C still rests on A, and dropping B's edges outright would
    /// strand C at the start of the course as though it were foundational.
    /// </summary>
    private static IEnumerable<TopicEdge> Bypass(IEnumerable<TopicEdge> edges, HashSet<Guid> dismissed)
    {
        var all = edges.ToList();
        if (dismissed.Count == 0)
            return all;

        var result = new HashSet<TopicEdge>(all.Where(e =>
            !dismissed.Contains(e.TopicId) && !dismissed.Contains(e.PrerequisiteId)));

        foreach (var gone in dismissed)
        {
            // Everything that reached the dismissed topic now reaches straight through it.
            var above = Ancestors(all, [gone]).Where(a => !dismissed.Contains(a));
            var below = all.Where(e => e.PrerequisiteId == gone && !dismissed.Contains(e.TopicId))
                           .Select(e => e.TopicId);

            foreach (var dependent in below)
                foreach (var prerequisite in above)
                    if (dependent != prerequisite)
                        result.Add(new TopicEdge(dependent, prerequisite));
        }

        return result;
    }

    /// <summary>
    /// Depth of a topic, memoised. <paramref name="visiting"/> breaks cycles: a database that
    /// somehow holds one must still render, so the edge that closes the loop is ignored rather
    /// than throwing and taking the whole map down with it.
    /// </summary>
    private static int StageOf(
        Guid id,
        Dictionary<Guid, List<Guid>> prerequisitesOf,
        Dictionary<Guid, int> memo,
        HashSet<Guid> visiting)
    {
        if (memo.TryGetValue(id, out var known))
            return known;

        if (!visiting.Add(id))
            return 0;

        var stage = prerequisitesOf.TryGetValue(id, out var parents) && parents.Count > 0
            ? parents.Max(p => StageOf(p, prerequisitesOf, memo, visiting)) + 1
            : 0;

        visiting.Remove(id);
        memo[id] = stage;
        return stage;
    }
}
