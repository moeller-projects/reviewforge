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

        var ordered = DiffBlockSplit.Split(unifiedDiff, out var unresolvableHunks)
            .Where(b => b is { File: not null, IsDeletion: false })
            .Select(b => (File: b.File!, b.Text))
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

    // Block splitting lives in DiffBlockSplit (shared with the repo_file_diff agent tool);
    // deletion-only and preamble blocks are filtered out above — shards are unchanged.
}
