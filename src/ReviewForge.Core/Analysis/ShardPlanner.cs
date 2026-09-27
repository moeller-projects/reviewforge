using System.Text;

namespace ReviewForge.Core.Analysis;

/// <summary>One shard of a large diff: the files it names and their unified diff slice.</summary>
public sealed record DiffShard(IReadOnlyList<string> Files, string DiffText);

/// <summary>Result of shard planning. <see cref="Overflowed"/> marks a shard cap overflow —
/// the caller must fall back to the legacy single-agent path, never fail the run.</summary>
public sealed record ShardPlan(IReadOnlyList<DiffShard> Shards, bool Overflowed);

/// <summary>
/// Pure map-reduce shard planning over a unified diff. Files are sorted by normalized path
/// (ordinal) and greedily bucketed by cumulative diff size; a file larger than the budget
/// gets a shard of its own and is never split. Same input → identical shards, so plans are
/// deterministic across runs and hosts.
/// </summary>
public static class ShardPlanner
{
    public static ShardPlan Plan(string unifiedDiff, int shardMaxChars, int maxShards)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardMaxChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxShards, 1);

        var ordered = SplitBlocks(unifiedDiff, out var unresolvableHunks)
            .OrderBy(b => RepoPath.Normalize(b.File), StringComparer.Ordinal)
            .ToList();

        // Unattributable hunks must never be silently sharded away: fail closed to the
        // legacy whole-diff path, same as a shard-cap overflow.
        var overflowed = unresolvableHunks;

        var shards = new List<DiffShard>();
        var currentFiles = new List<string>();
        var current = new StringBuilder();

        void CloseCurrent()
        {
            shards.Add(new DiffShard(currentFiles.ToArray(), current.ToString()));
            currentFiles = [];
            current.Clear();
        }

        foreach (var block in ordered)
        {
            if (block.Text.Length > shardMaxChars)
            {
                // Oversize file: its own shard, never split.
                if (currentFiles.Count > 0)
                {
                    CloseCurrent();
                }

                if (shards.Count >= maxShards)
                {
                    overflowed = true;
                    break;
                }

                shards.Add(new DiffShard([block.File], block.Text));
                continue;
            }

            if (currentFiles.Count > 0 && current.Length + block.Text.Length > shardMaxChars)
            {
                CloseCurrent();
            }

            if (shards.Count >= maxShards)
            {
                overflowed = true;
                break;
            }

            currentFiles.Add(block.File);
            current.Append(block.Text);
        }

        if (!overflowed && currentFiles.Count > 0)
        {
            CloseCurrent();
        }

        // A single shard is not sharding — callers take the exact legacy single-agent path.
        return new ShardPlan(shards, overflowed);
    }

    /// <summary>Splits a unified diff into per-file blocks keyed by destination path. Paths
    /// may be bare or C-style-quoted, resolved via <see cref="DiffPathParser"/> exactly like
    /// <see cref="DiffIndex"/>. Blocks with no resolvable file (preamble, deletion-only files
    /// with <c>+++ /dev/null</c>) are dropped — they carry no reviewable added lines. A
    /// hunk-bearing block whose file cannot be resolved marks the plan overflowed so the
    /// caller fails closed to the legacy whole-diff path instead of silently dropping it.</summary>
    private static List<(string File, string Text)> SplitBlocks(string unifiedDiff, out bool unresolvableHunks)
    {
        var unattributable = false; // captured by Flush; copied to the out param at the end
        unresolvableHunks = false;
        var blocks = new List<(string File, string Text)>();
        if (string.IsNullOrEmpty(unifiedDiff))
        {
            return blocks;
        }

        var current = new StringBuilder();
        string? file = null;
        var hasAddedLine = false;
        void Flush()
        {
            if (file is not null)
            {
                blocks.Add((file, current.ToString()));
            }
            else if (hasAddedLine)
            {
                // Added content we cannot attribute to a file (deletion-only blocks carry
                // no '+' lines and are dropped by design): never shard it away silently.
                unattributable = true;
            }

            current.Clear();
            file = null;
            hasAddedLine = false;
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
                file = null; // deletion-only block: carries no reviewable added lines
                continue;
            }

            if (file is null && line.StartsWith("+++ ", StringComparison.Ordinal))
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
