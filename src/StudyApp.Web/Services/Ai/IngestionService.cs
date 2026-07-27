using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Stage 1 of the pipeline: turns an uploaded file into its reusable <see cref="MaterialExtract"/>.
/// This is the only place an original PDF or scan is ever read by the model.
/// </summary>
public class IngestionService(
    IDbContextFactory<StudyDbContext> factory,
    MaterialFileStore fileStore,
    ClaudeService claude)
{
    public async Task RunAsync(GenerationJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var material = await db.Materials.FirstOrDefaultAsync(m => m.Id == job.MaterialId, ct)
            ?? throw new InvalidOperationException("The material was deleted before ingestion ran.");

        var path = fileStore.GetFullPath(material.FilePath);
        if (!File.Exists(path))
            throw new InvalidOperationException("The uploaded file is missing from storage.");

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var result = await claude.ExtractAsync(bytes, material.MimeType, material.Kind.ToString(), ct);

        // Re-ingesting replaces the previous extract outright rather than accumulating
        // versions — one material has exactly one current best reading of it.
        var existing = await db.MaterialExtracts.FirstOrDefaultAsync(e => e.MaterialId == material.Id, ct);
        if (existing is not null)
            db.MaterialExtracts.Remove(existing);

        db.MaterialExtracts.Add(new MaterialExtract
        {
            MaterialId = material.Id,
            Language = result.Value.Language,
            PageCount = result.Value.PageCount,
            Sections = [.. result.Value.Sections.Select(s => new ExtractSection
            {
                Page = s.Page,
                Title = s.Title,
                Markdown = s.Markdown,
            })],
            Terms = [.. result.Value.Terms.Select(t => new ExtractTerm
            {
                Term = t.Term,
                Definition = t.Definition,
            })],
            Topics = [.. result.Value.Topics],
            Model = job.Model,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
        });

        material.Status = MaterialStatus.Ingested;

        job.InputTokens = result.InputTokens;
        job.OutputTokens = result.OutputTokens;
        job.Message = $"Extracted {result.Value.Sections.Count} sections across " +
                      $"{result.Value.PageCount} page(s); {result.Value.Topics.Count} topics, " +
                      $"{result.Value.Terms.Count} terms.";

        db.GenerationJobs.Attach(job);
        db.Entry(job).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }
}
