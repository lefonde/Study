namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Published per-million-token rates, used to show what a run cost. Estimates only — the
/// authoritative number is on the Anthropic console bill — but enough that spend on the
/// user's own key is never invisible.
/// </summary>
public static class AiPricing
{
    private static readonly Dictionary<string, (decimal Input, decimal Output)> RatesPerMillion =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"] = (5m, 25m),
            ["claude-fable-5"] = (10m, 50m),
            ["claude-sonnet-5"] = (3m, 15m),
            ["claude-haiku-4-5"] = (1m, 5m),
        };

    public static decimal Estimate(string model, long inputTokens, long outputTokens)
    {
        if (!RatesPerMillion.TryGetValue(model, out var rate))
            return 0m;
        return inputTokens / 1_000_000m * rate.Input
             + outputTokens / 1_000_000m * rate.Output;
    }

    public static string Format(decimal usd) => usd is < 0.01m and > 0m ? "<$0.01" : $"${usd:0.00}";

    // Ingestion is a vision pass, so cost tracks pages, and pages are only knowable by opening
    // the file. These factors approximate tokens-per-megabyte across the mix this app sees
    // (scanned assignments, text PDFs, photos of handwriting) and are wrong for any individual
    // file — a 1 MB text PDF holds far more pages than a 1 MB photo.
    private const decimal InputTokensPerMb = 15_000m;
    private const decimal OutputTokensPerMb = 6_000m;

    /// <summary>
    /// A deliberately wide cost range for ingesting <paramref name="totalBytes"/> of material.
    ///
    /// Returned as a range, and shown as one, because a single confident-looking figure derived
    /// from file size would be false precision — and this estimate exists specifically to be
    /// trusted before spending real money in bulk. The recorded per-job cost afterwards is the
    /// number that is actually accurate.
    /// </summary>
    public static (decimal Low, decimal High) EstimateIngestion(string model, long totalBytes)
    {
        var mb = totalBytes / (1024m * 1024m);
        var midpoint = Estimate(model, (long)(mb * InputTokensPerMb), (long)(mb * OutputTokensPerMb));
        return (midpoint * 0.5m, midpoint * 2m);
    }

    public static string FormatRange((decimal Low, decimal High) range) =>
        range.High < 0.01m ? "<$0.01" : $"{Format(range.Low)}–{Format(range.High)}";
}
