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
    public void Diff_analyzer_scans_quoted_paths_identically_to_ascii()
    {
        const string asciiDiff = """
            diff --git a/src/paylo.cs b/src/paylo.cs
            --- a/src/paylo.cs
            +++ b/src/paylo.cs
            @@ -0,0 +1,1 @@
            +var fileNаme = value;
            """;
        const string quotedDiff = """
            diff --git "a/src/paylo\303\244d.cs" "b/src/paylo\303\244d.cs"
            --- "a/src/paylo\303\244d.cs"
            +++ "b/src/paylo\303\244d.cs"
            @@ -0,0 +1,1 @@
            +var fileNаme = value;
            """;

        var ascii = Assert.Single(HomoglyphDiffAnalyzer.Analyze(asciiDiff));
        var quoted = Assert.Single(HomoglyphDiffAnalyzer.Analyze(quotedDiff));
        Assert.Equal(ascii.RuleId, quoted.RuleId);
        Assert.Equal(ascii.Snippet, quoted.Snippet);
        Assert.Equal("src/payloäd.cs", quoted.Anchor!.FilePath); // decoded, not the raw escape sequence
        Assert.Equal(ascii.Anchor!.StartLine, quoted.Anchor.StartLine);
    }

    [Fact]
    public void Diff_analyzer_treats_plus_prefixed_content_as_content_not_header()
    {
        // An added line that literally starts with "+++ b/" must be scanned, not treated
        // as a new file header (P1-14 ordering fix).
        const string diff = """
            diff --git a/src/real.cs b/src/real.cs
            --- a/src/real.cs
            +++ b/src/real.cs
            @@ -0,0 +1,2 @@
            +++ b/fake.cs
            +var fileNаme = value;
            """;

        var finding = Assert.Single(HomoglyphDiffAnalyzer.Analyze(diff));
        Assert.Equal("src/real.cs", finding.Anchor!.FilePath);
        Assert.Equal(2, finding.Anchor.StartLine);
    }

    [Fact]
    public void Diff_analyzer_ends_hunk_on_unexpected_line_and_counts_context()
    {
        const string diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,3 +1,4 @@
             context
            -removed
            +var fileNаme = value;
            UNEXPECTED
            +var otherNаme = value;
            """;

        var finding = Assert.Single(HomoglyphDiffAnalyzer.Analyze(diff));
        Assert.Equal("f.cs", finding.Anchor!.FilePath);
        Assert.Equal(2, finding.Anchor.StartLine); // one context line precedes the addition
        // The "+var otherNаme" line follows the unexpected line: hunk ended, not scanned.
    }

    [Fact]
    public void Diff_analyzer_ignores_content_after_deleted_file_marker()
    {
        const string diff = """
            diff --git a/gone.cs b/gone.cs
            --- a/gone.cs
            +++ /dev/null
            @@ -1,1 +0,0 @@
            -var fileNаme = value;
            +++ b/later.cs
            @@ -0,0 +1,1 @@
            +var fileNаme = value;
            """;

        var finding = Assert.Single(HomoglyphDiffAnalyzer.Analyze(diff));
        Assert.Equal("later.cs", finding.Anchor!.FilePath);
    }

    [Fact]
    public void Diff_analyzer_ignores_at_lines_without_hunk_numbers()
    {
        const string diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ no numbers here @@
            +var fileNаme = value;
            """;

        Assert.Empty(HomoglyphDiffAnalyzer.Analyze(diff));
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
