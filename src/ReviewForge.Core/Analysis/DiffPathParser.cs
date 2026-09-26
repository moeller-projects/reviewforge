using System.Text;

namespace ReviewForge.Core.Analysis;

/// <summary>
/// Parses git path tokens as they appear in unified-diff headers. Git (and libgit2-fed
/// exports) C-style-quote any path containing '"', '\', control characters, or, with the
/// default core.quotePath, non-ASCII bytes: <c>"b/caf\303\251.cs"</c>. This parser decodes
/// both bare and quoted tokens per git's quote.c rules — \" \\ \a \b \f \n \r \t \v and
/// \NNN octal bytes reassembled as UTF-8. Decoding never throws: malformed input reports
/// false so the run fails closed at the prepare-repository scope guard instead of
/// mis-attributing hunk content or silently shrinking review scope.
/// </summary>
internal static class DiffPathParser
{
    /// <summary>Decodes one path token (bare: the whole span; quoted: must end at the closing quote).</summary>
    public static bool TryDecodeToken(ReadOnlySpan<char> token, out string path)
    {
        path = string.Empty;
        if (token.IsEmpty)
        {
            return false;
        }

        if (token[0] != '"')
        {
            // Git quotes every path containing spaces, so a bare token with a space is
            // malformed — accepting it would silently truncate the path.
            if (token.IndexOf(' ') >= 0)
            {
                return false;
            }

            path = token.ToString();
            return true;
        }

        var inner = token[1..];
        // Fast path: no escapes and pure ASCII — the closing quote is the final char.
        if (inner.IndexOf('\\') < 0 && inner.IndexOf('"') == inner.Length - 1
            && inner[..^1].IndexOfAnyInRange('\u0080', '\uFFFF') < 0)
        {
            path = inner[..^1].ToString();
            return true;
        }

        var bytes = new List<byte>(inner.Length);
        var i = 0;
        Span<byte> charBytes = stackalloc byte[4];
        while (i < inner.Length)
        {
            var c = inner[i];
            if (c == '"')
            {
                // The closing quote must terminate the token; anything after is not a header we understand.
                if (i != inner.Length - 1)
                {
                    path = string.Empty;
                    return false;
                }

                path = Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes));
                return true;
            }

            if (c == '\\')
            {
                i++;
                if (i >= inner.Length || !TryDecodeEscape(inner, ref i, out var escaped))
                {
                    path = string.Empty;
                    return false;
                }

                bytes.Add(escaped);
                continue;
            }

            var n = Encoding.UTF8.GetBytes(new ReadOnlySpan<char>(in c), charBytes);
            for (var b = 0; b < n; b++)
            {
                bytes.Add(charBytes[b]);
            }

            i++;
        }

        // Unterminated quote.
        return false;
    }

    /// <summary>Reads one token from the start of span: quoted runs to the closing unescaped
    /// quote, bare runs to the first space or the end of span.</summary>
    public static bool TryReadToken(ReadOnlySpan<char> span, out ReadOnlySpan<char> token, out int consumed)
    {
        token = default;
        consumed = 0;
        if (span.IsEmpty)
        {
            return false;
        }

        if (span[0] != '"')
        {
            var end = span.IndexOf(' ');
            token = end < 0 ? span : span[..end];
            consumed = token.Length;
            return token.Length > 0;
        }

        var i = 1;
        while (i < span.Length)
        {
            if (span[i] == '\\')
            {
                i += 2; // skip the escaped char; correctness of the escape is TryDecodeToken's job
                continue;
            }

            if (span[i] == '"')
            {
                token = span[..(i + 1)];
                consumed = i + 1;
                return true;
            }

            i++;
        }

        return false; // unterminated
    }

    /// <summary>Strips the b/ side prefix from a decoded diff path token.</summary>
    public static bool TryStripBPrefix(string decoded, out string repoRelative)
    {
        if (decoded.StartsWith("b/", StringComparison.Ordinal) && decoded.Length > 2)
        {
            repoRelative = decoded[2..];
            return true;
        }

        repoRelative = string.Empty;
        return false;
    }

    private static bool TryDecodeEscape(ReadOnlySpan<char> inner, ref int i, out byte value)
    {
        value = 0;
        var c = inner[i];
        switch (c)
        {
            case 'a': value = 0x07; i++; return true;
            case 'b': value = 0x08; i++; return true;
            case 'f': value = 0x0C; i++; return true;
            case 'n': value = 0x0A; i++; return true;
            case 'r': value = 0x0D; i++; return true;
            case 't': value = 0x09; i++; return true;
            case 'v': value = 0x0B; i++; return true;
            case '\\': value = 0x5C; i++; return true;
            case '"': value = 0x22; i++; return true;
        }

        if (c is < '0' or > '7')
        {
            return false;
        }

        var v = 0;
        var digits = 0;
        while (digits < 3 && i < inner.Length && inner[i] is >= '0' and <= '7')
        {
            v = v * 8 + (inner[i] - '0');
            i++;
            digits++;
        }

        value = (byte)v;
        return true;
    }
}
