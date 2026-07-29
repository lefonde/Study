using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;

namespace StudyApp.Web.Services;

public static class MarkdownRenderer
{
    // DisableHtml: user markdown must never inject raw HTML (stored XSS).
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static string ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? "" : Markdown.ToHtml(markdown, Pipeline);

    /// <summary>
    /// Renders markdown for a spot where the text sits <i>inline</i> — a topic name beside its
    /// badges, a plan item beside its verdict — with no wrapping paragraph.
    ///
    /// <see cref="ToHtml"/> always emits block HTML, so even "Banker's algorithm" comes back as
    /// <c>&lt;p&gt;…&lt;/p&gt;</c>. A paragraph inside a span is invalid, and the browser hoists
    /// it out of the flex row it was laid out in. Suppressing it is what lets rendered LaTeX and
    /// RTL text live in a label rather than only in a card body.
    ///
    /// Input that is genuinely more than one block (someone pasted a list into a topic name)
    /// still renders as blocks — running it together would lose the structure, and a stray
    /// margin is the smaller problem.
    /// </summary>
    public static string ToInlineHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var document = Markdown.Parse(markdown, Pipeline);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer)
        {
            ImplicitParagraph = document.Count == 1 && document[0] is ParagraphBlock,
        };
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return writer.ToString();
    }
}
