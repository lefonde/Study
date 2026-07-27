namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Anthropic API configuration. The key comes from configuration only — an environment
/// variable locally (StudyApp__Anthropic__ApiKey) or a platform secret in production
/// (`fly secrets set`). It is deliberately never stored in the database, never written to a
/// file by the app, and never sent to the browser: the Blazor Server process is the only
/// thing that ever holds it.
///
/// (The original plan called for a DPAPI-protected settings file. That was written before the
/// app targeted a Linux container — DPAPI is Windows-only, so configuration/secrets is the
/// portable equivalent.)
/// </summary>
public class AiOptions
{
    public AiOptions(IConfiguration configuration)
    {
        // Prefer the app-scoped key, but honour the conventional ANTHROPIC_API_KEY too so a
        // machine that already has one set just works.
        ApiKey = configuration["StudyApp:Anthropic:ApiKey"]
                 ?? configuration["ANTHROPIC_API_KEY"];
        Model = configuration["StudyApp:Anthropic:Model"] is { Length: > 0 } m
            ? m
            : "claude-opus-5";
    }

    public string? ApiKey { get; }
    public string Model { get; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Last four characters of the key, for confirming *which* key is loaded without exposing it.</summary>
    public string KeyHint => IsConfigured && ApiKey!.Length > 4
        ? $"…{ApiKey[^4..]}"
        : "not set";
}
