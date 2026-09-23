using ReviewForge.Core.Analysis;
using ReviewForge.Core.Reasoning.Rules;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class HomoglyphDetectorTests
{
    [Fact]
    public void Mixed_latin_and_cyrillic_identifier_is_reported()
    {
        var findings = HomoglyphDetector.ScanLine("var fileNаme = value;", 12);

        var finding = Assert.Single(findings);
        Assert.Equal("fileNаme", finding.Token);
        Assert.Equal(12, finding.Line);
        Assert.Equal("mixed-script identifier", finding.Reason);
        Assert.Equal("fileName", finding.AsciiLookalike);
    }

    [Fact]
    public void Confusable_keyword_is_reported_when_ascii_context_exists()
    {
        var findings = HomoglyphDetector.ScanLine("return рublic;", 3);

        var finding = Assert.Single(findings);
        Assert.Equal("confusable keyword", finding.Reason);
        Assert.Equal("public", finding.AsciiLookalike);
    }

    [Fact]
    public void Natural_latin_text_does_not_report()
    {
        Assert.Empty(HomoglyphDetector.ScanLine("Müller Straße", 1));
    }
    [Fact]
    public void Context_guard_suppresses_non_ascii_only_lines()
    {
        Assert.Empty(HomoglyphDetector.ScanLine("аbc", 1));
        Assert.NotEmpty(HomoglyphDetector.ScanLine("аbc", 1, new HomoglyphDetector.Options
        {
            RequireAsciiContext = false
        }));
    }

    [Fact]
    public void Diff_analyzer_reports_added_lines_only()
    {
        const string diff = """
            diff --git a/src/file.cs b/src/file.cs
            --- a/src/file.cs
            +++ b/src/file.cs
            @@ -1,2 +1,2 @@
            -var fileName = value;
            +var fileNаme = value;
            """;

        var finding = Assert.Single(HomoglyphDiffAnalyzer.Analyze(diff));
        Assert.Equal("homoglyph/mixed-script-identifier", finding.RuleId);
        Assert.Equal("src/file.cs", finding.Anchor!.FilePath);
        Assert.Equal(1, finding.Anchor.StartLine);
    }

    [Fact]
    public void Minimum_length_is_respected()
    {
        Assert.Empty(HomoglyphDetector.ScanLine("a а", 1, new HomoglyphDetector.Options { MinTokenLength = 3 }));
    }

    [Theory]
    [InlineData("а", "a")]
    [InlineData("с", "c")]
    [InlineData("е", "e")]
    [InlineData("о", "o")]
    [InlineData("р", "p")]
    [InlineData("х", "x")]
    public void Known_confusables_have_ascii_skeleton(string token, string expected)
    {
        Assert.True(HomoglyphDetector.LooksConfusable(token, new HomoglyphDetector.Options { MinTokenLength = 1 }, out var skeleton));
        Assert.Equal(expected, skeleton);
    }

    [Fact]
    public void Security_rulebook_contains_homoglyph_rules()
    {
        var book = new RuleBookComposer().Compose([], []);

        Assert.Equal("medium", book.Rules["homoglyph/mixed-script-identifier"].DefaultSeverity);
        Assert.Equal("high", book.Rules["homoglyph/confusable-keyword"].DefaultSeverity);
    }
}
