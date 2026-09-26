using System.Collections.Immutable;
using System.Numerics;
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

        /// <summary>Shared immutable default (P2-27): resolved once per scan instead of being
        /// allocated per line. Do not mutate the sets — construct an <see cref="Options"/>
        /// for custom policies.</summary>
        public static Options Default { get; } = new()
        {
            AllowedScripts = ImmutableHashSet.Create(StringComparer.Ordinal, "Latin"),
            AllowedAsciiKeywords = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                "return", "import", "public", "static", "string", "class", "null"),
        };
    }

    // Latin-lookalike mappings pinned from Unicode 16.0.0 confusables.txt (2024-08-14),
    // Cyrillic/Greek/Armenian blocks plus visual Arabic-Indic digits (P2-30). Only
    // letter/digit targets — Skeleton runs over identifier tokens, never punctuation.
    private static readonly IReadOnlyDictionary<char, char> Confusables = new Dictionary<char, char>
    {
        ['ͺ'] = 'i', ['Ϳ'] = 'J', ['Α'] = 'A', ['Β'] = 'B',
        ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H', ['Θ'] = 'O',
        ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M', ['Ν'] = 'N',
        ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T', ['Υ'] = 'Y',
        ['Χ'] = 'X', ['α'] = 'a', ['γ'] = 'y', ['η'] = 'n',
        ['θ'] = 'O', ['ι'] = 'i', ['ν'] = 'v', ['ο'] = 'o',
        ['ρ'] = 'p', ['σ'] = 'o', ['υ'] = 'u', ['ϑ'] = 'O',
        ['ϒ'] = 'Y', ['Ϝ'] = 'F', ['Ϩ'] = '2', ['ϱ'] = 'p',
        ['ϲ'] = 'c', ['ϳ'] = 'j', ['ϴ'] = 'O', ['Ϲ'] = 'C',
        ['Ϻ'] = 'M', ['Ѕ'] = 'S', ['І'] = 'I', ['Ј'] = 'J',
        ['А'] = 'A', ['Б'] = 'b', ['В'] = 'B', ['Е'] = 'E',
        ['З'] = '3', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H',
        ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T',
        ['У'] = 'Y', ['Х'] = 'X', ['Ы'] = 'b', ['Ь'] = 'b',
        ['Ю'] = 'l', ['а'] = 'a', ['б'] = '6', ['г'] = 'r',
        ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c',
        ['у'] = 'y', ['х'] = 'x', ['ѕ'] = 's', ['і'] = 'i',
        ['ј'] = 'j', ['ћ'] = 'h', ['ѡ'] = 'w', ['Ѣ'] = 'b',
        ['ѣ'] = 'b', ['Ѳ'] = 'O', ['ѳ'] = 'o', ['Ѵ'] = 'V',
        ['ѵ'] = 'v', ['ѽ'] = 'w', ['Ҍ'] = 'b', ['ҍ'] = 'b',
        ['ґ'] = 'r', ['ғ'] = 'r', ['Ҙ'] = '3', ['Қ'] = 'K',
        ['Ҟ'] = 'K', ['Ң'] = 'H', ['Ҫ'] = 'C', ['ҫ'] = 'c',
        ['Ҭ'] = 'T', ['Ү'] = 'Y', ['ү'] = 'y', ['Ұ'] = 'Y',
        ['ұ'] = 'y', ['Ҳ'] = 'X', ['һ'] = 'h', ['ҽ'] = 'e',
        ['ҿ'] = 'e', ['Ӏ'] = 'l', ['Ӈ'] = 'H', ['Ӊ'] = 'H',
        ['Ӎ'] = 'M', ['ӏ'] = 'i', ['Ӕ'] = 'A', ['ӕ'] = 'a',
        ['Ӡ'] = '3', ['Ө'] = 'O', ['ө'] = 'o', ['ԁ'] = 'd',
        ['Ԍ'] = 'G', ['ԛ'] = 'q', ['Ԝ'] = 'W', ['ԝ'] = 'w',
        ['Ս'] = 'U', ['Տ'] = 'S', ['Օ'] = 'O', ['ա'] = 'w',
        ['գ'] = 'q', ['զ'] = 'q', ['հ'] = 'h', ['ո'] = 'n',
        ['ռ'] = 'n', ['ս'] = 'u', ['ց'] = 'g', ['ք'] = 'f',
        ['օ'] = 'o', ['٠'] = '0', ['١'] = '1', ['٢'] = '2',
        ['٣'] = '3', ['٤'] = '4', ['٥'] = '5', ['٦'] = '6',
        ['٧'] = '7', ['٨'] = '8', ['٩'] = '9', ['۰'] = '0',
        ['۱'] = '1', ['۲'] = '2', ['۳'] = '3', ['۴'] = '4',
        ['۵'] = '5', ['۶'] = '6', ['۷'] = '7', ['۸'] = '8',
        ['۹'] = '9',
    };

    // Script bit assignments for the single-pass distinct-script mask.
    private const int LatinBit = 1 << 0;
    private const int GreekBit = 1 << 1;
    private const int CyrillicBit = 1 << 2;
    private const int ArmenianBit = 1 << 3;
    private const int FullwidthBit = 1 << 4;
    private const int OtherBit = 1 << 5;
    private const int AllScriptBits = LatinBit | GreekBit | CyrillicBit | ArmenianBit | FullwidthBit | OtherBit;

    private static int ScriptBit(char c)
        => c switch
        {
            >= '\u0370' and <= '\u03FF' => GreekBit,
            >= '\u0400' and <= '\u052F' => CyrillicBit,
            >= '\u0530' and <= '\u058F' => ArmenianBit,
            >= '\uFF00' and <= '\uFFEF' => FullwidthBit,
            <= '\u024F' => LatinBit,
            _ => OtherBit,
        };

    public static IReadOnlyList<ConfusableToken> ScanLine(string line, int lineNumber, Options? options = null)
    {
        var opts = options ?? Options.Default;
        if (opts.MinTokenLength < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum token length must be positive.");

        // ASCII fast path (vectorized): a pure-ASCII line has exactly one script and a
        // skeleton identical to the token, so it can never flag — skip tokenizing entirely.
        if (line.AsSpan().IndexOfAnyExceptInRange((char)0x00, (char)0x7F) < 0)
            return [];

        var tokens = TokenizeRanges(line, out var hasAsciiToken);
        if (opts.RequireAsciiContext && !hasAsciiToken)
            return [];

        var allowedMask = AllowedScriptMask(opts.AllowedScripts);
        List<ConfusableToken>? findings = null;
        foreach (var (start, length, allAscii) in tokens)
        {
            if (allAscii || length < opts.MinTokenLength)
                continue;

            var token = line.AsSpan(start, length);
            var scripts = ScriptMask(token);
            var mixed = BitOperations.PopCount((uint)scripts) > 1 && (scripts & ~allowedMask) != 0;

            // NFKC per token is required for parity on direct (unnormalized) input: fullwidth
            // "ｖａｒ" only reads as the "var" keyword because NFKC maps it to ASCII.
            var tokenText = line.Substring(start, length);
            var skeleton = Skeleton(tokenText);
            var hasKnownSkeleton = !string.Equals(skeleton, tokenText, StringComparison.Ordinal)
                && opts.AllowedAsciiKeywords.Contains(skeleton);

            if (hasKnownSkeleton)
            {
                (findings ??= []).Add(new ConfusableToken(tokenText, lineNumber, start, "confusable keyword", skeleton));
            }
            else if (mixed)
            {
                (findings ??= []).Add(new ConfusableToken(tokenText, lineNumber, start, "mixed-script identifier", skeleton));
            }
            // P2-30 — whole-token lookalike: a single non-Latin script spelling an ASCII-looking
            // identifier evades both rules above (one script, skeleton not a whitelisted keyword).
            // Fire when the skeleton is fully ASCII and differs from the raw token. Prose in one
            // script keeps non-ASCII skeleton chars (unmapped letters), so Greek/Russian text
            // stays quiet; keyword skeletons are already claimed by the confusable-keyword rule.
            else if (!string.Equals(skeleton, tokenText, StringComparison.Ordinal)
                     && skeleton.All(c => c <= 0x7F))
            {
                (findings ??= []).Add(new ConfusableToken(tokenText, lineNumber, start, "whole-token lookalike", skeleton));
            }
        }

        return findings ?? [];
    }

    private static int AllowedScriptMask(IReadOnlySet<string> allowed)
    {
        var mask = 0;
        foreach (var script in allowed)
        {
            mask |= script switch
            {
                "Latin" => LatinBit,
                "Greek" => GreekBit,
                "Cyrillic" => CyrillicBit,
                "Armenian" => ArmenianBit,
                "Fullwidth" => FullwidthBit,
                "Other" => OtherBit,
                _ => 0,
            };
        }

        return mask & AllScriptBits;
    }

    /// <summary>Distinct-script bitmask over the letters of a token — one pass, no allocations.</summary>
    private static int ScriptMask(ReadOnlySpan<char> token)
    {
        var mask = 0;
        foreach (var c in token)
        {
            if (char.IsLetter(c))
            {
                mask |= ScriptBit(c);
            }
        }

        return mask;
    }

    public static bool LooksConfusable(string token, Options? options, out string? asciiSkeleton)
    {
        ArgumentNullException.ThrowIfNull(token);
        options ??= Options.Default;
        asciiSkeleton = Skeleton(token);
        return token.Length >= options.MinTokenLength
            && !string.Equals(token, asciiSkeleton, StringComparison.Ordinal)
            && asciiSkeleton.All(c => c <= 0x7F);
    }

    /// <summary>Token ranges with an inline all-ASCII flag — substrings are materialized only
    /// for tokens that flag (P2-27).</summary>
    private static List<(int Start, int Length, bool AllAscii)> TokenizeRanges(string line, out bool hasAsciiToken)
    {
        hasAsciiToken = false;
        var tokens = new List<(int, int, bool)>();
        var start = -1;
        var tokenHasNonAscii = false;
        for (var i = 0; i <= line.Length; i++)
        {
            var isToken = i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
            if (isToken)
            {
                if (start < 0)
                {
                    start = i;
                    tokenHasNonAscii = false;
                }

                if (line[i] > 0x7F)
                {
                    tokenHasNonAscii = true;
                }
            }
            else if (start >= 0)
            {
                tokens.Add((start, i - start, !tokenHasNonAscii));
                hasAsciiToken |= !tokenHasNonAscii;
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
}
