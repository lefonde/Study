namespace StudyApp.Core.Entities;

/// <summary>
/// Links a card to a topic it helps you learn.
///
/// Many-to-many on purpose: "what is the difference between a mutex and a semaphore?" genuinely
/// serves both topics, and forcing a single owner would make one of them look uncovered while a
/// perfectly good card for it already exists.
///
/// This is what turns mastery — which only ever measured the cards you happen to have — into a
/// statement about the course itself. Without it a three-card chapter reads 100% while being
/// almost entirely unlearned.
/// </summary>
public class CardTopic
{
    public Guid CardId { get; set; }
    public Card? Card { get; set; }

    public Guid CourseTopicId { get; set; }
    public CourseTopic? Topic { get; set; }
}
