namespace StudyApp.Core.Import;

public record ParsedCard(string Front, string Back, bool IsDuplicate);

public record ImportParseResult(List<ParsedCard> Cards, List<string> SkippedLines)
{
    public int NewCount => Cards.Count(c => !c.IsDuplicate);
    public int DuplicateCount => Cards.Count(c => c.IsDuplicate);
}

/// <summary>
/// Parses pasted text into cards, one per line. Separator auto-detected per line,
/// tried in priority order: tab, " :: ", ";". Splits on the first occurrence only,
/// so backs may contain later separator characters.
/// Duplicates (against existing deck fronts or within the pasted batch) are flagged,
/// case-insensitively, so the import preview can skip them by default.
/// </summary>
public static class CardLineParser
{
    private static readonly string[] Separators = ["\t", " :: ", ";"];

    public static ImportParseResult Parse(string? text, IEnumerable<string>? existingFronts = null)
    {
        var cards = new List<ParsedCard>();
        var skipped = new List<string>();
        var existing = new HashSet<string>(existingFronts ?? [], StringComparer.OrdinalIgnoreCase);
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            string? front = null, back = null;
            foreach (var sep in Separators)
            {
                var idx = line.IndexOf(sep, StringComparison.Ordinal);
                if (idx < 0)
                    continue;
                var f = line[..idx].Trim();
                var b = line[(idx + sep.Length)..].Trim();
                if (f.Length == 0 || b.Length == 0)
                    continue;
                (front, back) = (f, b);
                break;
            }

            if (front is null || back is null)
            {
                skipped.Add(line);
                continue;
            }

            var isDuplicate = existing.Contains(front) || !seenInBatch.Add(front);
            cards.Add(new ParsedCard(front, back, isDuplicate));
        }

        return new ImportParseResult(cards, skipped);
    }
}
