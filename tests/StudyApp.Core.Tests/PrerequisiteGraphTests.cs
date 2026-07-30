using StudyApp.Core.Entities;
using StudyApp.Core.Planning;

namespace StudyApp.Core.Tests;

public class PrerequisiteGraphTests
{
    /// <summary>Named topics with stable ids, so a test can read like the graph it describes.</summary>
    private sealed class Graph
    {
        private readonly Dictionary<string, CourseTopic> _topics = [];
        private readonly List<TopicEdge> _edges = [];

        public CourseTopic Add(
            string name,
            TopicImportance importance = TopicImportance.Supporting,
            bool dismissed = false)
        {
            var topic = new CourseTopic { Name = name, Importance = importance, IsDismissed = dismissed };
            _topics[name] = topic;
            return topic;
        }

        /// <summary>"<paramref name="name"/> needs all of <paramref name="prerequisites"/> first."</summary>
        public Graph Needs(string name, params string[] prerequisites)
        {
            foreach (var p in prerequisites)
                _edges.Add(new TopicEdge(_topics[name].Id, _topics[p].Id));
            return this;
        }

        public Guid Id(string name) => _topics[name].Id;
        public IReadOnlyList<TopicEdge> Edges => _edges;
        public ProgressionMap Build() => PrerequisiteGraph.Build(_topics.Values, _edges);

        public int StageOf(string name) => Build().StageByTopic[Id(name)];

        public IReadOnlyList<string> NamesInStage(int index) =>
            [.. Build().Stages.Single(s => s.Index == index).Topics.Select(t => t.Name)];
    }

    private static Graph TenTopicCourse()
    {
        var g = new Graph();
        foreach (var n in Enumerable.Range(1, 10))
            g.Add($"T{n}");

        // The shape from the request: 1 and 2 feed 3; 4 and 5 feed 6; the last leans on the lot.
        g.Needs("T3", "T1", "T2")
         .Needs("T6", "T4", "T5")
         .Needs("T7", "T3", "T6")
         .Needs("T10", "T7", "T8", "T9");
        return g;
    }

    [Fact]
    public void Empty_Course_Builds_An_Empty_Map()
    {
        Assert.Equal(ProgressionMap.Empty, PrerequisiteGraph.Build([], []));
    }

    [Fact]
    public void Topics_With_No_Prerequisites_All_Start_At_Stage_Zero()
    {
        var g = new Graph();
        g.Add("A");
        g.Add("B");
        g.Add("C");

        var map = g.Build();

        Assert.Single(map.Stages);
        Assert.Equal(3, map.Stages[0].Topics.Count);
        Assert.False(map.HasAnyEdges);
    }

    [Fact]
    public void A_Topic_Sits_One_Stage_After_Everything_It_Needs()
    {
        var g = TenTopicCourse();

        Assert.Equal(0, g.StageOf("T1"));
        Assert.Equal(0, g.StageOf("T2"));
        Assert.Equal(1, g.StageOf("T3"));
        Assert.Equal(1, g.StageOf("T6"));
        Assert.Equal(2, g.StageOf("T7"));
        Assert.Equal(3, g.StageOf("T10"));

        // T8 and T9 depend on nothing, so they are available from the start even though the
        // topic that needs them is last.
        Assert.Equal(0, g.StageOf("T8"));
        Assert.Equal(0, g.StageOf("T9"));
    }

    /// <summary>
    /// The reason layering takes the longest path and not the shortest: T10 needs T7 (stage 2) and
    /// T8 (stage 0). Placing it after the nearer one would put it before something it depends on.
    /// </summary>
    [Fact]
    public void Depth_Follows_The_Deepest_Prerequisite_Not_The_Nearest()
    {
        Assert.Equal(3, TenTopicCourse().StageOf("T10"));
    }

    [Fact]
    public void Stages_Are_Ordered_By_Importance_Then_Name()
    {
        var g = new Graph();
        g.Add("zebra", TopicImportance.Core);
        g.Add("apple", TopicImportance.Peripheral);
        g.Add("mango", TopicImportance.Core);

        Assert.Equal(["mango", "zebra", "apple"], g.NamesInStage(0));
    }

    [Fact]
    public void Dismissed_Topics_Leave_The_Map()
    {
        var g = new Graph();
        g.Add("A");
        g.Add("B", dismissed: true);
        g.Needs("B", "A");

        var map = g.Build();

        Assert.Single(map.Stages[0].Topics);
        Assert.Equal("A", map.Stages[0].Topics[0].Name);
        Assert.False(map.HasAnyEdges);
    }

    /// <summary>
    /// Dismissing the middle of a chain must not strand what came after it. In A → B → C with B
    /// dismissed, C still rests on A — severing at B would show C as foundational.
    /// </summary>
    [Fact]
    public void A_Dismissed_Topic_Is_Bypassed_Not_Severed()
    {
        var g = new Graph();
        g.Add("A");
        g.Add("B", dismissed: true);
        g.Add("C");
        g.Needs("B", "A").Needs("C", "B");

        var map = g.Build();

        Assert.Equal(0, map.StageByTopic[g.Id("A")]);
        Assert.Equal(1, map.StageByTopic[g.Id("C")]);
        Assert.Equal([new TopicEdge(g.Id("C"), g.Id("A"))], map.Edges);
    }

    [Fact]
    public void Ancestors_Are_Transitive_And_Exclude_The_Seeds()
    {
        var g = TenTopicCourse();

        var ancestors = PrerequisiteGraph.Ancestors(g.Edges, [g.Id("T7")]);

        Assert.Equal(
            new HashSet<Guid> { g.Id("T3"), g.Id("T6"), g.Id("T1"), g.Id("T2"), g.Id("T4"), g.Id("T5") },
            ancestors.ToHashSet());
        Assert.DoesNotContain(g.Id("T7"), ancestors);
    }

    [Fact]
    public void Ancestors_Of_A_Foundational_Topic_Is_Empty()
    {
        var g = TenTopicCourse();
        Assert.Empty(PrerequisiteGraph.Ancestors(g.Edges, [g.Id("T1")]));
    }

    [Fact]
    public void WouldCycle_Rejects_A_Self_Edge()
    {
        var g = TenTopicCourse();
        Assert.True(PrerequisiteGraph.WouldCycle(g.Edges, g.Id("T1"), g.Id("T1")));
    }

    [Fact]
    public void WouldCycle_Rejects_An_Edge_That_Closes_A_Loop_Indirectly()
    {
        var g = TenTopicCourse();

        // T7 already depends on T3 → T1, so making T1 depend on T7 would close the loop.
        Assert.True(PrerequisiteGraph.WouldCycle(g.Edges, g.Id("T1"), g.Id("T7")));
    }

    [Fact]
    public void WouldCycle_Allows_An_Edge_In_The_Existing_Direction()
    {
        var g = TenTopicCourse();
        Assert.False(PrerequisiteGraph.WouldCycle(g.Edges, g.Id("T10"), g.Id("T1")));
        Assert.False(PrerequisiteGraph.WouldCycle(g.Edges, g.Id("T8"), g.Id("T9")));
    }

    /// <summary>
    /// A cycle should never reach the database — WouldCycle guards every write — but if one ever
    /// does, the map must still render rather than hang or throw.
    /// </summary>
    [Fact]
    public void A_Cycle_In_Stored_Data_Does_Not_Break_The_Map()
    {
        var g = new Graph();
        g.Add("A");
        g.Add("B");
        g.Add("C");
        g.Needs("B", "A").Needs("C", "B").Needs("A", "C");

        var map = g.Build();

        Assert.Equal(3, map.StageByTopic.Count);
        AssertStagesAreContiguous(map);
    }

    /// <summary>
    /// Stage indices must run 0, 1, 2… with no holes, whatever the input — a renderer labelling
    /// "Stage 1, Stage 3" looks broken. A cycle is the only thing that can produce a gap, so this
    /// is really a guard on that path.
    /// </summary>
    private static void AssertStagesAreContiguous(ProgressionMap map)
    {
        Assert.Equal(
            [.. Enumerable.Range(0, map.Stages.Count)],
            [.. map.Stages.Select(s => s.Index)]);
        Assert.All(map.StageByTopic.Values, stage => Assert.InRange(stage, 0, map.Stages.Count - 1));
    }

    [Fact]
    public void Stage_Indices_Are_Contiguous_For_A_Normal_Course()
    {
        AssertStagesAreContiguous(TenTopicCourse().Build());
    }

    // --- layout: ordering within a stage, and rows shared across stages ---

    /// <summary>
    /// The case the ordering work exists for. Under the old importance-then-name ordering these
    /// three links crossed, because a stage's vertical order had nothing to do with its edges.
    /// </summary>
    [Fact]
    public void Stages_Are_Ordered_To_Avoid_Crossings()
    {
        var g = new Graph();
        // Names chosen so alphabetical order is the exact opposite of what the edges want.
        g.Add("a-first");
        g.Add("b-second");
        g.Add("c-third");
        g.Add("x-needs-c");
        g.Add("y-needs-b");
        g.Add("z-needs-a");
        g.Needs("x-needs-c", "c-third")
         .Needs("y-needs-b", "b-second")
         .Needs("z-needs-a", "a-first");

        Assert.Equal(0, PrerequisiteGraph.CountCrossings(g.Build()));
    }

    /// <summary>
    /// Measures the new layout against the one it replaced, on the shape from the original request.
    /// The old ordering was importance-then-name with the row being just the position in that list,
    /// so reproducing it here is a fair before/after rather than an assertion about nothing.
    /// </summary>
    [Fact]
    public void Layout_Crosses_No_More_Than_The_Importance_Ordering_It_Replaced()
    {
        var g = TenTopicCourse();
        var map = g.Build();

        var oldRows = map.Stages
            .SelectMany(s => s.Topics
                .OrderByDescending(t => t.Importance)
                .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select((t, index) => (t.Id, Row: index)))
            .ToDictionary(x => x.Id, x => x.Row);

        var before = PrerequisiteGraph.CountCrossings(map with { RowByTopic = oldRows });
        var after = PrerequisiteGraph.CountCrossings(map);

        Assert.True(after <= before, $"Layout made crossings worse: {before} -> {after}.");
    }

    [Fact]
    public void A_Chain_Is_Laid_Out_On_One_Row()
    {
        var g = new Graph();
        g.Add("A");
        g.Add("B");
        g.Add("C");
        g.Needs("B", "A").Needs("C", "B");

        var map = g.Build();

        // Same row in all three stages means the two links are drawn as horizontal lines.
        Assert.Equal(map.RowByTopic[g.Id("A")], map.RowByTopic[g.Id("B")]);
        Assert.Equal(map.RowByTopic[g.Id("B")], map.RowByTopic[g.Id("C")]);
    }

    /// <summary>
    /// A link that skips a stage must not be drawn behind the cards in the stage it crosses, so the
    /// layout holds its row open there.
    /// </summary>
    [Fact]
    public void An_Edge_Spanning_A_Stage_Keeps_Its_Row_Clear()
    {
        var g = new Graph();
        g.Add("start");
        g.Add("middle-one");
        g.Add("middle-two");
        g.Add("end");
        // start -> end skips stage 1 entirely; the two middles live there.
        g.Needs("middle-one", "start").Needs("middle-two", "start").Needs("end", "middle-one");
        g.Needs("end", "start");

        var map = g.Build();

        var lane = Assert.Single(map.Lanes);
        Assert.Equal(1, lane.Stage);
        Assert.Equal(g.Id("start"), lane.PrerequisiteId);
        Assert.Equal(g.Id("end"), lane.TopicId);

        // The reserved band holds no card in the stage the link crosses, which is the whole point:
        // the line threads through it instead of vanishing behind a topic.
        var occupied = map.Stages[1].Topics.Select(t => map.RowByTopic[t.Id]).ToList();
        Assert.DoesNotContain(lane.Row, occupied);
    }

    [Fact]
    public void A_Single_Hop_Edge_Needs_No_Lane()
    {
        var g = new Graph();
        g.Add("A");
        g.Add("B");
        g.Needs("B", "A");

        Assert.Empty(g.Build().Lanes);
    }

    [Fact]
    public void No_Lane_Ever_Sits_On_A_Card()
    {
        var map = TenTopicCourse().Build();

        foreach (var lane in map.Lanes)
        {
            var occupied = map.Stages[lane.Stage].Topics.Select(t => map.RowByTopic[t.Id]);
            Assert.DoesNotContain(lane.Row, occupied);
        }
    }

    [Fact]
    public void Rows_Are_Compact_And_Zero_Based()
    {
        var map = TenTopicCourse().Build();

        var rows = map.RowByTopic.Values.Distinct().Order().ToList();
        Assert.Equal(0, rows[0]);
        Assert.True(map.RowCount > rows[^1], "RowCount must cover every assigned row.");
    }

    /// <summary>
    /// The layout runs on every page render, so two builds of the same course must not produce two
    /// different pictures — a graph that reshuffles under you is worse than a tangled one.
    /// </summary>
    [Fact]
    public void Layout_Is_Deterministic()
    {
        static string Describe(ProgressionMap map) => string.Join(
            "|",
            map.RowByTopic.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

        var first = TenTopicCourse().Build();
        var second = TenTopicCourse().Build();

        // Distinct Graph instances mint distinct ids, so compare the shape rather than the ids.
        Assert.Equal(
            string.Join("|", first.Stages.Select(s => string.Join(",", s.Topics.Select(t => t.Name)))),
            string.Join("|", second.Stages.Select(s => string.Join(",", s.Topics.Select(t => t.Name)))));

        var again = TenTopicCourse();
        Assert.Equal(Describe(again.Build()), Describe(again.Build()));
    }

    [Fact]
    public void Every_Visible_Topic_Gets_A_Row()
    {
        var g = TenTopicCourse();
        g.Add("dismissed-one", dismissed: true);

        var map = g.Build();

        Assert.Equal(10, map.RowByTopic.Count);
        Assert.DoesNotContain(g.Id("dismissed-one"), map.RowByTopic.Keys);
    }

    [Fact]
    public void Descendants_Mirror_Ancestors()
    {
        var g = TenTopicCourse();

        var below = PrerequisiteGraph.Descendants(g.Edges, [g.Id("T1")]);

        // T1 feeds T3, which feeds T7, which feeds T10.
        Assert.Equal(
            new HashSet<Guid> { g.Id("T3"), g.Id("T7"), g.Id("T10") },
            below.ToHashSet());
        Assert.Empty(PrerequisiteGraph.Descendants(g.Edges, [g.Id("T10")]));
    }

    [Fact]
    public void Map_Reports_What_A_Topic_Needs_And_What_It_Unlocks()
    {
        var g = TenTopicCourse();
        var map = g.Build();

        Assert.Equal(
            new HashSet<Guid> { g.Id("T1"), g.Id("T2") },
            map.PrerequisitesOf(g.Id("T3")).ToHashSet());
        Assert.Equal([g.Id("T7")], map.UnlockedBy(g.Id("T3")));
        Assert.Empty(map.UnlockedBy(g.Id("T10")));
    }

    /// <summary>
    /// Every caller with topics in hand needs the same projection off <c>Prerequisites</c>, and the
    /// direction matters: the row's own topic is the dependent one, the other end comes first.
    /// </summary>
    [Fact]
    public void Edges_Are_Read_Off_The_Topics_In_The_Right_Direction()
    {
        var first = new CourseTopic { Name = "Semaphores" };
        var second = new CourseTopic { Name = "Bounded buffer" };
        second.Prerequisites.Add(new TopicPrerequisite
        {
            CourseTopicId = second.Id,
            PrerequisiteTopicId = first.Id,
        });

        var edges = PrerequisiteGraph.EdgesOf([first, second]);

        Assert.Equal([new TopicEdge(second.Id, first.Id)], edges);
        Assert.Equal(1, PrerequisiteGraph.Build([first, second], edges).StageByTopic[second.Id]);
    }

    [Fact]
    public void Topics_With_No_Prerequisites_Produce_No_Edges()
    {
        Assert.Empty(PrerequisiteGraph.EdgesOf([new CourseTopic { Name = "T1" }]));
    }
}
