namespace StudyApp.Core.Scheduling;

/// <summary>
/// Which slice of the collection a review session covers.
///
/// One kind per hierarchy the app already holds, rather than a growing parameter list on the
/// session builder: cards sit in decks, decks and cards sit in the chapter/lesson tree, cards link
/// to topics, and topics sit at a stage of the learning path. All four are legitimate ways to ask
/// "practise this and not everything", and they resolve to a set of cards by different routes.
/// </summary>
public enum ReviewScopeKind
{
    /// <summary>Every card the user owns, across every course — the daily session.</summary>
    Everything = 0,
    Course = 1,
    Deck = 2,
    /// <summary>A chapter or lesson, <b>including everything beneath it</b>.</summary>
    Unit = 3,
    /// <summary>One <c>CourseTopic</c>, reached through the <c>CardTopic</c> links.</summary>
    Topic = 4,
    /// <summary>Every topic at one rung of the learning path.</summary>
    Stage = 5,
}

/// <summary>
/// A scope, as the URL expresses it. <paramref name="Id"/> is the course, deck, unit or topic;
/// <see cref="ReviewScopeKind.Stage"/> is the one kind that needs two values, since a stage index
/// only means anything relative to a course.
///
/// A stage index is <i>derived</i>, not an identity — adding one prerequisite can renumber every
/// stage after it. That is fine for a link resolved fresh on each visit, which is what this is,
/// but it is the reason a stage is never stored anywhere.
/// </summary>
public record ReviewScope(ReviewScopeKind Kind, Guid? Id = null, Guid? CourseId = null, int Stage = 0)
{
    public static ReviewScope Everything { get; } = new(ReviewScopeKind.Everything);

    public static ReviewScope ForCourse(Guid courseId) =>
        new(ReviewScopeKind.Course, courseId, courseId);

    public static ReviewScope ForDeck(Guid deckId) => new(ReviewScopeKind.Deck, deckId);

    public static ReviewScope ForUnit(Guid unitId) => new(ReviewScopeKind.Unit, unitId);

    public static ReviewScope ForTopic(Guid topicId) => new(ReviewScopeKind.Topic, topicId);

    public static ReviewScope ForStage(Guid courseId, int stage) =>
        new(ReviewScopeKind.Stage, null, courseId, stage);

    /// <summary>
    /// Whether this scope is narrow enough that most of its cards will not be due on any given
    /// day, so offering to practise past the schedule is worth the screen space.
    /// </summary>
    public bool IsNarrow => Kind is ReviewScopeKind.Unit or ReviewScopeKind.Topic or ReviewScopeKind.Stage;
}
