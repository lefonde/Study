namespace StudyApp.Core.Entities;

/// <summary>
/// "You have to understand <see cref="PrerequisiteTopicId"/> before <see cref="CourseTopicId"/>
/// makes sense."
///
/// This is the edge set of the course's learning path — what turns a list of topics into an order
/// you can actually follow. It is what lets the map lay topics out in stages, and what lets an
/// assignment's plan include the foundations its assessed topics quietly rest on.
///
/// Composite key with no surrogate id, like <see cref="CardTopic"/>: an edge has no identity
/// beyond the pair it joins, and there is nothing to say about one that isn't said by its ends.
///
/// There is deliberately no "set by the user" flag. Nothing ever removes an edge except the user,
/// so a hand-drawn one already survives every re-map; the only cost is that a re-map may
/// re-propose an edge that was deleted, which is cheap to reject again.
/// </summary>
public class TopicPrerequisite
{
    /// <summary>The dependent topic — the one that needs the other first.</summary>
    public Guid CourseTopicId { get; set; }
    public CourseTopic? Topic { get; set; }

    /// <summary>The topic that must come first.</summary>
    public Guid PrerequisiteTopicId { get; set; }
    public CourseTopic? Prerequisite { get; set; }
}
