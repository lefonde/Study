using StudyApp.Core.Import;

namespace StudyApp.Core.Tests;

public class CardLineParserTests
{
    [Fact]
    public void Parses_Tab_Separated_Lines()
    {
        var result = CardLineParser.Parse("What is DI?\tDependency Injection");

        var card = Assert.Single(result.Cards);
        Assert.Equal("What is DI?", card.Front);
        Assert.Equal("Dependency Injection", card.Back);
    }

    [Fact]
    public void Parses_DoubleColon_Separator()
    {
        var result = CardLineParser.Parse("front :: back");
        var card = Assert.Single(result.Cards);
        Assert.Equal(("front", "back"), (card.Front, card.Back));
    }

    [Fact]
    public void Parses_Semicolon_Separator()
    {
        var result = CardLineParser.Parse("front;back");
        var card = Assert.Single(result.Cards);
        Assert.Equal(("front", "back"), (card.Front, card.Back));
    }

    [Fact]
    public void Tab_Takes_Priority_And_Back_Keeps_Other_Separators()
    {
        var result = CardLineParser.Parse("term\tdefinition; with :: extras");
        var card = Assert.Single(result.Cards);
        Assert.Equal("term", card.Front);
        Assert.Equal("definition; with :: extras", card.Back);
    }

    [Fact]
    public void Splits_On_First_Separator_Occurrence_Only()
    {
        var result = CardLineParser.Parse("a;b;c");
        var card = Assert.Single(result.Cards);
        Assert.Equal("a", card.Front);
        Assert.Equal("b;c", card.Back);
    }

    [Fact]
    public void Lines_Without_Separator_Are_Skipped_And_Reported()
    {
        var result = CardLineParser.Parse("valid;card\nthis line has no separator\n");

        Assert.Single(result.Cards);
        var skippedLine = Assert.Single(result.SkippedLines);
        Assert.Equal("this line has no separator", skippedLine);
    }

    [Fact]
    public void Blank_Lines_Are_Ignored()
    {
        var result = CardLineParser.Parse("a;b\n\n   \r\nc;d\n");
        Assert.Equal(2, result.Cards.Count);
        Assert.Empty(result.SkippedLines);
    }

    [Fact]
    public void Duplicate_Against_Existing_Deck_Is_Flagged_CaseInsensitively()
    {
        var result = CardLineParser.Parse("Existing Front;back", ["existing front"]);
        var card = Assert.Single(result.Cards);
        Assert.True(card.IsDuplicate);
    }

    [Fact]
    public void Duplicate_Within_Batch_Flags_Second_Occurrence_Only()
    {
        var result = CardLineParser.Parse("q;a1\nq;a2");

        Assert.Equal(2, result.Cards.Count);
        Assert.False(result.Cards[0].IsDuplicate);
        Assert.True(result.Cards[1].IsDuplicate);
        Assert.Equal(1, result.NewCount);
        Assert.Equal(1, result.DuplicateCount);
    }

    [Fact]
    public void Null_And_Empty_Input_Yield_Empty_Result()
    {
        Assert.Empty(CardLineParser.Parse(null).Cards);
        Assert.Empty(CardLineParser.Parse("").Cards);
    }
}
