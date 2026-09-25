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

    [Theory]
    // Default-options corpus: every case exercises a distinct branch of the scanner.
    [InlineData("var fileNаme = value;")]       // mixed Latin+Cyrillic
    [InlineData("return рublic;")]              // confusable keyword
    [InlineData("Müller Straße")]               // Latin-1 supplement, single script
    [InlineData("аbc")]                         // suppressed by RequireAsciiContext
    [InlineData("")]                            // empty line
    [InlineData("plain ascii only")]            // ASCII fast path
    [InlineData("ｖａｒ x = 1;")]               // fullwidth keyword via NFKC
    [InlineData("Αlpha beta")]                  // Greek+Latin mixed
    [InlineData("ΑΤΜ card")]                    // Greek only, skeleton not a keyword
    [InlineData("e\u0301xit = 1")]              // decomposed combining mark
    [InlineData("user_аgent id")]               // underscore token, mixed script
    [InlineData("==> ü <==")]                   // symbol soup, short token
    [InlineData("türkçe variable ok")]          // Latin single script
    [InlineData("🎉party time x")]              // emoji surrogate pair is not a letter
    [InlineData("x = 1; // аbс")]               // comment token, context present
    public void ScanLine_matches_reference_implementation(string input)
    {
        var expected = ReferenceScanner.ScanLine(input, 7);
        var actual = HomoglyphDetector.ScanLine(input, 7);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ScanLine_matches_reference_implementation_with_custom_options()
    {
        var cases = new[]
        {
            ("аbc", new HomoglyphDetector.Options { RequireAsciiContext = false }),
            ("аbc", new HomoglyphDetector.Options
            {
                RequireAsciiContext = false,
                AllowedScripts = new HashSet<string>(StringComparer.Ordinal) { "Latin", "Cyrillic" },
            }),
            ("x ѕelect", new HomoglyphDetector.Options
            {
                AllowedAsciiKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "select" },
            }),
            ("a а", new HomoglyphDetector.Options { MinTokenLength = 3 }),
            ("a а", new HomoglyphDetector.Options { MinTokenLength = 1 }),
            ("return", new HomoglyphDetector.Options { AllowedAsciiKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) }),
        };

        foreach (var (input, options) in cases)
        {
            var expected = ReferenceScanner.ScanLine(input, 3, options);
            var actual = HomoglyphDetector.ScanLine(input, 3, options);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Options_default_matches_fresh_default_options()
    {
        var fresh = new HomoglyphDetector.Options();
        Assert.Equal(fresh.MinTokenLength, HomoglyphDetector.Options.Default.MinTokenLength);
        Assert.Equal(fresh.RequireAsciiContext, HomoglyphDetector.Options.Default.RequireAsciiContext);
        Assert.Equal(fresh.AllowedScripts, HomoglyphDetector.Options.Default.AllowedScripts);
        Assert.Equal(fresh.AllowedAsciiKeywords, HomoglyphDetector.Options.Default.AllowedAsciiKeywords);
    }

    [Fact]
    public void Analyze_stays_under_allocation_budget_for_large_diffs()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("+++ b/f.cs\n@@ -0,0 +1,10001 @@\n");
        for (var i = 0; i < 10_000; i++)
        {
            builder.Append("+var count").Append(i).Append(" = ComputeValue(x); // plain ascii line here\n");
        }

        builder.Append("+var fileNаme = value;\n");
        var diff = builder.ToString();

        HomoglyphDiffAnalyzer.Analyze(diff); // warmup (JIT) outside the measurement window
        var before = GC.GetAllocatedBytesForCurrentThread();
        var findings = HomoglyphDiffAnalyzer.Analyze(diff);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Single(findings);
        Assert.True(allocated < 2_000_000, $"allocated {allocated:N0} bytes for a 10k-line scan");
    }

    /// <summary>Pre-P2-27 scanner kept verbatim as the parity oracle for the de-allocated
    /// implementation (P2-27 §5: same inputs through old vs new path).</summary>
    private static class ReferenceScanner
    {
        private static readonly IReadOnlyDictionary<char, char> Confusables = new Dictionary<char, char>
        {
            ['а'] = 'a', ['А'] = 'A', ['с'] = 'c', ['С'] = 'C',
            ['е'] = 'e', ['Е'] = 'E', ['о'] = 'o', ['О'] = 'O',
            ['р'] = 'p', ['Р'] = 'P', ['х'] = 'x', ['Х'] = 'X',
            ['і'] = 'i', ['І'] = 'I', ['ј'] = 'j', ['Ј'] = 'J',
            ['у'] = 'y', ['У'] = 'Y', ['ѕ'] = 's', ['Ѕ'] = 'S',
            ['ѵ'] = 'v', ['Ѵ'] = 'V',
            ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H',
            ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M', ['Ν'] = 'N', ['Ο'] = 'O',
            ['Ρ'] = 'P', ['Τ'] = 'T', ['Χ'] = 'X', ['Υ'] = 'Y'
        };

        public static IReadOnlyList<HomoglyphDetector.ConfusableToken> ScanLine(
            string line, int lineNumber, HomoglyphDetector.Options? options = null)
        {
            options ??= new HomoglyphDetector.Options();
            var tokens = Tokenize(line);
            var hasAsciiContext = tokens.Any(item => item.Token.All(c => c <= 0x7F));
            if (options.RequireAsciiContext && !hasAsciiContext)
            {
                return [];
            }

            var findings = new List<HomoglyphDetector.ConfusableToken>();
            foreach (var (token, start) in tokens)
            {
                if (token.Length < options.MinTokenLength || token.All(c => c <= 0x7F))
                {
                    continue;
                }

                var scripts = token.Where(char.IsLetter).Select(ScriptOf).Distinct(StringComparer.Ordinal).ToArray();
                var mixed = scripts.Length > 1 && scripts.Any(script => !options.AllowedScripts.Contains(script));
                var skeleton = Skeleton(token);
                var hasKnownSkeleton = !string.Equals(skeleton, token, StringComparison.Ordinal)
                    && options.AllowedAsciiKeywords.Contains(skeleton);

                if (hasKnownSkeleton)
                {
                    findings.Add(new HomoglyphDetector.ConfusableToken(token, lineNumber, start, "confusable keyword", skeleton));
                }
                else if (mixed)
                {
                    findings.Add(new HomoglyphDetector.ConfusableToken(token, lineNumber, start, "mixed-script identifier", skeleton));
                }
            }

            return findings;
        }

        private static List<(string Token, int Start)> Tokenize(string line)
        {
            var tokens = new List<(string, int)>();
            var start = -1;
            for (var i = 0; i <= line.Length; i++)
            {
                var isToken = i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
                if (isToken && start < 0)
                {
                    start = i;
                }
                else if (!isToken && start >= 0)
                {
                    tokens.Add((line[start..i], start));
                    start = -1;
                }
            }

            return tokens;
        }

        private static string Skeleton(string token)
        {
            var builder = new System.Text.StringBuilder(token.Length);
            foreach (var c in token.Normalize(System.Text.NormalizationForm.FormKC))
            {
                builder.Append(Confusables.TryGetValue(c, out var mapped) ? mapped : c);
            }

            return builder.ToString();
        }

        private static string ScriptOf(char c)
            => c switch
            {
                >= '\u0370' and <= '\u03FF' => "Greek",
                >= '\u0400' and <= '\u052F' => "Cyrillic",
                >= '\u0530' and <= '\u058F' => "Armenian",
                >= '\uFF00' and <= '\uFFEF' => "Fullwidth",
                _ when c <= 0x024F => "Latin",
                _ => "Other"
            };
    }
}
