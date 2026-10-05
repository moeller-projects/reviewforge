using ReviewForge.Core.Analysis;

namespace ReviewForge.Core.Domain;


public static class ResolvePlanner
{
    public static ResolvePlan Plan(
        IReadOnlyList<ResolvableComment> comments,
        IReadOnlyList<ThreadVerdict> verdicts,
        IReadOnlyCollection<string> changedFiles,
        int maxThreads,
        int maxWritableFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxThreads);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWritableFiles);
        var byId = comments.ToDictionary(c => c.ThreadId);
        var latestVerdicts = verdicts.GroupBy(v => v.ThreadId).ToDictionary(g => g.Key, g => g.Last());
        var changed = changedFiles.Select(RepoPath.Normalize).ToHashSet(RepoPath.PathComparer);
        var fixes = new List<PlannedFix>();
        var deferred = new List<(int ThreadId, string Reason)>();
        var writable = new HashSet<string>(RepoPath.PathComparer);
        var eligible = latestVerdicts.Values
            .Where(v => v.Verdict == TriageVerdict.Actionable
                        && byId.TryGetValue(v.ThreadId, out var comment)
                        && comment.CommenterAllowed
                        && comment.Anchor is not null
                        && changed.Contains(RepoPath.Normalize(comment.Anchor.FilePath)))
            .GroupBy(v => RepoPath.Normalize(byId[v.ThreadId].Anchor!.FilePath), RepoPath.PathComparer)
            .OrderBy(g => g.Key, RepoPath.PathComparer);
        var remainingThreads = maxThreads;
        foreach (var group in eligible)
        {
            var ordered = group.OrderBy(v => v.ThreadId).ToArray();
            if (writable.Count >= maxWritableFiles)
            {
                deferred.AddRange(ordered.Select(v => (v.ThreadId, "writable-file budget")));
                continue;
            }
            var selected = ordered.Take(remainingThreads).ToArray();
            deferred.AddRange(ordered.Skip(selected.Length).Select(v => (v.ThreadId, "thread budget")));
            if (selected.Length == 0) continue;
            var first = byId[selected[0].ThreadId];
            var ids = selected.Select(v => v.ThreadId).ToArray();
            var request = string.Join("\n\n", selected.Select(v => $"Thread #{v.ThreadId}: {byId[v.ThreadId].RequestText}"));
            var evidence = string.Join("\n", selected.Select(v => $"Thread #{v.ThreadId}: {v.Evidence}"));
            fixes.Add(new PlannedFix(ids[0], first.Anchor!, request, evidence, [group.Key], ids));
            writable.Add(group.Key);
            remainingThreads -= ids.Length;
        }
        return new ResolvePlan(fixes, writable, deferred);
    }
}
