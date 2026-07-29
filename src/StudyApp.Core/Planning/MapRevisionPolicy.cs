using StudyApp.Core.Entities;

namespace StudyApp.Core.Planning;

/// <summary>A proposal and, if it may not be applied, why not.</summary>
public record ProposalVerdict(TopicProposal Proposal, string? BlockedReason)
{
    public bool IsApplicable => BlockedReason is null;
}

/// <summary>
/// Decides which proposed map changes may actually be applied.
///
/// The prompt already avoids generating most of these, but that is a request, not a guarantee —
/// a model asked to leave pinned topics alone will occasionally propose changing one anyway.
/// This is the enforcement, and it is deliberately pure and tested: it protects judgements the
/// user made by hand, which are the most expensive thing in the map to lose and the only part
/// nothing else can reconstruct.
/// </summary>
public static class MapRevisionPolicy
{
    public static ProposalVerdict Evaluate(
        TopicProposal proposal, IReadOnlyDictionary<Guid, CourseTopic> topicsById) =>
        new(proposal, BlockedReason(proposal, topicsById));

    public static IReadOnlyList<ProposalVerdict> EvaluateAll(
        IEnumerable<TopicProposal> proposals, IEnumerable<CourseTopic> topics)
    {
        var byId = topics.ToDictionary(t => t.Id);
        return proposals.Select(p => Evaluate(p, byId)).ToList();
    }

    private static string? BlockedReason(
        TopicProposal proposal, IReadOnlyDictionary<Guid, CourseTopic> topicsById)
    {
        if (proposal.Kind == TopicChangeKind.Add)
            return AddBlockedReason(proposal, topicsById);

        if (proposal.CourseTopicId is not { } targetId)
            return "Change proposed without saying which topic it applies to.";

        if (!topicsById.TryGetValue(targetId, out var target))
            return "The topic this changes no longer exists.";

        // Dismissed is a standing decision, not a one-off. Letting a later run revive a topic the
        // user removed would make dismissing it pointless — they would have to do it after every
        // upload, forever.
        if (target.IsDismissed)
            return "You dismissed this topic.";

        return proposal.Kind switch
        {
            TopicChangeKind.Reweight when target.ImportancePinned =>
                "You pinned this topic's importance.",
            TopicChangeKind.Retire when target.ImportancePinned =>
                "You pinned this topic's importance.",
            TopicChangeKind.Merge => MergeBlockedReason(proposal, targetId, topicsById),
            // Relink is deliberately NOT blocked by a pin. Pinning fixes how much a topic matters;
            // it says nothing about what the topic builds on, and a pinned topic can perfectly
            // well gain a prerequisite the map only just learned about.
            TopicChangeKind.Relink => null,
            _ => null,
        };
    }

    private static string? AddBlockedReason(
        TopicProposal proposal, IReadOnlyDictionary<Guid, CourseTopic> topicsById)
    {
        if (string.IsNullOrWhiteSpace(proposal.ProposedName))
            return "Proposed topic has no name.";

        // A re-run that re-proposes an existing topic as new is the failure mode that would
        // fragment the map into near-duplicates over time, so it is refused outright rather than
        // creating a second row the user then has to merge by hand.
        var clash = topicsById.Values.FirstOrDefault(t =>
            !t.IsDeleted &&
            string.Equals(t.Name.Trim(), proposal.ProposedName.Trim(), StringComparison.OrdinalIgnoreCase));

        return clash is null ? null : $"“{clash.Name}” is already on the map.";
    }

    private static string? MergeBlockedReason(
        TopicProposal proposal, Guid targetId, IReadOnlyDictionary<Guid, CourseTopic> topicsById)
    {
        if (proposal.MergeIntoTopicId is not { } into)
            return "Merge proposed without a topic to merge into.";
        if (into == targetId)
            return "Cannot merge a topic into itself.";
        if (!topicsById.TryGetValue(into, out var survivor))
            return "The topic this merges into no longer exists.";
        if (survivor.IsDismissed)
            return "Cannot merge into a topic you dismissed.";
        return null;
    }
}
