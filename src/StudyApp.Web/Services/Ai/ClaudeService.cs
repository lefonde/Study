using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;
using StudyApp.Core.Entities;

namespace StudyApp.Web.Services.Ai;

public record AiResult<T>(T Value, long InputTokens, long OutputTokens);

// Wire shapes for the structured outputs. Kept internal to this layer — the domain gets
// StudyApp.Core entities, not these.
public record ExtractedSection(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("markdown")] string Markdown);

public record ExtractedTerm(
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("definition")] string Definition);

public record ExtractionResult(
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("sections")] List<ExtractedSection> Sections,
    [property: JsonPropertyName("topics")] List<string> Topics,
    [property: JsonPropertyName("terms")] List<ExtractedTerm> Terms);

public record GeneratedCard(
    [property: JsonPropertyName("front")] string Front,
    [property: JsonPropertyName("back")] string Back,
    [property: JsonPropertyName("sourceReference")] string? SourceReference,
    [property: JsonPropertyName("rationale")] string? Rationale,
    [property: JsonPropertyName("topicRef")] string? TopicRef);

public record GenerationResult(
    [property: JsonPropertyName("cards")] List<GeneratedCard> Cards);

public record MappedSource(
    [property: JsonPropertyName("materialRef")] string MaterialRef,
    [property: JsonPropertyName("sourceReference")] string? SourceReference,
    [property: JsonPropertyName("mention")] string Mention);

public record MappedProposal(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("topicRef")] string? TopicRef,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("importance")] string Importance,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("mergeIntoRef")] string? MergeIntoRef,
    [property: JsonPropertyName("sources")] List<MappedSource> Sources);

public record MapResult(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("proposals")] List<MappedProposal> Proposals);

public record MappedCardLink(
    [property: JsonPropertyName("cardRef")] string CardRef,
    [property: JsonPropertyName("topicRefs")] List<string> TopicRefs);

public record CoverageResult(
    [property: JsonPropertyName("links")] List<MappedCardLink> Links);

/// <summary>
/// All Anthropic API access. Two calls, matching the two pipeline stages:
/// <see cref="ExtractAsync"/> reads an original file with vision (expensive, once per
/// material) and <see cref="GenerateCardsAsync"/> works from the resulting text (cheap,
/// repeatable). Nothing else in the app talks to the API.
/// </summary>
public class ClaudeService(AiOptions options, ILogger<ClaudeService> logger)
{
    // Base64 inflates by ~4/3, and the API caps a request around 32 MB. Refuse early with a
    // message the user can act on rather than letting a huge upload fail deep in the request.
    private const long MaxSourceBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Rules every extraction obeys regardless of mode. These are about fidelity of *facts* —
    /// notation, language, location — which matter identically whether the output is a
    /// transcription or a set of notes.
    /// </summary>
    private const string ExtractionSharedRules = """
        - Write ALL mathematics as LaTeX: $...$ inline, $$...$$ for display. Never describe a
          formula in prose ("lambda times S" is wrong; "$\lambda \cdot \bar{S}$" is right).
        - Write the delimiters as plain $ characters. Never escape them as \$.
        - Put ONLY mathematical notation between the delimiters — never Hebrew or other
          right-to-left words. Prose goes outside the math. Write
          `נגדיר $X_i$ כמספר הקופונים`, never `$X_i = מספר הקופונים$`. Right-to-left text
          inside a formula cannot be typeset and renders as reversed garbage, and because this
          extraction is reused for every later step the damage would be permanent.
        - Preserve the document's original language exactly. Never translate. A Hebrew source
          stays Hebrew.
        - Figures and diagrams cannot be reproduced as text: describe each in square brackets
          in place, e.g. [Figure: process state diagram — New → Ready → Running → Terminated].
        - Record the page number every section came from. Citations depend on it being right.
        - Split into sections at natural boundaries: headings, numbered problems, topics.
        - topics: the distinct concepts this document covers, in the source language.
        - terms: domain vocabulary with the definition this source uses, so later work speaks
          the course's own language rather than generic textbook phrasing.
        """;

    /// <summary>
    /// For material the student owns and is assessed on, where the exact wording IS the object
    /// of study: a question's precise phrasing determines what it asks, and their own notes are
    /// theirs to reproduce.
    /// </summary>
    private const string VerbatimExtractionPrompt = $"""
        You convert a page of course material into a faithful, reusable text representation.

        This extraction runs ONCE and then permanently stands in for the original file for all
        later work — no downstream step ever sees the source document again. Anything you drop
        here is lost for good, so transcribe rather than summarise.

        Rules:
        - Transcribe the document completely. Do not summarise, abridge, or skip content.
        - Transcribe handwriting as faithfully as you can. Mark a genuinely illegible word [?].
        - Keep exam and assignment questions word for word, including mark allocations: their
          exact phrasing is what determines what is being asked.
        {ExtractionSharedRules}
        """;

    /// <summary>
    /// For published reference material — textbooks, course books, distributed lecture notes.
    ///
    /// Produces comprehensive study notes rather than a transcription. This is a better fit for
    /// what the pipeline downstream actually consumes: mapping reads section titles, topics and
    /// terms, and card generation needs definitions, results and methods — none of which require
    /// the source's prose reproduced at length. It also keeps a full course book's worth of
    /// published text from being copied wholesale into local storage.
    /// </summary>
    private const string StructuredExtractionPrompt = $"""
        You turn a page of published course material into comprehensive study notes.

        This runs ONCE and everything downstream — topic mapping, flashcard writing — sees only
        your notes, never the source. So the SUBSTANCE must survive in full even though the
        prose does not: a student working from your notes alone should be able to learn the
        material and answer exam questions on it.

        Rules:
        - Capture every concept, definition, rule, theorem or property statement, formula,
          algorithm, and worked-example method. These are the facts of the subject and none of
          them may be dropped, softened, or merged.
        - Write them in your own words, structured as notes. Do not reproduce the source's
          sentences and paragraphs at length — this is published material.
        - Length follows the content: a dense page of definitions produces dense notes. Never
          compress by leaving something out; compress by dropping narration, motivation, asides
          and repetition.
        - Keep worked examples as method — the steps, the reasoning at each step, and the result
          — rather than as a copy of the exercise text.
        - Quote only where the exact words carry the meaning: a formal definition, a theorem
          statement, a term being introduced.
        {ExtractionSharedRules}
        """;

    private const string GenerationSystemPrompt = """
        You write flashcards for a student, from their own course material.

        Rules:
        - Use ONLY the supplied source text. Never introduce facts that aren't in it. If the
          source doesn't support a card, don't write that card.
        - Write in the same language as the source material.
        - Mathematics as LaTeX: $...$ inline, $$...$$ for display. Write the delimiters as
          plain $ characters; never escape them as \$.
        - Put ONLY mathematical notation between the delimiters — never Hebrew or other
          right-to-left words. Prose goes outside the math. Write
          `מהי ההסתברות ש-$A$ מתרחש?`, never `$A = המאורע שקורה$`. Right-to-left text inside a
          formula cannot be typeset and renders as reversed garbage.
        - One idea per card. The front is a question; the back answers it and nothing more.
        - Use the course's own terminology, supplied in the glossary, over generic synonyms.
        - Do not restate cards the student already has — a list of existing fronts is supplied.
        - Set sourceReference to the page the card came from, using the <!-- p.N --> markers in
          the source (format it as "p. 14"). Never invent a page number.
        - rationale: one short clause on why this is worth memorising.
        - Prefer what is definitional, formula-based, or examinable over incidental detail.
        """;

    private const string MappingSystemPrompt = """
        You maintain a map of what a university course covers and what matters in it.

        You are REVISING an existing map, not writing a new one. The current topics are supplied
        with short refs (T1, T2…). Anything that survives must keep its exact existing name, so
        that the student's cards, notes and decisions stay attached to it. Renaming a topic
        silently destroys those links.

        Propose one change per entry:
        - add       — a concept genuinely absent from the current map. Never re-add something
                      already listed under any wording; if the wording should change, that is a
                      merge, not an add.
        - reweight  — an existing topic whose importance the new material changes. Say what
                      changed it.
        - merge     — two entries that are the same concept. topicRef is absorbed into
                      mergeIntoRef; keep the better-established name as the survivor.
        - retire    — no material supports this any more.

        If nothing changed, return an empty proposals list. That is a valid and useful answer —
        do not invent churn to look productive.

        IMPORTANCE is judged from what the course ASSESSES, not from how much text covers it.
        Each material carries a weight saying how strongly that kind of material signals what
        matters in THIS course; a course examined by one final and a course graded on weekly
        assignments treat the same textbook very differently. Respect those weights over your own
        assumptions about how courses usually work.
        - core        — assessed in high-weight material, or a prerequisite for something that is.
        - supporting  — taught and needed to understand core topics, but not itself assessed.
        - peripheral  — mentioned, background, or an aside.

        reason: cite the evidence, naming the material and location — "assessed in Maman 11 Q3",
        not "seems important". A reason that cannot be checked is worthless to the student.

        sources: every material that evidences the topic, using its ref (M1, M2…).
        mention = assessed (a question tests it) | defined (it is taught or defined here) |
        referenced (mentioned in passing).

        Other rules:
        - Use the course's own language. Never translate a Hebrew topic into English.
        - Name topics as concepts ("Banker's algorithm"), not as document headings
          ("Section 3.2") and not as questions.
        - Mathematics in names or summaries follows the same rule as elsewhere: LaTeX between
          plain $ delimiters, and never right-to-left text inside them.
        - Only use refs that appear in the input. Never invent one.
        """;

    private static readonly string MappingSchema = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "One line: what changed in this revision and why." },
            "proposals": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string", "enum": ["add", "reweight", "merge", "retire"] },
                  "topicRef": { "type": "string", "description": "Existing topic ref (T1). Empty for add." },
                  "name": { "type": "string" },
                  "summary": { "type": "string" },
                  "importance": { "type": "string", "enum": ["core", "supporting", "peripheral"] },
                  "reason": { "type": "string" },
                  "mergeIntoRef": { "type": "string", "description": "Surviving topic ref for a merge. Empty otherwise." },
                  "sources": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "materialRef": { "type": "string" },
                        "sourceReference": { "type": "string" },
                        "mention": { "type": "string", "enum": ["assessed", "defined", "referenced"] }
                      },
                      "required": ["materialRef", "sourceReference", "mention"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["kind", "topicRef", "name", "summary", "importance", "reason", "mergeIntoRef", "sources"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "proposals"],
          "additionalProperties": false
        }
        """;

    private const string CoverageSystemPrompt = """
        You decide which of a course's topics each flashcard actually helps the student learn.

        For every card, list the topics it covers, by ref (T1, T2…).

        - A card may cover more than one topic. "What is the difference between a mutex and a
          semaphore?" genuinely serves both, and listing only one would leave the other looking
          uncovered when a perfectly good card for it already exists.
        - A card may cover none. Return an empty list rather than forcing a weak match: a card
          wrongly credited to a topic hides a real gap, which is worse than leaving it unlinked.
        - Judge what the card TEACHES, not which words it contains. A card asking for the four
          conditions of circular wait covers Deadlock even without using that word.
        - Only use refs that appear in the input. Never invent one.
        """;

    private static readonly string CoverageSchema = """
        {
          "type": "object",
          "properties": {
            "links": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "cardRef": { "type": "string" },
                  "topicRefs": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["cardRef", "topicRefs"],
                "additionalProperties": false
              }
            }
          },
          "required": ["links"],
          "additionalProperties": false
        }
        """;

    private static readonly string ExtractionSchema = """
        {
          "type": "object",
          "properties": {
            "language": { "type": "string", "description": "BCP-47 tag of the source language, e.g. he, en" },
            "pageCount": { "type": "integer" },
            "sections": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "page": { "type": "integer" },
                  "title": { "type": "string" },
                  "markdown": { "type": "string" }
                },
                "required": ["page", "title", "markdown"],
                "additionalProperties": false
              }
            },
            "topics": { "type": "array", "items": { "type": "string" } },
            "terms": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "term": { "type": "string" },
                  "definition": { "type": "string" }
                },
                "required": ["term", "definition"],
                "additionalProperties": false
              }
            }
          },
          "required": ["language", "pageCount", "sections", "topics", "terms"],
          "additionalProperties": false
        }
        """;

    private static readonly string GenerationSchema = """
        {
          "type": "object",
          "properties": {
            "cards": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "front": { "type": "string" },
                  "back": { "type": "string" },
                  "sourceReference": { "type": "string" },
                  "rationale": { "type": "string" },
                  "topicRef": { "type": "string", "description": "Topic ref (T1) this card is for. Empty when no topics were requested." }
                },
                "required": ["front", "back", "sourceReference", "rationale", "topicRef"],
                "additionalProperties": false
              }
            }
          },
          "required": ["cards"],
          "additionalProperties": false
        }
        """;

    private AnthropicClient CreateClient() =>
        options.IsConfigured
            ? new AnthropicClient { ApiKey = options.ApiKey }
            : throw new InvalidOperationException(
                "No Anthropic API key configured. Set StudyApp__Anthropic__ApiKey.");

    /// <summary>
    /// Stage 1: read the original file with vision. Run once per material.
    ///
    /// The mode depends on the material's kind — see <see cref="ModeFor"/>.
    /// </summary>
    public async Task<AiResult<ExtractionResult>> ExtractAsync(
        byte[] fileBytes, string mimeType, MaterialKind materialKind, CancellationToken ct = default)
    {
        if (fileBytes.LongLength > MaxSourceBytes)
            throw new InvalidOperationException(
                $"File is {fileBytes.LongLength / 1024 / 1024} MB; the limit for one ingestion pass is " +
                $"{MaxSourceBytes / 1024 / 1024} MB. Split it into chapters and upload separately.");

        var mode = ModeFor(materialKind);
        var source = BuildSourceBlock(fileBytes, mimeType);
        var instruction = new TextBlockParam
        {
            Text = mode == ExtractionMode.Verbatim
                ? $"This is course material of kind: {materialKind}. Transcribe it in full, following every rule."
                : $"This is published course material of kind: {materialKind}. Write complete study "
                  + "notes from it, following every rule.",
        };

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 64000,
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text = mode == ExtractionMode.Verbatim
                        ? VerbatimExtractionPrompt
                        : StructuredExtractionPrompt,
                },
            },
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = ParseSchema(ExtractionSchema) },
            },
            Messages =
            [
                new() { Role = Role.User, Content = new List<ContentBlockParam> { source, instruction } },
            ],
        };

        return await StreamJsonAsync<ExtractionResult>(parameters, ct);
    }

    /// <summary>
    /// Stage 2: write cards from already-extracted text. No vision, no original file — which
    /// is what makes repeated generation over the same material cheap.
    /// </summary>
    public async Task<AiResult<GenerationResult>> GenerateCardsAsync(
        string sourceText,
        IReadOnlyList<string> glossary,
        IReadOnlyList<string> existingFronts,
        int targetCount,
        string? targetTopics = null,
        CancellationToken ct = default)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Write up to {targetCount} flashcards from the source material below.");

        if (!string.IsNullOrWhiteSpace(targetTopics))
        {
            prompt.AppendLine();
            prompt.AppendLine("## Write cards ONLY for these topics — they currently have none");
            prompt.AppendLine(targetTopics);
            prompt.AppendLine();
            prompt.AppendLine(
                "Set topicRef on every card to the topic it covers. Spread the cards across the " +
                "topics rather than exhausting the first one. If the source doesn't support a " +
                "topic, write nothing for it and leave the gap visible rather than inventing " +
                "material to fill it.");
        }

        if (glossary.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("## Course glossary — prefer this vocabulary");
            foreach (var term in glossary.Take(200))
                prompt.AppendLine($"- {term}");
        }

        if (existingFronts.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("## Cards the student already has — do not duplicate these");
            foreach (var front in existingFronts.Take(300))
                prompt.AppendLine($"- {front}");
        }

        prompt.AppendLine();
        prompt.AppendLine("## Source material");
        prompt.AppendLine(sourceText);

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 16000,
            System = new List<TextBlockParam>
            {
                // Stable across every generation run for this course, so it caches.
                new() { Text = GenerationSystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = ParseSchema(GenerationSchema) },
            },
            Messages = [new() { Role = Role.User, Content = prompt.ToString() }],
        };

        return await StreamJsonAsync<GenerationResult>(parameters, ct);
    }

    /// <summary>
    /// Stage 2b: revise the course's topic map from its extracts.
    ///
    /// Text-only like generation, and deliberately cheap enough to re-run whenever material is
    /// added — that is the whole premise of a map that keeps up with the course rather than one
    /// snapshot taken when it was first uploaded.
    /// </summary>
    public async Task<AiResult<MapResult>> MapCourseAsync(
        string courseName,
        string weightProfile,
        string currentTopics,
        string materialBundle,
        CancellationToken ct = default)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"# Course: {courseName}");
        prompt.AppendLine();
        prompt.AppendLine("## How much each kind of material counts in this course");
        prompt.AppendLine(weightProfile);
        prompt.AppendLine();
        prompt.AppendLine("## Current map");
        prompt.AppendLine(currentTopics);
        prompt.AppendLine();
        prompt.AppendLine("## Materials");
        prompt.AppendLine(materialBundle);

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 32000,
            System = new List<TextBlockParam>
            {
                // Identical on every re-map of every course, so it caches across runs.
                new() { Text = MappingSystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = ParseSchema(MappingSchema) },
            },
            Messages = [new() { Role = Role.User, Content = prompt.ToString() }],
        };

        return await StreamJsonAsync<MapResult>(parameters, ct);
    }

    /// <summary>
    /// Stage 2c: works out which topics each card covers.
    ///
    /// Separate from <see cref="MapCourseAsync"/> rather than folded into it: this needs
    /// re-running whenever cards are added, which is far more often than the topic set changes,
    /// and sending hundreds of card fronts through the mapping prompt would make re-mapping
    /// expensive for no benefit.
    /// </summary>
    public async Task<AiResult<CoverageResult>> MapCoverageAsync(
        string topics, string cards, CancellationToken ct = default)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("## Topics");
        prompt.AppendLine(topics);
        prompt.AppendLine();
        prompt.AppendLine("## Cards");
        prompt.AppendLine(cards);

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 16000,
            System = new List<TextBlockParam>
            {
                new() { Text = CoverageSystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = ParseSchema(CoverageSchema) },
            },
            Messages = [new() { Role = Role.User, Content = prompt.ToString() }],
        };

        return await StreamJsonAsync<CoverageResult>(parameters, ct);
    }

    /// <summary>
    /// Normalizes markdown the model produced before it is stored or rendered.
    ///
    /// Models sometimes emit `\$` — an artifact of escaping inside JSON string output. Markdig
    /// reads that as a literal dollar sign rather than a math delimiter, so the formula never
    /// becomes math: it stays raw LaTeX in the page. Belt-and-braces with the prompt rule,
    /// because an extract is written once and reused forever.
    /// </summary>
    public static string NormalizeMarkdown(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? "" : markdown.Replace("\\$", "$");

    private static ContentBlockParam BuildSourceBlock(byte[] bytes, string mimeType)
    {
        var base64 = Convert.ToBase64String(bytes);
        if (mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            return new DocumentBlockParam { Source = new Base64PdfSource { Data = base64 } };

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new ImageBlockParam
            {
                Source = new Base64ImageSource { Data = base64, MediaType = mimeType },
            };

        throw new InvalidOperationException(
            $"Cannot ingest '{mimeType}'. Upload a PDF or an image (png/jpeg/webp/gif); " +
            "scans and photos of handwriting are fine.");
    }

    private static Dictionary<string, JsonElement> ParseSchema(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>
    /// Streams the response and accumulates the JSON body. Streaming matters here because
    /// a full extraction can run for minutes and produce tens of thousands of tokens — a
    /// non-streaming request of that size risks hitting the request timeout.
    /// </summary>
    private async Task<AiResult<T>> StreamJsonAsync<T>(MessageCreateParams parameters, CancellationToken ct)
    {
        var json = new StringBuilder();
        long inputTokens = 0, outputTokens = 0;

        for (var attempt = 1; ; attempt++)
        {
            json.Clear();
            inputTokens = outputTokens = 0;

            try
            {
                var client = CreateClient();
                await foreach (var streamEvent in client.Messages.CreateStreaming(parameters).WithCancellation(ct))
                {
                    if (streamEvent.TryPickContentBlockDelta(out var delta) &&
                        delta.Delta.TryPickText(out var text))
                    {
                        json.Append(text.Text);
                    }
                    else if (streamEvent.TryPickStart(out var start))
                    {
                        inputTokens = start.Message.Usage.InputTokens;
                    }
                    else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Usage is { } usage)
                    {
                        outputTokens = usage.OutputTokens;
                    }
                }
                break;
            }
            catch (Exception ex) when (IsContentFiltered(ex))
            {
                // Deliberately not retried: the block is a deterministic function of the request,
                // so a second attempt buys the same refusal and charges for it again.
                logger.LogWarning(ex, "Response blocked by the content filter");
                throw new InvalidOperationException(
                    "Anthropic's content filter blocked the model's response. Retrying won't help — " +
                    "the same request is blocked the same way. Extraction asks for a complete " +
                    "verbatim transcription, which is the usual trigger on copyrighted text; a " +
                    "shorter section, or a document you're free to reproduce, should go through.", ex);
            }
            catch (Exception ex) when (attempt <= MaxTransientRetries && IsTransient(ex))
            {
                // A long ingestion is exactly when an overloaded upstream is most likely to be
                // hit, and losing a multi-minute vision pass to a blip is worth one retry.
                var delay = TimeSpan.FromSeconds(2 * attempt);
                logger.LogWarning(ex,
                    "Transient stream failure on attempt {Attempt}; retrying in {Delay}s", attempt, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        var payload = json.ToString();
        if (string.IsNullOrWhiteSpace(payload))
            throw new InvalidOperationException("The model returned an empty response.");

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(payload);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Could not parse model output as {Type}. Payload starts: {Head}",
                typeof(T).Name, payload[..Math.Min(400, payload.Length)]);
            throw new InvalidOperationException(
                "The model's response wasn't in the expected format. Try running this again.", ex);
        }

        return value is null
            ? throw new InvalidOperationException("The model returned no usable content.")
            : new AiResult<T>(value, inputTokens, outputTokens);
    }

    private const int MaxTransientRetries = 2;

    public enum ExtractionMode
    {
        /// <summary>Word-for-word. The student's own material, and assessments whose wording is the point.</summary>
        Verbatim,
        /// <summary>Comprehensive study notes. Published reference material.</summary>
        StructuredNotes,
    }

    /// <summary>
    /// Which extraction mode a kind of material gets.
    ///
    /// The split is ownership and purpose, not importance. Exams, assignments, the student's own
    /// notes and a syllabus are theirs and short, and for assessments the exact phrasing is the
    /// object of study — a question means what it says. Textbooks and distributed lecture notes
    /// are published works whose *content* is what's needed downstream, not their prose: mapping
    /// reads topics and terms, and card generation needs definitions, results and methods.
    ///
    /// Structured notes is also the safe default for <see cref="MaterialKind.Other"/>, since an
    /// unclassified upload is more likely to be something published than something the student
    /// wrote.
    /// </summary>
    public static ExtractionMode ModeFor(MaterialKind kind) => kind switch
    {
        MaterialKind.Exam or MaterialKind.HomeAssignment => ExtractionMode.Verbatim,
        MaterialKind.HandwrittenNotes or MaterialKind.Screenshot => ExtractionMode.Verbatim,
        MaterialKind.Syllabus => ExtractionMode.Verbatim,
        _ => ExtractionMode.StructuredNotes,
    };

    /// <summary>
    /// A server-side block on the model's <i>output</i>, delivered as an error event part-way
    /// through the stream rather than as a failed request — so it surfaces here as an exception
    /// carrying the raw SSE payload, which is no use to anyone in the jobs list.
    /// </summary>
    private static bool IsContentFiltered(Exception ex) =>
        Flatten(ex).Any(m =>
            m.Contains("content filtering", StringComparison.OrdinalIgnoreCase)
            || m.Contains("content_filter", StringComparison.OrdinalIgnoreCase));

    /// <summary>Worth one more try: upstream capacity and connection failures, not refusals.</summary>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException
        || Flatten(ex).Any(m =>
            m.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || m.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || m.Contains("\"type\":\"api_error\"", StringComparison.OrdinalIgnoreCase)
            || m.Contains("529", StringComparison.Ordinal)
            || m.Contains("503", StringComparison.Ordinal));

    /// <summary>Messages from the whole exception chain — the SDK often wraps the real detail.</summary>
    private static IEnumerable<string> Flatten(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            yield return ex.Message;
    }
}
