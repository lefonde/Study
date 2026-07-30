using StudyApp.Core.Entities;

namespace StudyApp.Core.Planning;

/// <summary>
/// Decides where each topic sits inside its stage, so the drawn path can actually be followed.
///
/// Stage assignment alone is not a layout. Ordering a stage by importance — which is what this
/// replaced — puts a topic wherever the alphabet happens to leave it, entirely unrelated to where
/// its prerequisites are, so the connectors tangle no matter how much room the page has. On a real
/// course of ~40 topics the result was unreadable.
///
/// This is the ordering and coordinate half of the standard layered-graph method
/// (Sugiyama): reduce crossings by repeatedly reordering each layer toward the average position of
/// its neighbours, then choose rows so that connected topics line up horizontally.
///
/// Two properties the renderer depends on:
/// <list type="bullet">
/// <item>a <b>row means the same vertical band in every stage</b>, which is what turns a link
/// between neighbours into a short horizontal line instead of a long diagonal;</item>
/// <item>an edge spanning more than one stage <b>reserves its row</b> in the stages it crosses, so
/// it threads through empty space rather than disappearing behind the cards in between.</item>
/// </list>
/// </summary>
internal static class LayeredLayout
{
    /// <summary>
    /// Enough passes to settle a course-sized graph without making a page load noticeable. The
    /// best-scoring pass wins, so extra iterations can never make the result worse.
    /// </summary>
    private const int OrderingSweeps = 8;

    private const int AlignmentSweeps = 4;

    /// <summary>A topic, or a placeholder holding a lane open for an edge passing through.</summary>
    private sealed class Slot(int layer, CourseTopic? topic)
    {
        public int Layer { get; } = layer;
        public CourseTopic? Topic { get; } = topic;
        public bool IsReal => Topic is not null;

        /// <summary>For a placeholder, the edge it is holding a lane open for.</summary>
        public TopicEdge? Lane { get; init; }

        /// <summary>Position within the layer. What crossing reduction rearranges.</summary>
        public int Order { get; set; }

        /// <summary>Grid row. Shared across stages, so equal rows are horizontally aligned.</summary>
        public int Row { get; set; }

        public List<Slot> Up { get; } = [];
        public List<Slot> Down { get; } = [];
    }

    internal record Result(IReadOnlyDictionary<Guid, int> RowByTopic, int RowCount)
    {
        /// <summary>Topics in the order they should be rendered within each stage.</summary>
        public required IReadOnlyDictionary<int, IReadOnlyList<CourseTopic>> TopicsByStage { get; init; }

        public required IReadOnlyList<EdgeLane> Lanes { get; init; }
    }

    public static Result Arrange(
        IReadOnlyDictionary<Guid, CourseTopic> visible,
        IReadOnlyList<TopicEdge> edges,
        IReadOnlyDictionary<Guid, int> stageByTopic)
    {
        var layers = BuildSlots(visible, edges, stageByTopic, out var slotByTopic);
        if (layers.Count == 0)
            return new Result(new Dictionary<Guid, int>(), 0)
            {
                TopicsByStage = new Dictionary<int, IReadOnlyList<CourseTopic>>(),
                Lanes = [],
            };

        ReduceCrossings(layers);
        AssignRows(layers);

        var rowByTopic = slotByTopic.ToDictionary(kv => kv.Key, kv => kv.Value.Row);
        var rowCount = layers.SelectMany(l => l).Select(s => s.Row).DefaultIfEmpty(-1).Max() + 1;

        var topicsByStage = layers
            .Select((layer, index) => (index, topics: (IReadOnlyList<CourseTopic>)
                [.. layer.Where(s => s.IsReal).OrderBy(s => s.Row).Select(s => s.Topic!)]))
            .ToDictionary(x => x.index, x => x.topics);

        var lanes = layers.SelectMany(l => l)
            .Where(s => s.Lane is not null)
            .Select(s => new EdgeLane(s.Lane!.TopicId, s.Lane.PrerequisiteId, s.Layer, s.Row))
            .OrderBy(l => l.Stage)
            .ToList();

        return new Result(rowByTopic, rowCount)
        {
            TopicsByStage = topicsByStage,
            Lanes = lanes,
        };
    }

    /// <summary>
    /// One slot per topic, plus a chain of placeholders for every edge that skips a stage.
    ///
    /// The placeholders are the reason a long edge stays visible: they take up a row in each stage
    /// the edge crosses, so nothing else is laid out there.
    /// </summary>
    private static List<List<Slot>> BuildSlots(
        IReadOnlyDictionary<Guid, CourseTopic> visible,
        IReadOnlyList<TopicEdge> edges,
        IReadOnlyDictionary<Guid, int> stageByTopic,
        out Dictionary<Guid, Slot> slotByTopic)
    {
        var layerCount = stageByTopic.Values.DefaultIfEmpty(-1).Max() + 1;
        var layers = Enumerable.Range(0, Math.Max(0, layerCount)).Select(_ => new List<Slot>()).ToList();

        slotByTopic = [];
        if (layerCount <= 0)
            return layers;

        // Initial order is the old presentation ordering — Core first, then alphabetical. It is now
        // only a starting point and a tie-break, but keeping it means the result is deterministic
        // and, where the topology says nothing, still reads sensibly.
        foreach (var topic in visible.Values
            .OrderByDescending(t => t.Importance)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var layer = stageByTopic[topic.Id];
            var slot = new Slot(layer, topic);
            slotByTopic[topic.Id] = slot;
            layers[layer].Add(slot);
        }

        foreach (var edge in edges)
        {
            if (!slotByTopic.TryGetValue(edge.TopicId, out var dependent)
                || !slotByTopic.TryGetValue(edge.PrerequisiteId, out var prerequisite))
                continue;

            // A cycle that survived into the data can leave an edge pointing sideways or backwards.
            // Link it directly rather than trying to thread it through stages it doesn't cross.
            if (dependent.Layer - prerequisite.Layer <= 1)
            {
                Link(prerequisite, dependent);
                continue;
            }

            var previous = prerequisite;
            for (var layer = prerequisite.Layer + 1; layer < dependent.Layer; layer++)
            {
                var placeholder = new Slot(layer, null) { Lane = edge };
                layers[layer].Add(placeholder);
                Link(previous, placeholder);
                previous = placeholder;
            }
            Link(previous, dependent);
        }

        foreach (var layer in layers)
            for (var i = 0; i < layer.Count; i++)
                layer[i].Order = i;

        return layers;
    }

    private static void Link(Slot prerequisite, Slot dependent)
    {
        prerequisite.Down.Add(dependent);
        dependent.Up.Add(prerequisite);
    }

    /// <summary>
    /// The median heuristic: sweep forward putting each layer in the average order of the layer
    /// before it, then backward against the layer after, keeping whichever pass measured fewest
    /// crossings. Nodes with no neighbour in the reference layer hold their position.
    /// </summary>
    private static void ReduceCrossings(List<List<Slot>> layers)
    {
        var best = Snapshot(layers);
        var bestCrossings = CountCrossings(layers);

        for (var sweep = 0; sweep < OrderingSweeps && bestCrossings > 0; sweep++)
        {
            for (var layer = 1; layer < layers.Count; layer++)
                Reorder(layers[layer], s => s.Up);

            Consider(layers, ref best, ref bestCrossings);

            for (var layer = layers.Count - 2; layer >= 0; layer--)
                Reorder(layers[layer], s => s.Down);

            Consider(layers, ref best, ref bestCrossings);
        }

        Restore(layers, best);
    }

    private static void Consider(
        List<List<Slot>> layers, ref Dictionary<Slot, int> best, ref int bestCrossings)
    {
        var crossings = CountCrossings(layers);
        if (crossings >= bestCrossings)
            return;

        bestCrossings = crossings;
        best = Snapshot(layers);
    }

    private static Dictionary<Slot, int> Snapshot(List<List<Slot>> layers) =>
        layers.SelectMany(l => l).ToDictionary(s => s, s => s.Order);

    private static void Restore(List<List<Slot>> layers, Dictionary<Slot, int> orders)
    {
        foreach (var layer in layers)
        {
            foreach (var slot in layer)
                slot.Order = orders[slot];
            layer.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }

    /// <summary>
    /// Sorts one layer by the median position of each node's neighbours in the reference direction.
    /// A stable sort keyed on the current position for nodes without neighbours is what stops the
    /// rest of the layer from being shuffled arbitrarily.
    /// </summary>
    private static void Reorder(List<Slot> layer, Func<Slot, List<Slot>> neighbours)
    {
        var keys = new Dictionary<Slot, double>(layer.Count);
        foreach (var slot in layer)
        {
            var positions = neighbours(slot).Select(n => (double)n.Order).OrderBy(x => x).ToList();
            keys[slot] = positions.Count switch
            {
                0 => slot.Order,
                _ => positions.Count % 2 == 1
                    ? positions[positions.Count / 2]
                    : (positions[positions.Count / 2 - 1] + positions[positions.Count / 2]) / 2.0,
            };
        }

        // OrderBy is a stable sort, so equal medians keep their existing relative order — which
        // makes the whole pass deterministic.
        var sorted = layer.OrderBy(s => keys[s]).ToList();
        layer.Clear();
        layer.AddRange(sorted);

        for (var i = 0; i < layer.Count; i++)
            layer[i].Order = i;
    }

    /// <summary>
    /// Crossings between every adjacent pair of layers. Two edges cross when their endpoints are
    /// in the opposite order on the two sides.
    /// </summary>
    private static int CountCrossings(List<List<Slot>> layers)
    {
        var total = 0;
        for (var i = 0; i + 1 < layers.Count; i++)
        {
            var pairs = layers[i]
                .SelectMany(u => u.Down.Select(v => (Left: u.Order, Right: v.Order)))
                .ToList();

            for (var a = 0; a < pairs.Count; a++)
                for (var b = a + 1; b < pairs.Count; b++)
                {
                    var (l1, r1) = pairs[a];
                    var (l2, r2) = pairs[b];
                    if ((l1 - l2) * (r1 - r2) < 0)
                        total++;
                }
        }
        return total;
    }

    /// <summary>
    /// Turns positions into rows. Rows are global: row 4 is the same band in every stage, which is
    /// what makes a link between neighbouring stages come out horizontal.
    ///
    /// Alignment is <b>budgeted</b>: no row index may exceed the number of slots in the busiest
    /// stage, so the map can never be taller than its fullest column. Within that budget each node
    /// moves toward the rows of its neighbours, clamped so it can neither overtake nor displace the
    /// nodes beside it.
    ///
    /// Two earlier attempts are worth recording, because both looked reasonable and both made the
    /// picture worse. Nudging nodes with a push-down repair inflated a graph whose busiest stage
    /// held 13 slots to 21 rows. Sliding each stage as a whole traded that for a staircase — every
    /// stage offset a little further than the last, which spread 39 topics over 20 bands and left
    /// the first stage's cards below the fold. Capping the budget is what makes the trade explicit:
    /// alignment is taken only where it is free.
    /// </summary>
    private static void AssignRows(List<List<Slot>> layers)
    {
        foreach (var layer in layers)
            foreach (var slot in layer)
                slot.Row = slot.Order;

        var budget = layers.Max(l => l.Count);

        // Forward then backward, because pulling a stage toward the one on its left can pull it
        // away from the one on its right. A few passes settle it.
        for (var sweep = 0; sweep < AlignmentSweeps; sweep++)
        {
            for (var i = 1; i < layers.Count; i++)
                Align(layers[i], budget, s => s.Up);

            for (var i = layers.Count - 2; i >= 0; i--)
                Align(layers[i], budget, s => s.Down);
        }

        Compact(layers);
    }

    /// <summary>
    /// Moves each node toward the median row of its neighbours, within the gap its own neighbours in
    /// the stage leave it. The bounds are what make this non-inflating: a node can never push
    /// another one out of the way, and the last node cannot run past the budget.
    /// </summary>
    private static void Align(List<Slot> layer, int budget, Func<Slot, List<Slot>> neighbours)
    {
        var ordered = layer.OrderBy(s => s.Order).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var slot = ordered[i];
            var rows = neighbours(slot).Select(n => n.Row).OrderBy(x => x).ToList();
            if (rows.Count == 0)
                continue;

            var lower = Math.Max(i, i == 0 ? 0 : ordered[i - 1].Row + 1);
            // Leave room for everything still to be placed below this node, or the stage could not
            // keep its rows distinct inside the budget.
            var upper = Math.Min(
                i == ordered.Count - 1 ? budget - 1 : ordered[i + 1].Row - 1,
                budget - 1 - (ordered.Count - 1 - i));

            if (upper < lower)
                continue;

            slot.Row = Math.Clamp(rows[rows.Count / 2], lower, upper);
        }
    }

    /// <summary>
    /// Normalises rows to start at zero and removes any band no stage uses. Safe because dropping a
    /// globally empty band shifts every stage equally, so the alignment survives; without it the
    /// sliding leaves gaps that render as dead vertical space.
    /// </summary>
    private static void Compact(List<List<Slot>> layers)
    {
        var used = layers.SelectMany(l => l).Select(s => s.Row).Distinct().Order().ToList();
        var mapped = used.Select((row, index) => (row, index)).ToDictionary(x => x.row, x => x.index);

        foreach (var slot in layers.SelectMany(l => l))
            slot.Row = mapped[slot.Row];
    }
}
