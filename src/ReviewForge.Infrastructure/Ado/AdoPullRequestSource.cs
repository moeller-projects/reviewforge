using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    public AdoPullRequestSource(AdoOptions options)
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
    }

    public async Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
    {
        var gpr = await ExecuteWithTransientRetryAsync(
            async attemptCt =>
            {
                var git = await GitClientAsync(attemptCt);
                return await git.GetPullRequestAsync(
                    pr.Project,
                    pr.RepositoryId,
                    pr.PrId,
                    cancellationToken: attemptCt);
            },
            ct);

        return new PullRequest(
            gpr.PullRequestId,
            gpr.Title ?? string.Empty,
            gpr.Description,
            gpr.LastMergeSourceCommit?.CommitId ?? string.Empty,
            gpr.LastMergeTargetCommit?.CommitId ?? string.Empty,
            gpr.Repository?.RemoteUrl ?? string.Empty,
            gpr.IsDraft ?? false);
    }

    public async Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
    {
        var projectClient = await _Connection.GetClientAsync<ProjectHttpClient>(ct);
        var result = new List<PullRequestCandidate>();

        foreach (var project in await projectClient.GetProjects())
        {
            var git = await _Connection.GetClientAsync<GitHttpClient>(ct);
            var prs = await git.GetPullRequestsByProjectAsync(
                project.Name,
                new GitPullRequestSearchCriteria {Status = PullRequestStatus.Active},
                cancellationToken: ct);

            foreach (var gpr in prs)
            {
                var repositoryId = gpr.Repository?.Id.ToString();
                if (repositoryId is null)
                {
                    continue;
                }

                var key = new PrKey(_Org, project.Name, repositoryId, gpr.PullRequestId);
                var pr = new PullRequest(
                    gpr.PullRequestId,
                    gpr.Title ?? string.Empty,
                    gpr.Description,
                    gpr.LastMergeSourceCommit?.CommitId ?? string.Empty,
                    gpr.LastMergeTargetCommit?.CommitId ?? string.Empty,
                    gpr.Repository?.RemoteUrl ?? string.Empty,
                    gpr.IsDraft ?? false);

                result.Add(new PullRequestCandidate(
                    key,
                    pr,
                    StripRefs(gpr.TargetRefName),
                    gpr.CreatedBy?.Id.ToString() ?? string.Empty,
                    gpr.CreatedBy?.DisplayName ?? string.Empty));
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(PrKey pr, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        var refs = await git.GetPullRequestWorkItemRefsAsync(pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: ct);
        var ids = refs.Select(r => int.Parse(r.Id)).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var wit = await _Connection.GetClientAsync<WorkItemTrackingHttpClient>(ct);
        var items = await wit.GetWorkItemsAsync(ids, expand: WitModels.WorkItemExpand.All, cancellationToken: ct);

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
        var git = await GitClientAsync(ct);
        var iterations = await git.GetPullRequestIterationsAsync(pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: ct);
        var latest = iterations.MaxBy(i => i.Id ?? 0);
        if (latest?.Id is not { } iterationId)
        {
            return [];
        }

        var changes = await git.GetPullRequestIterationChangesAsync(pr.Project, pr.RepositoryId, pr.PrId, iterationId, cancellationToken: ct);
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
        var git = await GitClientAsync(ct);
        var botId = (await CurrentIdentityAsync(ct)).Id;
        var threads = await git.GetThreadsAsync(pr.Project, pr.RepositoryId, pr.PrId, cancellationToken: ct);

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
                    ]))
        ];
    }

    public async Task<CurrentUser> GetCurrentUserAsync(CancellationToken ct)
    {
        var identity = await CurrentIdentityAsync(ct);
        return new CurrentUser(identity.Id, identity.DisplayName);
    }

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

    public async Task PostGeneralCommentAsync(PrKey pr, string text, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        var thread = new GitPullRequestCommentThread
        {
            Comments = [new AdoComment {Content = text, CommentType = AdoCommentType.Text}],
            Status = AdoThreadStatus.Active,
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

    public async Task SetReviewerVoteAsync(PrKey pr, string reviewerId, int vote, CancellationToken ct)
    {
        var git = await GitClientAsync(ct);
        await git.CreatePullRequestReviewerAsync(
            new IdentityRefWithVote {Id = reviewerId, Vote = (short) vote},
            pr.Project, pr.RepositoryId, pr.PrId, reviewerId, cancellationToken: ct);
    }

    private static string OrgFromUrl(string orgUrl)
    {
        var uri = new Uri(orgUrl);
        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 ? uri.Host : path.Split('/')[0];
    }

    private static async Task<T> ExecuteWithTransientRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1;; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                await Task.Delay(delay, ct);
                delay += delay;
            }
        }
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