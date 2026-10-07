using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline;

/// <summary>Outputs fetched from the pull-request host and finding store.</summary>
public sealed record FetchOutcome
{
    private readonly IReadOnlyList<ChangedFile> _ChangedFileManifest = [];
    private IReadOnlyList<string>? _ChangedFiles;

    public PullRequest? PullRequest { get; init; }
    public IReadOnlyList<WorkItem> WorkItems { get; init; } = [];
    public IReadOnlyList<ReviewThread> Threads { get; init; } = [];

    public IReadOnlyList<ChangedFile> ChangedFileManifest
    {
        get => _ChangedFileManifest;
        init
        {
            _ChangedFileManifest = value;
            _ChangedFiles = null;
        }
    }

    public IReadOnlyList<string> ChangedFiles
        => _ChangedFiles ??= ChangedFileManifest.Select(file => file.Path).ToArray();

    public CurrentUser? CurrentUser { get; init; }
    public PriorRun? PriorRun { get; init; }
    public IReadOnlySet<string> ResolvedKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}