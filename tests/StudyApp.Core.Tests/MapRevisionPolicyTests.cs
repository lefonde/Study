using StudyApp.Core.Entities;
using StudyApp.Core.Planning;

namespace StudyApp.Core.Tests;

public class MapRevisionPolicyTests
{
    private static CourseTopic Topic(
        string name = "Deadlock", bool pinned = false, bool dismissed = false,
        TopicImportance importance = TopicImportance.Supporting) =>
        new()
        {
            Name = name,
            ImportancePinned = pinned,
            IsDismissed = dismissed,
            Importance = importance,
        };

    private static TopicProposal Proposal(
        TopicChangeKind kind, CourseTopic? target = null, string name = "",
        Guid? mergeInto = null) =>
        new()
        {
            Kind = kind,
            CourseTopicId = target?.Id,
            ProposedName = name,
            MergeIntoTopicId = mergeInto,
        };

    private static string? Reason(TopicProposal proposal, params CourseTopic[] topics) =>
        MapRevisionPolicy.EvaluateAll([proposal], topics).Single().BlockedReason;

    // --- the protections that exist for the user's benefit ---

    [Fact]
    public void Pinned_Importance_Cannot_Be_Reweighted()
    {
        var pinned = Topic(pinned: true);
        Assert.Equal("You pinned this topic's importance.",
            Reason(Proposal(TopicChangeKind.Reweight, pinned), pinned));
    }

    [Fact]
    public void Pinned_Topic_Cannot_Be_Retired()
    {
        var pinned = Topic(pinned: true);
        Assert.NotNull(Reason(Proposal(TopicChangeKind.Retire, pinned), pinned));
    }

    /// <summary>
    /// Dismissing must be permanent, or the user would have to redo it after every upload.
    /// </summary>
    [Fact]
    public void Dismissed_Topics_Are_Not_Resurrected()
    {
        var dismissed = Topic(dismissed: true);
        Assert.Equal("You dismissed this topic.",
            Reason(Proposal(TopicChangeKind.Reweight, dismissed), dismissed));
    }

    [Fact]
    public void Unpinned_Topics_Can_Be_Reweighted()
    {
        var topic = Topic();
        Assert.Null(Reason(Proposal(TopicChangeKind.Reweight, topic), topic));
    }

    // --- keeping the map from fragmenting ---

    [Fact]
    public void Adding_A_Topic_That_Already_Exists_Is_Refused()
    {
        var existing = Topic("Deadlock");
        var reason = Reason(Proposal(TopicChangeKind.Add, name: "  deadlock  "), existing);
        Assert.Contains("already on the map", reason);
    }

    [Fact]
    public void Adding_A_Genuinely_New_Topic_Is_Allowed()
    {
        var existing = Topic("Deadlock");
        Assert.Null(Reason(Proposal(TopicChangeKind.Add, name: "Semaphores"), existing));
    }

    [Fact]
    public void Nameless_Additions_Are_Refused()
    {
        Assert.Equal("Proposed topic has no name.", Reason(Proposal(TopicChangeKind.Add, name: "   ")));
    }

    // --- malformed proposals, which a model will occasionally produce ---

    [Fact]
    public void Change_Without_A_Target_Is_Refused()
    {
        Assert.Contains("which topic", Reason(Proposal(TopicChangeKind.Reweight)));
    }

    [Fact]
    public void Change_Targeting_An_Unknown_Topic_Is_Refused()
    {
        // Target exists as an object but was never handed to the policy — i.e. deleted meanwhile.
        Assert.Equal("The topic this changes no longer exists.",
            Reason(Proposal(TopicChangeKind.Reweight, Topic())));
    }

    [Fact]
    public void Merge_Requires_A_Survivor()
    {
        var topic = Topic();
        Assert.Contains("without a topic to merge into",
            Reason(Proposal(TopicChangeKind.Merge, topic), topic));
    }

    [Fact]
    public void Merge_Into_Self_Is_Refused()
    {
        var topic = Topic();
        Assert.Equal("Cannot merge a topic into itself.",
            Reason(Proposal(TopicChangeKind.Merge, topic, mergeInto: topic.Id), topic));
    }

    [Fact]
    public void Merge_Into_A_Dismissed_Topic_Is_Refused()
    {
        var source = Topic("Mutex");
        var dismissed = Topic("Locks", dismissed: true);
        Assert.Equal("Cannot merge into a topic you dismissed.",
            Reason(Proposal(TopicChangeKind.Merge, source, mergeInto: dismissed.Id), source, dismissed));
    }

    [Fact]
    public void A_Valid_Merge_Is_Allowed()
    {
        var source = Topic("Mutex");
        var survivor = Topic("Locks");
        Assert.Null(Reason(Proposal(TopicChangeKind.Merge, source, mergeInto: survivor.Id), source, survivor));
    }

    [Fact]
    public void Applicable_And_Blocked_Are_Reported_Together()
    {
        var pinned = Topic("Deadlock", pinned: true);
        var open = Topic("Scheduling");

        var verdicts = MapRevisionPolicy.EvaluateAll(
            [Proposal(TopicChangeKind.Reweight, pinned), Proposal(TopicChangeKind.Reweight, open)],
            [pinned, open]);

        // A blocked proposal must not suppress the rest of the revision.
        Assert.False(verdicts[0].IsApplicable);
        Assert.True(verdicts[1].IsApplicable);
    }
}
