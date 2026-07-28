using System.Security.Cryptography;
using System.Text;

namespace StudyApp.Web.Services;

/// <summary>
/// Authentication configuration, read once at startup. Mirrors <see cref="Ai.AiOptions"/>:
/// every capability is off unless its configuration exists, so a local run stays
/// friction-free while a deployment turns on exactly what it sets.
///
/// Sign-in methods (any combination):
///   StudyApp__PasswordHash   — preferred: PBKDF2 hash from `dotnet run -- hash-password`
///   StudyApp__Password       — plaintext, dev convenience only
///   StudyApp__Auth__Google__ClientId / __ClientSecret
///   StudyApp__Auth__GitHub__ClientId / __ClientSecret
///   StudyApp__Auth__AllowedIdentities — emails / usernames, comma separated
/// </summary>
public class AuthOptions
{
    // OWASP's current PBKDF2-HMAC-SHA256 guidance. Stored in the hash string itself, so
    // raising it later doesn't invalidate existing hashes.
    public const int DefaultIterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly string? _password;
    private readonly string? _passwordHash;

    public AuthOptions(IConfiguration configuration)
    {
        _password = configuration["StudyApp:Password"];
        _passwordHash = configuration["StudyApp:PasswordHash"];

        GoogleClientId = configuration["StudyApp:Auth:Google:ClientId"];
        GoogleClientSecret = configuration["StudyApp:Auth:Google:ClientSecret"];
        GitHubClientId = configuration["StudyApp:Auth:GitHub:ClientId"];
        GitHubClientSecret = configuration["StudyApp:Auth:GitHub:ClientSecret"];

        AllowedIdentities = (configuration["StudyApp:Auth:AllowedIdentities"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Validate();
    }

    public string? GoogleClientId { get; }
    public string? GoogleClientSecret { get; }
    public string? GitHubClientId { get; }
    public string? GitHubClientSecret { get; }
    public IReadOnlySet<string> AllowedIdentities { get; }

    public bool HasPassword => !string.IsNullOrWhiteSpace(_password)
                               || !string.IsNullOrWhiteSpace(_passwordHash);
    public bool GoogleEnabled => !string.IsNullOrWhiteSpace(GoogleClientId)
                                 && !string.IsNullOrWhiteSpace(GoogleClientSecret);
    public bool GitHubEnabled => !string.IsNullOrWhiteSpace(GitHubClientId)
                                 && !string.IsNullOrWhiteSpace(GitHubClientSecret);

    public bool AnyExternalProvider => GoogleEnabled || GitHubEnabled;

    /// <summary>Auth is active as soon as any sign-in method is configured.</summary>
    public bool Enabled => HasPassword || AnyExternalProvider;

    /// <summary>
    /// "Sign in with Google" proves who someone is, not that they may enter — without an
    /// allowlist, every Google account on earth is a valid credential for this app. Refusing
    /// to start is the only safe response to that combination.
    /// </summary>
    private void Validate()
    {
        if (AnyExternalProvider && AllowedIdentities.Count == 0)
        {
            throw new InvalidOperationException(
                "An external sign-in provider is configured but StudyApp__Auth__AllowedIdentities " +
                "is empty. Without it ANY Google/GitHub account could sign in. Set it to your own " +
                "email address (Google) or GitHub username, comma separated for several.");
        }

        if (!string.IsNullOrWhiteSpace(_passwordHash) && !_passwordHash.StartsWith("pbkdf2.v1.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "StudyApp__PasswordHash is not in the expected format. Generate it with: " +
                "dotnet run --project src/StudyApp.Web -- hash-password \"your password\"");
        }
    }

    /// <summary>Verifies a submitted password against the hash, or the plaintext fallback.</summary>
    public bool VerifyPassword(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied))
            return false;

        if (!string.IsNullOrWhiteSpace(_passwordHash))
            return VerifyHash(supplied, _passwordHash);

        if (!string.IsNullOrWhiteSpace(_password))
            return FixedTimeEquals(supplied, _password);

        return false;
    }

    /// <summary>Case-insensitive allowlist check against the identity an OAuth provider returned.</summary>
    public bool IsIdentityAllowed(params string?[] candidates) =>
        candidates.Any(c => !string.IsNullOrWhiteSpace(c) && AllowedIdentities.Contains(c!));

    public static string HashPassword(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2.v1.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyHash(string supplied, string stored)
    {
        // pbkdf2.v1.{iterations}.{salt}.{hash}
        var parts = stored.Split('.');
        if (parts.Length != 5 || !int.TryParse(parts[2], out var iterations))
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(supplied), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Constant-time string compare, so a wrong password can't be narrowed by timing.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
