namespace StudyApp.Core.Entities;

/// <summary>
/// One day's <c>ProgressReport</c> for a deck, persisted so trend charts don't require
/// replaying every <see cref="ReviewLog"/> row on each page load. <see cref="DeckId"/> is
/// null-free here — course/unit trend is the sum of the day's deck rows, computed on read,
/// not stored separately.
/// </summary>
public class ProgressSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeckId { get; set; }
    public Deck? Deck { get; set; }
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }

    /// <summary>Date only (local study day the capture ran on) — one row per deck per day.</summary>
    public DateOnly CapturedOn { get; set; }

    public int Total { get; set; }
    public int Unseen { get; set; }
    public int Learning { get; set; }
    public int Young { get; set; }
    public int Mature { get; set; }
    public double Mastery { get; set; }
    public double RecallNow { get; set; }
    public double? Retention { get; set; }
}
