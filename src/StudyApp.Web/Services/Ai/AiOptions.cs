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
        if (configuration["StudyApp:Anthropic:ApiKey"] is { Length: > 0 } configured)
            (ApiKey, KeySource) = (configured, "configuration");
        else if (configuration["ANTHROPIC_API_KEY"] is { Length: > 0 } conventional)
            (ApiKey, KeySource) = (conventional, "ANTHROPIC_API_KEY");
        else if (ReadWindowsUserScope("StudyApp__Anthropic__ApiKey") is { Length: > 0 } stored)
            (ApiKey, KeySource) = (stored, "Windows user environment");
        else if (ReadWindowsUserScope("ANTHROPIC_API_KEY") is { Length: > 0 } storedAlt)
            (ApiKey, KeySource) = (storedAlt, "Windows user environment");

        Model = configuration["StudyApp:Anthropic:Model"] is { Length: > 0 } m
            ? m
            : "claude-opus-5";
    }

    /// <summary>
    /// Reads the persisted user-level variable straight out of the registry rather than the
    /// process's inherited environment block.
    ///
    /// This exists because `setx` only reaches processes started *after* it runs: a terminal
    /// (or IDE) opened beforehand keeps its stale copy, so relaunching the app from there
    /// still can't see a key that is demonstrably set. Reading the live user environment
    /// makes `setx` behave the way people reasonably expect. Windows-only by nature — on
    /// Linux deployments the configuration path above is the real one.
    /// </summary>
    private static string? ReadWindowsUserScope(string name) =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null;

    public string? ApiKey { get; }
    public string Model { get; }
    /// <summary>Where the key was found — shown in Settings so a wrong/stale key is diagnosable.</summary>
    public string KeySource { get; } = "none";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Last four characters of the key, for confirming *which* key is loaded without exposing it.</summary>
    public string KeyHint => IsConfigured && ApiKey!.Length > 4
        ? $"…{ApiKey[^4..]}"
        : "not set";
}
