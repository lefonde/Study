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
}
