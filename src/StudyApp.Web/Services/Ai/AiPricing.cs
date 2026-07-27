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
}
