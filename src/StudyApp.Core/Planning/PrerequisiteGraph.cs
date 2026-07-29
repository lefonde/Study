using StudyApp.Core.Entities;

namespace StudyApp.Core.Planning;

/// <summary>One dependency: <paramref name="TopicId"/> needs <paramref name="PrerequisiteId"/> first.</summary>
public record TopicEdge(Guid TopicId, Guid PrerequisiteId);

/// <summary>One rung of the learning path. <paramref name="Index"/> is zero-based.</summary>
public record TopicStage(int Index, IReadOnlyList<CourseTopic> Topics);

/// <summary>
/// The course laid out as an order you can follow, plus the edges that produced it.
/// <paramref name="Edges"/> is the visible edge set — dismissed topics have already been spliced
/// out — so a renderer can draw exactly what it is given without re-deriving anything.
/// </summary>
public record ProgressionMap(
    IReadOnlyList<TopicStage> Stages,
    IReadOnlyList<TopicEdge> Edges,
    IReadOnlyDictionary<Guid, int> StageByTopic)
{
    public static ProgressionMap Empty { get; } = new([], [], new Dictionary<Guid, int>());

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

        var stages = stageByTopic
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => new TopicStage(
                g.Key,
                [.. g.Select(kv => visible[kv.Key])
                     .OrderByDescending(t => t.Importance)
                     .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)]))
            .ToList();

        return new ProgressionMap(stages, live, stageByTopic);
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
