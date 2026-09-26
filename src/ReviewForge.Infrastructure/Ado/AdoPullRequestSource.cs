using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using WitModels = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using AdoComment = Microsoft.TeamFoundation.SourceControl.WebApi.Comment;
using AdoCommentType = Microsoft.TeamFoundation.SourceControl.WebApi.CommentType;
using AdoThreadStatus = Microsoft.TeamFoundation.SourceControl.WebApi.CommentThreadStatus;

namespace ReviewForge.Infrastructure.Ado;

/// <summary>
/// Azure DevOps adapter. Thin mapping layer over the ADO SDK — excluded from coverage
/// by design; behavior is verified against a live organization, logic lives in Core.
/// Bot-authored finding threads carry their dedupe key in thread Properties.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class AdoPullRequestSource : IPullRequestSource
{
    public const string DedupeKeyProperty = "ReviewForge.DedupeKey";

    private readonly VssConnection _Connection;
    private readonly string _Org;
    private readonly string _Project;
    private readonly TransientRetryPolicy _Retry;

    public AdoPullRequestSource(
        AdoOptions options,
        ILogger<AdoPullRequestSource>? logger = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Pat))
        {
            throw new InvalidOperationException(
                $"ADO PAT missing — set the {AdoOptions.PatEnvironmentVariable} environment variable.");
        }

        _Project = options.Project;
        _Org = OrgFromUrl(options.OrgUrl);
        _Connection = new VssConnection(
            new Uri(options.OrgUrl),
            new VssBasicCredential(string.Empty, options.Pat));
        _Retry = new TransientRetryPolicy(
            options.Retry, AdoTransientErrors.IsTransient, AdoTransientErrors.ProbeRetryAfter, logger, clock);
    }

    public async Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
    {
        var gpr = await _Retry.ExecuteAsync(
            async attemptCt =>
            {
                var git = await GitClientAsync(attemptCt).ConfigureAwait(false);
                return await git.GetPullRequestAsync(
                    pr.Project,
                    pr.RepositoryId,
                    pr.PrId,
                    cancellationToken: attemptCt).ConfigureAwait(false);
            },
            $"GetPullRequest({pr.PrId})",
            ct).ConfigureAwait(false);

        return new PullRequest(
            gpr.PullRequestId,
            gpr.Title ?? string.Empty,
            gpr.Description,
            gpr.LastMergeSourceCommit?.CommitId ?? string.Empty,
            gpr.LastMergeTargetCommit?.CommitId ?? string.Empty,
            gpr.Repository?.RemoteUrl ?? string.Empty,
            gpr.IsDraft ?? false,
            gpr.CreatedBy?.Id.ToString() ?? string.Empty,
            gpr.CreatedBy?.DisplayName ?? string.Empty);
    }

    public async Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
    {
        var projectClient = await _Connection.GetClientAsync<ProjectHttpClient>(ct).ConfigureAwait(false);
        var projects = await _Retry.ExecuteAsync(
            _ => projectClient.GetProjects(),
            "GetProjects",
            ct).ConfigureAwait(false);
        var git = await _Connection.GetClientAsync<GitHttpClient>(ct).ConfigureAwait(false);
        var bag = new ConcurrentBag<(int Index, List<PullRequestCandidate> Candidates)>();

        await Parallel.ForEachAsync(
            projects.Select((project, index) => (project, index)),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (item, token) =>
            {
                var prs = await _Retry.ExecuteAsync(
                    attemptCt => git.GetPullRequestsByProjectAsync(
                        item.project.Name,
                        new GitPullRequestSearchCriteria { Status = PullRequestStatus.Active },
                        cancellationToken: attemptCt),
                    $"GetPullRequests({item.project.Name})",
                    token).ConfigureAwait(false);

                var list = new List<PullRequestCandidate>(prs.Count);
                foreach (var gpr in prs)
                {
                    var repositoryId = gpr.Repository?.Id.ToString();
                    if (repositoryId is null)
                    {
                        continue;
                    }

                    var key = new PrKey(_Org, item.project.Name, repositoryId, gpr.PullRequestId);
                    var pr = new PullRequest(
                        gpr.PullRequestId,
                        gpr.Title ?? string.Empty,
                        gpr.Description,
                        gpr.LastMergeSourceCommit?.CommitId ?? string.Empty,
                        gpr.LastMergeTargetCommit?.CommitId ?? string.Empty,
                        gpr.Repository?.RemoteUrl ?? string.Empty,
                        gpr.IsDraft ?? false,
                        gpr.CreatedBy?.Id.ToString() ?? string.Empty,
                        gpr.CreatedBy?.DisplayName ?? string.Empty);

                    list.Add(new PullRequestCandidate(
                        key,
                        pr,
                        StripRefs(gpr.TargetRefName),
                        gpr.CreatedBy?.Id.ToString() ?? string.Empty,
                        gpr.CreatedBy?.DisplayName ?? string.Empty));
                }

                bag.Add((item.index, list));
            });

        return [.. bag.OrderBy(b => b.Index).SelectMany(b => b.Candidates)];
    }

    public async Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(PrKey pr, CancellationToken ct)
    {
        var git = await GitClientAsync(ct).ConfigureAwait(false);
        var refs = await _Retry.ExecuteAsync(
            attemptCt => git.GetPullRequestWorkItemRefsAsync(pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: attemptCt),
            $"GetWorkItemRefs({pr.PrId})",
            ct).ConfigureAwait(false);
        var ids = refs.Select(r => int.Parse(r.Id)).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var wit = await _Connection.GetClientAsync<WorkItemTrackingHttpClient>(ct).ConfigureAwait(false);
        var items = await _Retry.ExecuteAsync(
            attemptCt => wit.GetWorkItemsAsync(ids, expand: WitModels.WorkItemExpand.All, cancellationToken: attemptCt),
            $"GetWorkItems({pr.PrId})",
            ct).ConfigureAwait(false);

        return
        [
            .. items.Select(wi => new WorkItem(
                wi.Id ?? 0,
                Field(wi, "System.Title"),
                Field(wi, "System.WorkItemType"),
                FieldOrNull(wi, "System.Description"),
                FieldOrNull(wi, "Microsoft.VSTS.Common.AcceptanceCriteria"),
                Field(wi, "System.State")))
        ];

        static string Field(WitModels.WorkItem wi, string name)
            => wi.Fields.TryGetValue(name, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

        static string? FieldOrNull(WitModels.WorkItem wi, string name)
            => wi.Fields.TryGetValue(name, out var value) ? value?.ToString() : null;
    }

    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PrKey pr, CancellationToken ct)
    {
        var git = await GitClientAsync(ct).ConfigureAwait(false);
        var iterations = await _Retry.ExecuteAsync(
            attemptCt => git.GetPullRequestIterationsAsync(pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: attemptCt),
            $"GetIterations({pr.PrId})",
            ct).ConfigureAwait(false);
        var latest = iterations.MaxBy(i => i.Id ?? 0);
        if (latest?.Id is not { } iterationId)
        {
            return [];
        }

        var changes = await _Retry.ExecuteAsync(
            attemptCt => git.GetPullRequestIterationChangesAsync(pr.Project, pr.RepositoryId, pr.PrId, iterationId, cancellationToken: attemptCt),
            $"GetIterationChanges({pr.PrId})",
            ct).ConfigureAwait(false);
        return
        [
            .. changes.ChangeEntries
                .Select(c => (Path: c.Item?.Path?.TrimStart('/'), Type: Convert.ToString(c.ChangeType, CultureInfo.InvariantCulture)))
                .Where(c => !string.IsNullOrWhiteSpace(c.Path))
                .Select(c => new ChangedFile(c.Path!, ParseChangeType(c.Type)))
        ];
    }

    public async Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(PrKey pr, CancellationToken ct)
    {
        var git = await GitClientAsync(ct).ConfigureAwait(false);
        var botId = (await _Retry.ExecuteAsync(
            attemptCt => CurrentIdentityAsync(attemptCt),
            "CurrentIdentity",
            ct).ConfigureAwait(false)).Id;
        var threads = await _Retry.ExecuteAsync(
            attemptCt => git.GetThreadsAsync(pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: attemptCt),
            $"GetThreads({pr.PrId})",
            ct).ConfigureAwait(false);

        return
        [
            .. threads
                .Where(t => !t.IsDeleted)
                .Select(t => new ReviewThread(
                    t.Id,
                    t.Properties?.TryGetValue(DedupeKeyProperty, out var key) == true ? key?.ToString() : null,
                    MapStatus(t.Status),
                    [
                        .. t.Comments
                            .Where(c => !c.IsDeleted)
                            .OrderBy(c => c.PublishedDate)
                            .Select(c => new ThreadComment(
                                c.Author?.Id.ToString() ?? string.Empty,
                                c.Author?.DisplayName ?? "unknown",
                                string.Equals(c.Author?.Id.ToString(), botId, StringComparison.OrdinalIgnoreCase),
                                c.Content ?? string.Empty,
                                AdoTime.ToUtc(c.PublishedDate)))
                    ],
                    t.ThreadContext?.FilePath is { } ctxPath
                        ? new ThreadAnchor(
                            ctxPath.TrimStart('/'),
                            t.ThreadContext.RightFileStart?.Line ?? 0,
                            t.ThreadContext.RightFileEnd?.Line ?? t.ThreadContext.RightFileStart?.Line ?? 0)
                        : null))
        ];
    }

    public async Task<CurrentUser> GetCurrentUserAsync(CancellationToken ct)
    {
        var identity = await _Retry.ExecuteAsync(
            attemptCt => CurrentIdentityAsync(attemptCt),
            "CurrentIdentity",
            ct).ConfigureAwait(false);
        return new CurrentUser(identity.Id, identity.DisplayName);
    }

    // Writes deliberately bypass the retry policy: re-running POSTs can duplicate
    // comments, thread votes, or statuses. The pipeline's publish gate absorbs
    // transient write failures via run status + follow-up runs (P0-2).

    public async Task<int> PostFindingThreadAsync(PrKey pr, RichFinding finding, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        var thread = new GitPullRequestCommentThread
        {
            Comments =
            [
                new AdoComment
                {
                    Content = CommentFormatter.FormatFinding(finding),
                    CommentType = AdoCommentType.Text,
                },
            ],
            Status = AdoThreadStatus.Active,
            ThreadContext = finding.Anchor is null
                ? null
                : new CommentThreadContext
                {
                    FilePath = "/" + finding.Anchor.FilePath.TrimStart('/'),
                    RightFileStart = new CommentPosition {Line = finding.Anchor.StartLine, Offset = 1},
                    RightFileEnd = new CommentPosition {Line = finding.Anchor.EndLine, Offset = 1},
                },
            Properties = new PropertiesCollection
            {
                [DedupeKeyProperty] = finding.DedupeKey ?? string.Empty,
            },
        };

        var created = await git.CreateThreadAsync(thread, pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: ct);
        return created.Id;
    }

    public async Task<int> PostSuggestionThreadAsync(PrKey pr, ThreadAnchor anchor, string body, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        var thread = new GitPullRequestCommentThread
        {
            Comments =
            [
                new AdoComment
                {
                    Content = body,
                    CommentType = AdoCommentType.Text,
                },
            ],
            Status = AdoThreadStatus.Active,
            ThreadContext = new CommentThreadContext
            {
                FilePath = "/" + anchor.FilePath.TrimStart('/'),
                RightFileStart = new CommentPosition {Line = anchor.StartLine, Offset = 1},
                RightFileEnd = new CommentPosition {Line = anchor.EndLine, Offset = 1},
            },
            // Deliberately NO Properties: a dedupe key would make triage's auto-resolve
            // and publish suppression see this thread; suggestion threads must stay invisible.
        };

        var created = await git.CreateThreadAsync(thread, pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: ct);
        return created.Id;
    }

    public async Task PostGeneralCommentAsync(
        PrKey pr,
        string text,
        string? dedupeKey,
        CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        var thread = new GitPullRequestCommentThread
        {
            Comments = [new AdoComment {Content = text, CommentType = AdoCommentType.Text}],
            Status = AdoThreadStatus.Active,
            Properties = dedupeKey is null
                ? null
                : new PropertiesCollection {[DedupeKeyProperty] = dedupeKey},
        };
        await git.CreateThreadAsync(thread, pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: ct);
    }

    public async Task ReplyToThreadAsync(PrKey pr, int threadId, string text, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        await git.CreateCommentAsync(
            new AdoComment {Content = text, CommentType = AdoCommentType.Text},
            pr.Project, pr.RepositoryId, pr.PrId, threadId, cancellationToken: ct);
    }

    public async Task SetThreadStatusAsync(PrKey pr, int threadId, ReviewThreadStatus status, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        await git.UpdateThreadAsync(
            new GitPullRequestCommentThread {Status = MapStatus(status)},
            pr.Project, pr.RepositoryId, pr.PrId, threadId, cancellationToken: ct);
    }

    public async Task SetReviewerVoteAsync(PrKey pr, string reviewerId, ReviewerVote vote, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        await git.CreatePullRequestReviewerAsync(
            new IdentityRefWithVote {Id = reviewerId, Vote = AdoReviewerVote.ToAdoVote(vote)},
            pr.Project, pr.RepositoryId, pr.PrId, reviewerId, cancellationToken: ct);
    }

    private static string OrgFromUrl(string orgUrl)
    {
        var uri = new Uri(orgUrl);
        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 ? uri.Host : path.Split('/')[0];
    }

    private static string StripRefs(string? refName)
        => refName is {Length: > 0} && refName.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? refName["refs/heads/".Length..]
            : refName ?? string.Empty;

    private static ChangedFileType ParseChangeType(string? value)
        => value?.ToLowerInvariant() switch
        {
            "add" => ChangedFileType.Add,
            "edit" => ChangedFileType.Edit,
            "delete" => ChangedFileType.Delete,
            "rename" => ChangedFileType.Rename,
            _ => ChangedFileType.Unknown,
        };

    private async Task<GitHttpClient> GitClientAsync(CancellationToken ct)
    {
        var client = await _Connection.GetClientAsync<GitHttpClient>(ct);
        return client;
    }

    private async Task<(string Id, string DisplayName)> CurrentIdentityAsync(CancellationToken ct)
    {
        await _Connection.ConnectAsync(ct);
        var identity = _Connection.AuthorizedIdentity;
        return (identity.Id.ToString(), identity.DisplayName);
    }

    private static ReviewThreadStatus MapStatus(AdoThreadStatus status) => status switch
    {
        AdoThreadStatus.Active => ReviewThreadStatus.Active,
        AdoThreadStatus.Fixed => ReviewThreadStatus.Fixed,
        AdoThreadStatus.Closed => ReviewThreadStatus.Closed,
        AdoThreadStatus.Pending => ReviewThreadStatus.Pending,
        _ => ReviewThreadStatus.Unknown,
    };

    private static AdoThreadStatus MapStatus(ReviewThreadStatus status) => status switch
    {
        ReviewThreadStatus.Active => AdoThreadStatus.Active,
        ReviewThreadStatus.Fixed => AdoThreadStatus.Fixed,
        ReviewThreadStatus.Closed => AdoThreadStatus.Closed,
        ReviewThreadStatus.Pending => AdoThreadStatus.Pending,
        _ => AdoThreadStatus.Unknown,
    };
}