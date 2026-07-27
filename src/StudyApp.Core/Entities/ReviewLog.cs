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
}
