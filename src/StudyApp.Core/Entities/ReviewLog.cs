using StudyApp.Core.Scheduling;

namespace StudyApp.Core.Entities;

/// <summary>Immutable record of a single review. Written from day one; feeds future analytics.</summary>
public class ReviewLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CardId { get; set; }
    public Card? Card { get; set; }
    public DateTime ReviewedAt { get; set; }
    public ReviewGrade Grade { get; set; }
    public double IntervalBeforeDays { get; set; }
    public double IntervalAfterDays { get; set; }

    /// <summary>
    /// This card was graded <i>ahead of its due date</i>, in a scoped drill, and its schedule was
    /// deliberately left alone — so <see cref="IntervalBeforeDays"/> and
    /// <see cref="IntervalAfterDays"/> are equal here by design rather than by coincidence.
    ///
    /// Recorded rather than discarded because the work really happened and should be visible. But
    /// retention statistics must exclude it: retention answers "did you recall it at the moment the
    /// scheduler predicted you were about to forget", and a review you asked for early was never
    /// that test. Counting it would push the figure up every time you drilled something.
    /// </summary>
    public bool IsPractice { get; set; }
}
