using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Scheduling;
using StudyApp.Web;
using StudyApp.Web.Components;
using StudyApp.Web.Data;
using StudyApp.Web.Services;

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

var app = builder.Build();

app.UseForwardedHeaders();

// Snapshot the user's data before any migration can touch it.
DatabaseBackup.Run(paths.DatabasePath, paths.BackupDirectory);
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

app.UseAntiforgery();

app.MapStaticAssets();

// Platform health probe: confirms the process is up *and* its data volume is reachable.
app.MapGet("/healthz", (StudyAppPaths p) =>
    Directory.Exists(p.DataDirectory) ? Results.Ok("healthy") : Results.StatusCode(503));

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
