using StudyApp.Core.Entities;

namespace StudyApp.Core.Scheduling;

/// <summary>
/// Classic SM-2 with Anki-style tweaks:
///   - graduation: first Good = 1 day, second success = 6 days, then interval × ease
///   - Hard  = interval × 1.2 (min +1 day), ease −0.15
///   - Easy  = interval × ease × 1.3, ease +0.15
///   - Again = lapse: back to learning, due immediately, ease −0.20 (only when lapsing from Review)
///   - ease floor 1.3, interval capped at 365 days, rounded to whole days
/// Learning-stage Again/Hard repeat within the session (due = now) and do not touch ease.
/// </summary>
public class Sm2Scheduler : IScheduler
{
    public const double MinEase = 1.3;
    public const int MaxIntervalDays = 365;
    public const int EasyGraduationDays = 4;

    public void Apply(Card card, ReviewGrade grade, DateTime nowUtc)
    {
        if (grade == ReviewGrade.Again)
        {
            if (card.State == CardState.Review)
            {
                card.Lapses++;
                card.EaseFactor = Math.Max(MinEase, card.EaseFactor - 0.20);
            }
            card.State = CardState.Learning;
            card.Repetitions = 0;
            card.IntervalDays = 0;
            card.Due = nowUtc;
            return;
        }

        if (card.State is CardState.New or CardState.Learning)
        {
            if (grade == ReviewGrade.Hard)
            {
                // Not confident yet — repeat within the session, no ease penalty while learning.
                card.State = CardState.Learning;
                card.IntervalDays = 0;
                card.Due = nowUtc;
                return;
            }

            card.Repetitions++;
            if (grade == ReviewGrade.Easy)
            {
                card.EaseFactor += 0.15;
                Schedule(card, EasyGraduationDays, nowUtc);
            }
            else
            {
                Schedule(card, card.Repetitions >= 2 ? 6 : 1, nowUtc);
            }
            return;
        }

        // Successful recall of a Review-state card.
        card.Repetitions++;
        var interval = grade switch
        {
            ReviewGrade.Hard => Math.Max(card.IntervalDays * 1.2, card.IntervalDays + 1),
            ReviewGrade.Good => card.Repetitions == 2 ? 6 : card.IntervalDays * card.EaseFactor,
            _ => card.IntervalDays * card.EaseFactor * 1.3,
        };
        card.EaseFactor = grade switch
        {
            ReviewGrade.Hard => Math.Max(MinEase, card.EaseFactor - 0.15),
            ReviewGrade.Easy => card.EaseFactor + 0.15,
            _ => card.EaseFactor,
        };
        Schedule(card, interval, nowUtc);
    }

    private static void Schedule(Card card, double intervalDays, DateTime nowUtc)
    {
        var days = (int)Math.Clamp(
            Math.Round(intervalDays, MidpointRounding.AwayFromZero), 1, MaxIntervalDays);
        card.State = CardState.Review;
        card.IntervalDays = days;
        card.Due = nowUtc.AddDays(days);
    }
}
