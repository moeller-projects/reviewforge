using System.Text;

namespace ReviewForge.Core.Analysis;

/// <summary>One per-file block of a unified diff, keyed by destination path.</summary>
public readonly record struct DiffBlock(string? File, string Text, bool IsDeletion);

/// <summary>
/// Lossless splitter for unified diffs: every block is retained — the preamble carries
/// <see cref="DiffBlock.File"/> <c>null</c>, deletion-only blocks (<c>+++ /dev/null</c>)
/// are retained with <see cref="DiffBlock.IsDeletion"/> <c>true</c>. Paths are resolved via
/// <see cref="DiffPathParser"/> exactly like <see cref="DiffIndex"/> (bare or C-style-quoted).
/// </summary>
public static class DiffBlockSplit
{
    /// <summary>Splits a unified diff into per-file blocks. A hunk-bearing block whose file
    /// cannot be resolved sets <paramref name="unresolvableHunks"/> so the caller can fail
    /// closed instead of silently dropping it.</summary>
    public static IReadOnlyList<DiffBlock> Split(string unifiedDiff, out bool unresolvableHunks)
    {
        var unattributable = false; // captured by Flush; copied to the out param at the end
        unresolvableHunks = false;
        var blocks = new List<DiffBlock>();
        if (string.IsNullOrEmpty(unifiedDiff))
        {
            return blocks;
        }

        var current = new StringBuilder();
        string? file = null;
        var hasAddedLine = false;
        var isDeletion = false;
        void Flush()
        {
            if (file is not null || current.Length > 0)
            {
                if (file is null && hasAddedLine)
                {
                    // Added content we cannot attribute to a file: never shard it away silently.
                    unattributable = true;
                }

                blocks.Add(new DiffBlock(file, current.ToString(), isDeletion));
            }

            current.Clear();
            file = null;
            hasAddedLine = false;
            isDeletion = false;
        }

        var lines = unifiedDiff.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i == lines.Length - 1 && line.Length == 0)
            {
                continue; // trailing-newline artifact of Split — not a diff line
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                current.Append(line).Append('\n');
                file = FileFromDiffGitLine(line);
                continue;
            }

            if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                isDeletion = true; // deletion-only block: retained, flagged, not dropped
            }

            if (file is null && !isDeletion && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                // "+++ b/path" or the C-quoted "+++ \"b/path with space\"" form.
                if (DiffPathParser.TryDecodeToken(line.AsSpan("+++ ".Length), out var decoded)
                    && DiffPathParser.TryStripBPrefix(decoded, out var rel))
                {
                    file = rel;
                }
            }

            if (line.Length > 0 && line[0] == '+' && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                hasAddedLine = true;
            }

            current.Append(line).Append('\n');
        }

        Flush();
        unresolvableHunks = unattributable;
        return blocks;
    }

    /// <summary>Extracts the b-side path from a "diff --git a/x b/y" line (bare or C-quoted);
    /// null when unresolvable (then a later +++ b/ line inside the block provides the key).</summary>
    private static string? FileFromDiffGitLine(string line)
    {
        const string prefix = "diff --git ";
        var rest = line.AsSpan(prefix.Length);
        if (!DiffPathParser.TryReadToken(rest, out _, out var consumed))
        {
            return null;
        }

        rest = rest[consumed..].TrimStart(' ');
        if (!DiffPathParser.TryReadToken(rest, out var token, out _)
            || !DiffPathParser.TryDecodeToken(token, out var decoded)
            || decoded == "b/dev/null"
            || !DiffPathParser.TryStripBPrefix(decoded, out var rel)
            || rel.Length == 0)
        {
            return null;
        }

        return rel;
    }
}
