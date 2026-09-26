using System.Security.Cryptography;
using System.Text;

namespace ReviewForge.Core.Analysis;

/// <summary>Content addressing for single lines: first 8 lowercase hex chars of
/// SHA256 over the UTF-8 bytes of the normalized line. Normalization strips the line
/// terminator, trailing whitespace and a leading BOM; leading indentation is preserved
/// (indentation is content). Pure; no IO.</summary>
public static class HashLine
{
    public const int HashLength = 8;

    private const string HexDigits = "0123456789abcdef";

    /// <summary>Hash of one content line. The input may still carry its terminator —
    /// normalization removes it (plus trailing whitespace and a leading BOM).</summary>
    public static string Of(string lineWithoutTerminator)
    {
        var normalized = Normalize(lineWithoutTerminator);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return string.Create(HashLength, bytes, static (span, hash) =>
        {
            for (var i = 0; i < HashLength; i++)
            {
                var b = hash[i >> 1];
                span[i] = (i & 1) == 0 ? HexDigits[b >> 4] : HexDigits[b & 0xF];
            }
        });
    }

    /// <summary>Normalization for hashing: strip trailing whitespace (including any line
    /// terminator) and one leading BOM. Indentation and interior content are preserved.</summary>
    internal static string Normalize(string line)
        => line.TrimEnd().TrimStart('\uFEFF');

    /// <summary>Dominant line ending ("\r\n" or "\n"); "\n" when no terminators present.</summary>
    public static string DetectNewLine(IReadOnlyList<string> rawLinesWithEndings)
    {
        var crlf = 0;
        var lf = 0;
        foreach (var raw in rawLinesWithEndings)
        {
            if (raw.EndsWith("\r\n", StringComparison.Ordinal))
            {
                crlf++;
            }
            else if (raw.EndsWith("\n", StringComparison.Ordinal))
            {
                lf++;
            }
        }

        return crlf > 0 && crlf >= lf ? "\r\n" : "\n";
    }
}
