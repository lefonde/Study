using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Scheduling;
using StudyApp.Web;
using StudyApp.Web.Components;
using StudyApp.Web.Data;
using StudyApp.Web.Services;
using StudyApp.Web.Services.Ai;

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

// Auth turns on only when a password is configured. Running locally stays friction-free,
// while any deployment that sets StudyApp__Password is protected — which matters far more
// now that an Anthropic API key lives on the server: an open instance doesn't just leak
// flashcards, it lets a stranger spend real money on generation runs.
var authPassword = builder.Configuration["StudyApp:Password"];
var authEnabled = !string.IsNullOrWhiteSpace(authPassword);
if (authEnabled)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
    builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);
    builder.Services.AddCascadingAuthenticationState();
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

var app = builder.Build();

app.UseForwardedHeaders();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

// Recover from a damaged write-ahead log before anything tries to read the database —
// otherwise an intact database behind a bad -wal looks like total corruption.
DatabaseRecovery.EnsureOpenable(
    paths.DatabasePath,
    Path.Combine(paths.DataDirectory, "quarantine"),
    loggerFactory.CreateLogger("DatabaseRecovery"));

// Snapshot the user's data before any migration can touch it.
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

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.UseAntiforgery();

// Assets must stay reachable unauthenticated or the login page itself can't render.
app.MapStaticAssets().AllowAnonymous();

if (authEnabled)
{
    // Login is a plain server-rendered form rather than a Blazor page: a cookie can't be
    // written from inside an established SignalR circuit, so sign-in has to happen on a
    // normal HTTP request/response.
    app.MapGet("/login", (string? error) => Results.Content(LoginPage(error is not null), "text/html"))
        .AllowAnonymous();

    app.MapPost("/login", async (HttpContext http) =>
    {
        var form = await http.Request.ReadFormAsync();
        var supplied = form["password"].ToString();

        // Fixed-time compare so a wrong password can't be narrowed down by timing.
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(authPassword!);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        var ok = expectedBytes.Length == suppliedBytes.Length
                 && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        if (!ok)
            return Results.Redirect("/login?error=1");

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Alex")], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
        return Results.Redirect("/");
    }).AllowAnonymous().DisableAntiforgery();

    app.MapPost("/logout", async (HttpContext http) =>
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }).DisableAntiforgery();
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

// $$ raw string: interpolation is {{ }}, so the CSS's own braces stay literal.
static string LoginPage(bool failed) => $$"""
    <!doctype html>
    <html lang="en"><head>
    <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <title>StudyApp — Sign in</title>
    <style>
      body { font-family: system-ui, sans-serif; display: grid; place-items: center;
             min-height: 100vh; margin: 0; background: #f6f7fb; }
      form { background: #fff; padding: 2rem; border-radius: .75rem; width: min(22rem, 90vw);
             box-shadow: 0 1px 3px rgb(0 0 0 / .12); }
      h1 { font-size: 1.25rem; margin: 0 0 1rem; }
      input { width: 100%; padding: .6rem; font-size: 1rem; border: 1px solid #ccc;
              border-radius: .4rem; box-sizing: border-box; }
      button { width: 100%; margin-top: .75rem; padding: .6rem; font-size: 1rem; border: 0;
               border-radius: .4rem; background: #4f6df5; color: #fff; cursor: pointer; }
      .err { color: #b3261e; font-size: .875rem; margin: 0 0 .75rem; }
    </style></head>
    <body>
      <form method="post" action="/login">
        <h1>📚 StudyApp</h1>
        {{(failed ? "<p class=\"err\">Incorrect password.</p>" : "")}}
        <input type="password" name="password" placeholder="Password" autofocus required
               autocomplete="current-password">
        <button type="submit">Sign in</button>
      </form>
    </body></html>
    """;
