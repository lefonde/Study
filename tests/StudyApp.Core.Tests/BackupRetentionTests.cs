using StudyApp.Core.Maintenance;

namespace StudyApp.Core.Tests;

public class BackupRetentionTests
{
    private static BackupEntry At(string stamp) =>
        new($"studyapp-{stamp}.db", DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", null));

    private static List<string> Deleted(IEnumerable<BackupEntry> entries, int keepRecent = 10, int keepDays = 7) =>
        BackupRetention.SelectForDeletion(entries, keepRecent, keepDays).Select(e => e.Path).ToList();

    [Fact]
    public void Nothing_Is_Deleted_When_Under_The_Recent_Limit()
    {
        var entries = Enumerable.Range(1, 5).Select(i => At($"2026072{i}-120000")).ToList();
        Assert.Empty(Deleted(entries));
    }

    [Fact]
    public void Empty_Input_Is_Handled()
    {
        Assert.Empty(BackupRetention.SelectForDeletion([]));
    }

    [Fact]
    public void Newest_Are_Always_Kept()
    {
        // 15 snapshots on one day; keepRecent=3 so only the newest 3 qualify on recency.
        var entries = Enumerable.Range(1, 15).Select(i => At($"20260728-{i:00}0000")).ToList();
        var deleted = Deleted(entries, keepRecent: 3, keepDays: 7);

        Assert.DoesNotContain("studyapp-20260728-150000.db", deleted);
        Assert.DoesNotContain("studyapp-20260728-140000.db", deleted);
        Assert.DoesNotContain("studyapp-20260728-130000.db", deleted);
    }

    /// <summary>
    /// The rule that exists because of a real incident: a day that starts healthy and ends
    /// damaged must not have its healthy copy pruned in favour of the damaged one.
    /// </summary>
    [Fact]
    public void First_And_Last_Of_Each_Day_Survive()
    {
        var entries = Enumerable.Range(1, 15).Select(i => At($"20260728-{i:00}0000")).ToList();
        var deleted = Deleted(entries, keepRecent: 3, keepDays: 7);

        Assert.DoesNotContain("studyapp-20260728-010000.db", deleted);  // the day as it began
        Assert.DoesNotContain("studyapp-20260728-150000.db", deleted);  // and as it ended
        Assert.Contains("studyapp-20260728-080000.db", deleted);        // the middle is expendable
    }

    [Fact]
    public void Each_Day_Within_The_Window_Keeps_A_Representative()
    {
        // One snapshot per day across 12 days, keepRecent=2 — so only the daily rule protects
        // days 3..12 counting back from the newest.
        var entries = Enumerable.Range(1, 12).Select(i => At($"202607{i + 10:00}-120000")).ToList();
        var deleted = Deleted(entries, keepRecent: 2, keepDays: 7).ToHashSet();

        // Newest 7 distinct days are kept (a single snapshot is both that day's first and last).
        foreach (var day in Enumerable.Range(16, 7))
            Assert.DoesNotContain($"studyapp-202607{day}-120000.db", deleted);

        // Everything older than the retention window goes.
        Assert.Contains("studyapp-20260711-120000.db", deleted);
        Assert.Contains("studyapp-20260715-120000.db", deleted);
    }

    [Fact]
    public void Days_Beyond_The_Window_Are_Not_Protected_By_The_Daily_Rule()
    {
        var entries = Enumerable.Range(1, 20).Select(i => At($"202607{i:00}-120000")).ToList();
        var deleted = Deleted(entries, keepRecent: 1, keepDays: 3).ToHashSet();

        Assert.DoesNotContain("studyapp-20260720-120000.db", deleted);
        Assert.DoesNotContain("studyapp-20260719-120000.db", deleted);
        Assert.DoesNotContain("studyapp-20260718-120000.db", deleted);
        Assert.Contains("studyapp-20260717-120000.db", deleted);
    }

    [Fact]
    public void Unordered_Input_Is_Handled()
    {
        // Directory enumeration order is not guaranteed, so the policy must sort for itself.
        var entries = new List<BackupEntry>
        {
            At("20260728-080000"), At("20260728-150000"), At("20260728-010000"),
            At("20260728-120000"), At("20260728-030000"),
        };
        var deleted = Deleted(entries, keepRecent: 1, keepDays: 7);

        Assert.DoesNotContain("studyapp-20260728-150000.db", deleted);
        Assert.DoesNotContain("studyapp-20260728-010000.db", deleted);
        Assert.Equal(3, deleted.Count);   // 03:00, 08:00 and 12:00 — the middle of the day
    }
}
