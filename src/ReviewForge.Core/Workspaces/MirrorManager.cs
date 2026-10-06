using System.Collections.Concurrent;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Workspaces;

/// <summary>Owns shared-mirror paths, warmup operations, and one-shot warmup attribution.</summary>
internal sealed class MirrorManager
{
    private readonly IGitOps _Git;
    private readonly IWorkspaceFs _Fs;
    private readonly string _Root;
    private readonly string? _Pat;

    // Heads whose mirror prefetch completed during a discovery sweep. The next acquire tags
    // its duration measurement with "warmed" (one-shot, TryRemove) so warmup wins are measurable.
    private readonly ConcurrentDictionary<string, byte> _WarmedHeads = new(StringComparer.Ordinal);

    public MirrorManager(IGitOps git, IWorkspaceFs fs, string root, string? pat)
    {
        _Git = git;
        _Fs = fs;
        _Root = root;
        _Pat = pat;
        _Fs.CreateDirectory(Path.Combine(_Root, "mirror"));
    }

    public bool TryTakeWarmedHead(string repositoryId, string headSha)
        => _WarmedHeads.TryRemove(RepoCheckoutPool.CheckoutKey(repositoryId, headSha), out _);

    public Task<TipCommitInfo?> GetCommitInfoAsync(string repositoryId, string headSha, CancellationToken ct)
    {
        var mirror = MirrorPath(repositoryId);
        return _Fs.DirectoryExists(mirror)
            ? _Git.GetCommitInfoAsync(mirror, headSha, ct)
            : Task.FromResult<TipCommitInfo?>(null);
    }

    public Task WarmupAsync(string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct)
        => _Git.WarmupMirrorAsync(MirrorPath(repositoryId), cloneUrl, baseSha, headSha, _Pat, ct);

    public void MarkWarmed(string repositoryId, string headSha)
        => _WarmedHeads[RepoCheckoutPool.CheckoutKey(repositoryId, headSha)] = 1;

    internal string MirrorPath(string repositoryId)
        => Path.Combine(_Root, "mirror", RepoCheckoutPool.KeyComponent(repositoryId));
}
