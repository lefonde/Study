using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Scheduling;
using StudyApp.Web;
using StudyApp.Web.Components;
using StudyApp.Web.Data;
using StudyApp.Web.Services;
using StudyApp.Web.Services.Ai;

// Utility mode: `dotnet run --project src/StudyApp.Web -- hash-password "pw"` prints a value
// for StudyApp__PasswordHash so the plaintext secret never has to be stored anywhere.
if (args is ["hash-password", var plaintext, ..])
{
    Console.WriteLine(AuthOptions.HashPassword(plaintext));
    return;
}

// 10 sign-in attempts per 5 minutes per IP: generous for a human, useless for a brute force.
var AuthWindow = new FixedWindowRateLimiterOptions
{
    PermitLimit = 10,
    Window = TimeSpan.FromMinutes(5),
    QueueLimit = 0,
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// InputFile uploads (e.g. course PDFs) travel over the SignalR circuit; the default
// 32 KB message cap is far too small. Matches MaterialsUpload.MaxFileSizeBytes.
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 64 * 1024 * 1024);

// Hosted behind a TLS-terminating proxy (Fly/Railway/Azure), so the original scheme and
// host arrive as X-Forwarded-* headers. Without this the app builds redirects and absolute
// URLs as http, which breaks the Blazor circuit handshake over https.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The proxy is the platform's own edge, not a user-supplied hop.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var paths = new StudyAppPaths(builder.Configuration);
paths.EnsureCreated();
builder.Services.AddSingleton(paths);

// Auth turns on as soon as any sign-in method is configured. Running locally stays
// friction-free, while any deployment that sets one is protected — which matters far more
// now that an Anthropic API key lives on the server: an open instance doesn't just leak
// flashcards, it lets a stranger spend real money on generation runs.
// Constructing this validates the configuration and throws on a dangerous combination
// (an OAuth provider with no allowlist), so a misconfigured deployment fails to start
// rather than coming up wide open.
var auth = new AuthOptions(builder.Configuration);
builder.Services.AddSingleton(auth);
var authEnabled = auth.Enabled;

// Registered unconditionally: NavMenu's <AuthorizeView> needs the cascading
// Task<AuthenticationState> to exist even when no sign-in method is configured, or every
// page throws. With auth off it simply resolves to an anonymous user and renders nothing.
builder.Services.AddCascadingAuthenticationState();

if (authEnabled)
{
    // Keys must outlive the container or every redeploy invalidates all auth cookies and
    // silently signs the user out. The data directory is the mounted volume in production.
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDirectory, "keys")))
        .SetApplicationName("StudyApp");

    var authBuilder = builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "StudyApp.Auth";
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Secure flag whenever the request arrived over https. Not Always, because that
            // would break plain-http local runs; UseForwardedHeaders means production sees
            // the real scheme through the proxy.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

    if (auth.GoogleEnabled)
    {
        authBuilder.AddGoogle(options =>
        {
            options.ClientId = auth.GoogleClientId!;
            options.ClientSecret = auth.GoogleClientSecret!;
            options.Events.OnTicketReceived = ctx => EnforceAllowlist(ctx, "google");
        });
    }

    if (auth.GitHubEnabled)
    {
        authBuilder.AddGitHub(options =>
        {
            options.ClientId = auth.GitHubClientId!;
            options.ClientSecret = auth.GitHubClientSecret!;
            // Without this GitHub returns no email, so an allowlist written as an email
            // address could never match — only the login handle would.
            options.Scope.Add("user:email");
            options.Events.OnTicketReceived = ctx => EnforceAllowlist(ctx, "github");
        });
    }

    builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);

    // Brute force protection. Without this a password is only as strong as the attacker's
    // bandwidth. Partitioned by client IP, which is meaningful because UseForwardedHeaders
    // runs before the limiter and restores the real caller behind the platform proxy.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (ctx, token) =>
        {
            ctx.HttpContext.Response.Headers.RetryAfter = "300";
            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Auth")
                .LogWarning("Sign-in rate limit hit from {Ip}", ctx.HttpContext.Connection.RemoteIpAddress);
            await ctx.HttpContext.Response.WriteAsync("Too many sign-in attempts. Try again shortly.", token);
        };
        options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => AuthWindow));

        // The password form posts to a Razor component, not a minimal-API endpoint, so
        // RequireRateLimiting can't be attached to it. A global limiter that only partitions
        // sign-in submissions covers it while leaving every other request unthrottled.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/login")
                ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => AuthWindow)
                : RateLimitPartition.GetNoLimiter<string>("unthrottled"));
    });
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TimestampInterceptor>();
builder.Services.AddDbContextFactory<StudyDbContext>((sp, options) => options
    .UseSqlite($"Data Source={paths.DatabasePath}")
    .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

builder.Services.AddSingleton<IScheduler, Sm2Scheduler>();
builder.Services.AddSingleton<DuePolicy>();
builder.Services.AddScoped<CourseService>();
builder.Services.AddScoped<DeckService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddSingleton<MaterialFileStore>();
builder.Services.AddScoped<CourseUnitService>();
builder.Services.AddScoped<MaterialService>();
builder.Services.AddSingleton<ProgressPolicy>();
builder.Services.AddScoped<ProgressService>();
builder.Services.AddScoped<ProgressSnapshotService>();

// AI pipeline. The runner is a singleton BackgroundService; the per-job services are scoped
// because they use the DbContext factory and are resolved per job.
builder.Services.AddSingleton<AiOptions>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddScoped<ClaudeService>();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddScoped<CardGenerationService>();
builder.Services.AddScoped<AiJobService>();
builder.Services.AddScoped<SuggestionService>();
builder.Services.AddHostedService<JobRunner>();

// The startup backup below only protects what existed at boot; this covers the session itself.
builder.Services.AddHostedService<PeriodicBackupService>();

var app = builder.Build();

app.UseForwardedHeaders();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

// Recover from a damaged write-ahead log before anything tries to read the database —
// otherwise an intact database behind a bad -wal looks like total corruption.
DatabaseRecovery.EnsureOpenable(
    paths.DatabasePath,
    Path.Combine(paths.DataDirectory, "quarantine"),
    loggerFactory.CreateLogger("DatabaseRecovery"));

// Snapshot the user's data before any migration can touch it. PeriodicBackupService then keeps
// snapshotting while the app runs, so a long session isn't left protected only by this one.
DatabaseBackup.Run(
    paths.DatabasePath,
    paths.BackupDirectory,
    loggerFactory.CreateLogger("DatabaseBackup"));
using (var scope = app.Services.CreateScope())
{
    using var db = scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<StudyDbContext>>()
        .CreateDbContext();
    db.Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// No UseHttpsRedirection: the app only ever serves plain http — locally on :5170, and in
// production behind a proxy that terminates TLS and already forces https (fly.toml's
// force_https). Redirecting here would either loop or just log "failed to determine the
// https port" on every start.

// Defence-in-depth headers on every response. CSP is the meaningful one: it caps the damage
// any injected markup could do, complementing MarkdownRenderer's raw-HTML ban.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        // Blazor's boot script and ImportMap are inline; 'unsafe-inline' is the pragmatic
        // choice over a nonce that the framework's own injected tags wouldn't carry.
        "script-src 'self' 'unsafe-inline'; " +
        // KaTeX positions glyphs with inline style attributes and cannot work without this.
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; " +
        // wss: is the Blazor Server circuit.
        "connect-src 'self' ws: wss:; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    await next();
});

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
}

app.UseAntiforgery();

// Assets must stay reachable unauthenticated or the login page itself can't render.
app.MapStaticAssets().AllowAnonymous();

if (authEnabled)
{
    // Starts the OAuth dance. Anonymous (you're not signed in yet) and rate limited so the
    // provider round-trip can't be used as an amplifier.
    app.MapGet("/login/challenge/{provider}", (string provider, string? returnUrl) =>
    {
        var scheme = provider.ToLowerInvariant() switch
        {
            "google" when auth.GoogleEnabled => "Google",
            "github" when auth.GitHubEnabled => "GitHub",
            _ => null,
        };
        if (scheme is null)
            return Results.Redirect("/login?error=unknownprovider");

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = LocalRedirect(returnUrl) },
            [scheme]);
    }).AllowAnonymous().RequireRateLimiting("auth");

    app.MapPost("/logout", async (HttpContext http) =>
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    });
}

// Platform health probe: confirms the process is up *and* its data volume is reachable.
// Anonymous by necessity — the platform's checker has no cookie.
app.MapGet("/healthz", (StudyAppPaths p) =>
    Directory.Exists(p.DataDirectory) ? Results.Ok("healthy") : Results.StatusCode(503))
    .AllowAnonymous();

// Types safe to render inline in the browser — none of them can carry executable script
// same-origin (notably excludes text/html and image/svg+xml, both common XSS vectors).
// MimeType is client-supplied at upload time, so this allowlist is the real security
// boundary, not just a UX nicety: anything else is forced to download instead.
var inlineSafeMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "application/pdf", "image/png", "image/jpeg", "image/gif", "image/webp", "text/plain",
};

// Uploaded material files live outside wwwroot (under LocalApplicationData), so they need
// an explicit streaming endpoint rather than static-file serving.
app.MapGet("/materials/{id:guid}/file", async (
    Guid id, HttpContext httpContext, IDbContextFactory<StudyDbContext> dbFactory, MaterialFileStore store) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var material = await db.Materials.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
    if (material is null)
        return Results.NotFound();

    var path = store.GetFullPath(material.FilePath);
    if (!File.Exists(path))
        return Results.NotFound();

    httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (inlineSafeMimeTypes.Contains(material.MimeType))
        return Results.File(path, material.MimeType, enableRangeProcessing: true);

    var downloadName = $"{material.Title}{Path.GetExtension(material.FilePath)}";
    return Results.File(path, "application/octet-stream", downloadName, enableRangeProcessing: true);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Rejects an authenticated-but-unauthorized identity. A provider only proves *who* someone
/// is; without this check any Google or GitHub account in the world would be a valid
/// credential for this app.
/// </summary>
static Task EnforceAllowlist(
    Microsoft.AspNetCore.Authentication.TicketReceivedContext context, string provider)
{
    var options = context.HttpContext.RequestServices.GetRequiredService<AuthOptions>();
    var logger = context.HttpContext.RequestServices
        .GetRequiredService<ILoggerFactory>().CreateLogger("Auth");

    var email = context.Principal?.FindFirstValue(ClaimTypes.Email);
    var name = context.Principal?.FindFirstValue(ClaimTypes.Name);
    // GitHub surfaces the login handle here; Google has no equivalent, hence checking several.
    var login = context.Principal?.FindFirstValue("urn:github:login");

    if (options.IsIdentityAllowed(email, login, name))
    {
        logger.LogInformation("Signed in via {Provider} as {Identity}", provider, email ?? login ?? name);
        return Task.CompletedTask;
    }

    logger.LogWarning(
        "Rejected {Provider} sign-in for {Identity} — not in StudyApp__Auth__AllowedIdentities",
        provider, email ?? login ?? name ?? "(unknown)");

    context.Fail("This account is not allowed to sign in.");
    context.Response.Redirect("/login?error=notallowed");
    context.HandleResponse();
    return Task.CompletedTask;
}

/// <summary>
/// Open-redirect guard: only ever send the browser to a path on this site. A returnUrl of
/// "https://evil.example" would otherwise turn our sign-in into a credible phishing hop.
/// </summary>
/// <summary>Sign-in attempts are throttled per caller IP (accurate thanks to UseForwardedHeaders).</summary>
static string ClientKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static string LocalRedirect(string? returnUrl) =>
    !string.IsNullOrEmpty(returnUrl)
    && returnUrl.StartsWith('/')
    && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        ? returnUrl
        : "/";
