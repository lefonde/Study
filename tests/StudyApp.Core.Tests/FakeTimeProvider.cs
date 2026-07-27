namespace StudyApp.Core.Tests;

/// <summary>Deterministic TimeProvider for tests: fixed now, fixed custom time zone.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    public FakeTimeProvider(DateTimeOffset utcNow, TimeSpan? localOffset = null)
    {
        UtcNow = utcNow;
        LocalTimeZone = localOffset is { } offset
            ? TimeZoneInfo.CreateCustomTimeZone("Test", offset, "Test", "Test")
            : TimeZoneInfo.Utc;
    }

    public DateTimeOffset UtcNow { get; set; }
    public override TimeZoneInfo LocalTimeZone { get; }
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
