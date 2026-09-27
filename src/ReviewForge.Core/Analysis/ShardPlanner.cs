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

        var ordered = SplitBlocks(unifiedDiff)
            .OrderBy(b => RepoPath.Normalize(b.File), StringComparer.Ordinal)
            .ToList();

        var shards = new List<DiffShard>();
        var currentFiles = new List<string>();
        var current = new StringBuilder();
        var overflowed = false;

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

    /// <summary>Splits a unified diff into per-file blocks keyed by destination path. Blocks
    /// with no resolvable file (preamble, deletion-only files with <c>+++ /dev/null</c>) are
    /// dropped — they carry no reviewable added lines.</summary>
    private static List<(string File, string Text)> SplitBlocks(string unifiedDiff)
    {
        var blocks = new List<(string File, string Text)>();
        if (string.IsNullOrEmpty(unifiedDiff))
        {
            return blocks;
        }

        var current = new StringBuilder();
        string? file = null;
        void Flush()
        {
            if (file is not null)
            {
                blocks.Add((file, current.ToString()));
            }

            current.Clear();
            file = null;
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

            if (file is null && line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                file = line["+++ b/".Length..];
            }

            current.Append(line).Append('\n');
        }

        Flush();
        return blocks;
    }

    /// <summary>Extracts the b-side path from a "diff --git a/x b/y" line; null when unresolvable
    /// (then a later +++ b/ line inside the block provides the key).</summary>
    private static string? FileFromDiffGitLine(string line)
    {
        const string marker = " b/";
        var idx = line.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0 || idx + marker.Length >= line.Length)
        {
            return null;
        }

        var path = line[(idx + marker.Length)..];
        return path.Length > 0 && path != "/dev/null" ? path : null;
    }
}
