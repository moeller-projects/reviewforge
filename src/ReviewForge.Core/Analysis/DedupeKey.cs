using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReviewForge.Core.Analysis;

/// <summary>
/// Shift-proof dedupe key: ruleId + file + normalized snippet — deliberately NOT the
/// absolute line. Lines rot on every force-push; content does not. The line is kept on
/// the finding as a hint for posting, not as identity.
/// </summary>
public static class DedupeKey
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Compute(string ruleId, string filePath, string? snippet)
    {
        var input = string.Join('|',
            ruleId.Trim().ToLowerInvariant(),
            filePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant(),
            NormalizeSnippet(snippet));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();
    }

    /// <summary>Whitespace- and casing-insensitive snippet identity.</summary>
    public static string NormalizeSnippet(string? snippet)
        => snippet is null ? "-" : Whitespace.Replace(snippet.Trim(), " ").ToLowerInvariant();
}