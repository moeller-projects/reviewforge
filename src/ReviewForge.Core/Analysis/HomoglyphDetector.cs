using System.Text;

namespace ReviewForge.Core.Analysis;

/// <summary>Detects suspicious mixed-script and confusable identifier tokens without rewriting them.</summary>
public static class HomoglyphDetector
{
    public sealed record ConfusableToken(
        string Token,
        int Line,
        int StartCol,
        string Reason,
        string? AsciiLookalike);

    public sealed record Options
    {
        public int MinTokenLength { get; init; } = 3;
        public bool RequireAsciiContext { get; init; } = true;
        public IReadOnlySet<string> AllowedScripts { get; init; } = new HashSet<string>(StringComparer.Ordinal)
            { "Latin" };
        public IReadOnlySet<string> AllowedAsciiKeywords { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "return", "import", "public", "static", "string", "class", "null" };
    }

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

    public static IReadOnlyList<ConfusableToken> ScanLine(string line, int lineNumber, Options? options = null)
    {
        options ??= new Options();
        if (options.MinTokenLength < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum token length must be positive.");

        var tokens = Tokenize(line);
        var hasAsciiContext = tokens.Any(item => item.Token.All(c => c <= 0x7F));
        if (options.RequireAsciiContext && !hasAsciiContext)
            return [];

        var findings = new List<ConfusableToken>();
        foreach (var (token, start) in tokens)
        {
            if (token.Length < options.MinTokenLength || token.All(c => c <= 0x7F))
                continue;

            var scripts = token.Where(char.IsLetter).Select(ScriptOf).Distinct(StringComparer.Ordinal).ToArray();
            var mixed = scripts.Length > 1 && scripts.Any(script => !options.AllowedScripts.Contains(script));
            var skeleton = Skeleton(token);
            var hasKnownSkeleton = !string.Equals(skeleton, token, StringComparison.Ordinal)
                && options.AllowedAsciiKeywords.Contains(skeleton);

            if (hasKnownSkeleton)
            {
                findings.Add(new ConfusableToken(token, lineNumber, start, "confusable keyword", skeleton));
            }
            else if (mixed)
            {
                findings.Add(new ConfusableToken(token, lineNumber, start, "mixed-script identifier", skeleton));
            }
        }

        return findings;
    }

    public static bool LooksConfusable(string token, Options? options, out string? asciiSkeleton)
    {
        ArgumentNullException.ThrowIfNull(token);
        options ??= new Options();
        asciiSkeleton = Skeleton(token);
        return token.Length >= options.MinTokenLength
            && !string.Equals(token, asciiSkeleton, StringComparison.Ordinal)
            && asciiSkeleton.All(c => c <= 0x7F);
    }

    private static List<(string Token, int Start)> Tokenize(string line)
    {
        var tokens = new List<(string, int)>();
        var start = -1;
        for (var i = 0; i <= line.Length; i++)
        {
            var isToken = i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
            if (isToken && start < 0)
                start = i;
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
        var builder = new StringBuilder(token.Length);
        foreach (var c in token.Normalize(System.Text.NormalizationForm.FormKC))
            builder.Append(Confusables.TryGetValue(c, out var mapped) ? mapped : c);
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
