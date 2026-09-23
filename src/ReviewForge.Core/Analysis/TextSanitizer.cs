using System.Buffers;

namespace ReviewForge.Core.Analysis;

/// <summary>Removes explicitly unsafe invisible and bidirectional characters from review text.</summary>
public static class TextSanitizer
{
    private static readonly SearchValues<char> ForbiddenBmpSingles = SearchValues.Create(
    [
        '\u00AD', // soft hyphen
        '\u034F', // combining grapheme joiner
        '\u115F', '\u1160', // Hangul fillers
        '\u180E', // Mongolian vowel separator
        '\u200B', '\u200C', '\u200D', '\u200E', '\u200F', // zero-width/bidi marks
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', // bidi embeddings/override
        '\u2060', '\u2061', '\u2062', '\u2063', '\u2064', // word joiner/invisible operators
        '\u2066', '\u2067', '\u2068', '\u2069', // bidi isolates
        '\u2800', // Braille pattern blank
        '\u3164', // Hangul filler
        '\uFFA0', // halfwidth Hangul filler
        '\uFEFF' // BOM/zero-width no-break space
    ]);

    private const char VariationSelectorLo = '\uFE00';
    private const char VariationSelectorHi = '\uFE0F';
    private const int TagsLo = 0xE0000;
    private const int TagsHi = 0xE007F;
    private const int VariationSelectorsSupplementLo = 0xE0100;
    private const int VariationSelectorsSupplementHi = 0xE01EF;

    /// <summary>
    /// Removes invisible and bidi-smuggling characters. Returns the original instance when
    /// nothing is stripped, so clean input takes no allocation. Unpaired surrogates pass through.
    /// </summary>
    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var firstBad = IndexOfForbidden(input);
        if (firstBad < 0)
        {
            return input;
        }

        var kept = CountClean(input, firstBad);
        return string.Create(kept, (input, firstBad), static (destination, state) =>
        {
            var (source, start) = state;
            source.AsSpan(0, start).CopyTo(destination);
            var written = start;
            for (var i = start; i < source.Length; i++)
            {
                var c = source[i];
                if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    var codePoint = char.ConvertToUtf32(c, source[i + 1]);
                    if (IsForbiddenAstral(codePoint))
                    {
                        i++;
                        continue;
                    }

                    destination[written++] = c;
                    destination[written++] = source[++i];
                }
                else if (!IsForbiddenBmp(c))
                {
                    destination[written++] = c;
                }
            }
        });
    }

    /// <summary>Returns whether <paramref name="input"/> contains a character removed by <see cref="Sanitize"/>.</summary>
    public static bool ContainsInvisible(string? input)
        => !string.IsNullOrEmpty(input) && IndexOfForbidden(input) >= 0;

    private static int IndexOfForbidden(string input)
    {
        var span = input.AsSpan();
        var candidate = span.IndexOfAny(ForbiddenBmpSingles);
        var variation = span.IndexOfAnyInRange(VariationSelectorLo, VariationSelectorHi);
        if (variation >= 0 && (candidate < 0 || variation < candidate))
        {
            candidate = variation;
        }

        for (var i = 0; i < span.Length; i++)
        {
            if (i == candidate)
            {
                return i;
            }

            if (!char.IsHighSurrogate(span[i]) || i + 1 >= span.Length || !char.IsLowSurrogate(span[i + 1]))
            {
                continue;
            }

            if (IsForbiddenAstral(char.ConvertToUtf32(span[i], span[i + 1])))
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    private static bool IsForbiddenBmp(char c)
        => ForbiddenBmpSingles.Contains(c) || c is >= VariationSelectorLo and <= VariationSelectorHi;

    private static bool IsForbiddenAstral(int codePoint)
        => codePoint is >= TagsLo and <= TagsHi
            or >= VariationSelectorsSupplementLo and <= VariationSelectorsSupplementHi;

    private static int CountClean(string input, int firstBad)
    {
        var kept = firstBad;
        for (var i = firstBad; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                if (!IsForbiddenAstral(char.ConvertToUtf32(c, input[i + 1])))
                {
                    kept += 2;
                }

                i++;
            }
            else if (!IsForbiddenBmp(c))
            {
                kept++;
            }
        }

        return kept;
    }
}
