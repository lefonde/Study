namespace StudyApp.Core.Entities;

/// <summary>
/// One contiguous piece of an extracted document, tagged with the page it came from so
/// generated cards can cite a real location ("p. 14") back in the original file.
/// </summary>
public class ExtractSection
{
    public int Page { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Markdown with LaTeX as $...$ / $$...$$ — the same syntax cards are authored in.</summary>
    public string Markdown { get; set; } = "";
}

/// <summary>A term the course uses, captured so later generation reuses the source's vocabulary.</summary>
public class ExtractTerm
{
    public string Term { get; set; } = "";
    public string Definition { get; set; } = "";
}

/// <summary>
/// The AI-ready form of an uploaded <see cref="Material"/>, produced once by an expensive
/// vision pass over the raw PDF/scan and then reused forever.
///
/// This is the boundary between the two stages of the pipeline: ingestion reads original
/// files, generation reads only extracts. Everything downstream (card generation, importance
/// analysis, gap closing) is therefore cheap, fast and text-only, and a 300-page textbook is
/// paid for once rather than on every run.
///
/// The consequence is that this record must be able to *stand in for the original*: math
/// survives as LaTeX rather than prose, the source language is preserved rather than
/// translated, figures that cannot be represented as text are described in place, and page
/// numbers are kept so citations stay verifiable. Extraction quality is the ceiling on every
/// downstream feature, which is why an extract is viewable and re-runnable rather than an
/// invisible cache.
/// </summary>
public class MaterialExtract : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MaterialId { get; set; }
    public Material? Material { get; set; }

    /// <summary>BCP-47-ish tag detected from the document, e.g. "he", "en". Drives generation language.</summary>
    public string? Language { get; set; }
    public int PageCount { get; set; }

    public List<ExtractSection> Sections { get; set; } = [];
    public List<ExtractTerm> Terms { get; set; } = [];
    public List<string> Topics { get; set; } = [];

    /// <summary>Which model produced this, so a re-run after a model upgrade is an informed choice.</summary>
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>The whole document as one markdown string, for prompt context and for review.</summary>
    public string ToMarkdown() =>
        string.Join("\n\n", Sections.Select(s =>
            string.IsNullOrWhiteSpace(s.Title)
                ? $"<!-- p.{s.Page} -->\n{s.Markdown}"
                : $"<!-- p.{s.Page} -->\n## {s.Title}\n\n{s.Markdown}"));
}
