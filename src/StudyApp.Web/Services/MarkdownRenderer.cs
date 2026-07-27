using Markdig;

namespace StudyApp.Web.Services;

public static class MarkdownRenderer
{
    // DisableHtml: user markdown must never inject raw HTML (stored XSS).
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static string ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? "" : Markdown.ToHtml(markdown, Pipeline);
}
