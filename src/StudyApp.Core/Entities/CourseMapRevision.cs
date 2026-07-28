namespace StudyApp.Core.Entities;

public enum RevisionStatus
{
    Pending = 0,
    Applied = 1,
    Discarded = 2,
}

public enum TopicChangeKind
{
    /// <summary>A topic the map didn't have.</summary>
    Add = 0,
    /// <summary>An existing topic whose importance the new evidence changes.</summary>
    Reweight = 1,
    /// <summary>Two topics that turned out to be the same concept under different names.</summary>
    Merge = 2,
    /// <summary>A topic no longer supported by any material — proposed for dismissal, never deleted.</summary>
    Retire = 3,
}

public enum ProposalStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

/// <summary>
/// One mapping run's proposed changes to a course's topics, held for review.
///
/// Mirrors <see cref="CardSuggestion"/>: nothing the model produces alters the course until the
/// user approves it. That matters most exactly when it's least convenient — uploading a past
/// exam months in can legitimately reshuffle what the course is "about", and a silent reshuffle
/// is indistinguishable from the model quietly getting it wrong.
/// </summary>
public class CourseMapRevision : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }

    /// <summary>The job that produced this, for cost and failure history.</summary>
    public Guid? JobId { get; set; }

    public RevisionStatus Status { get; set; } = RevisionStatus.Pending;

    /// <summary>The model's one-line account of what changed and why.</summary>
    public string Summary { get; set; } = "";

    public DateTime? AppliedAt { get; set; }

    public List<TopicProposal> Proposals { get; set; } = [];

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>A single proposed change within a <see cref="CourseMapRevision"/>.</summary>
public class TopicProposal : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    public CourseMapRevision? Revision { get; set; }

    /// <summary>The topic being changed. Null for <see cref="TopicChangeKind.Add"/>.</summary>
    public Guid? CourseTopicId { get; set; }
    public CourseTopic? Topic { get; set; }

    public TopicChangeKind Kind { get; set; }

    public string ProposedName { get; set; } = "";
    public string ProposedSummary { get; set; } = "";
    public TopicImportance ProposedImportance { get; set; }
    public Guid? ProposedUnitId { get; set; }

    /// <summary>Why — the evidence, so a proposal can be judged rather than merely accepted.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Survivor of a <see cref="TopicChangeKind.Merge"/>; the proposal's topic folds into it.</summary>
    public Guid? MergeIntoTopicId { get; set; }

    /// <summary>
    /// Where the model found this topic. Owned JSON rather than rows: it is written once with the
    /// proposal, read once when applying, and never queried on its own — unlike
    /// <see cref="TopicSource"/>, which it becomes.
    /// </summary>
    public List<ProposedSource> Sources { get; set; } = [];

    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>A <see cref="TopicSource"/> before its proposal is accepted.</summary>
public class ProposedSource
{
    public Guid MaterialId { get; set; }
    public string? SourceReference { get; set; }
    public TopicMention Mention { get; set; }
}
