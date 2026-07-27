using StudyApp.Core.Entities;

namespace StudyApp.Core.Scheduling;

/// <summary>
/// Applies a review grade to a card's scheduling state.
/// The seam that lets FSRS (or any other algorithm) replace SM-2 without touching the UI.
/// </summary>
public interface IScheduler
{
    void Apply(Card card, ReviewGrade grade, DateTime nowUtc);
}
