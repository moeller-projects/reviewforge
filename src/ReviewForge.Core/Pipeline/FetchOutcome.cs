using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline;

/// <summary>Outputs fetched from the pull-request host and finding store.</summary>
public sealed record FetchOutcome
{
    private IReadOnlyList<ChangedFile> _changedFileManifest = [];
    private IReadOnlyList<string>? _changedFiles;

    public PullRequest? PullRequest { get; init; }
    public IReadOnlyList<WorkItem> WorkItems { get; init; } = [];
    public IReadOnlyList<ReviewThread> Threads { get; init; } = [];
    public IReadOnlyList<ChangedFile> ChangedFileManifest
    {
        get => _changedFileManifest;
        init
        {
            _changedFileManifest = value;
            _changedFiles = null;
        }
    }
    public IReadOnlyList<string> ChangedFiles
        => _changedFiles ??= ChangedFileManifest.Select(file => file.Path).ToArray();
    public CurrentUser? CurrentUser { get; init; }
    public PriorRun? PriorRun { get; init; }
    public IReadOnlySet<string> ResolvedKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
