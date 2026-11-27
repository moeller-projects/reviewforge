using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using ReviewForge.Core.Domain;

namespace ReviewForge.Infrastructure.Ado;

public sealed record AdoProjectPage(IReadOnlyList<TeamProjectReference> Projects, string? ContinuationToken);

/// <summary>Covered paging, failure isolation and mapping; delegates are thin SDK calls.
/// Projects use the SDK's opaque continuation token; PRs use skip/top until an empty page.
/// Neither loop treats a server-capped short page as the end.</summary>
public sealed class AdoInventoryReader(
    string org,
    Uri organizationUrl,
    Func<string?, int, CancellationToken, Task<AdoProjectPage>> projects,
    Func<string, int, int, CancellationToken, Task<IReadOnlyList<GitPullRequest>>> pullRequests,
    Func<PrKey, CancellationToken, Task<int>>? linkedWorkItemCount = null)
{
    private const int PageSize = 100;
    private readonly Uri _BrowserBase = SafeBrowserBase(organizationUrl);

    private static Uri SafeBrowserBase(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Organization browser URL must use HTTP or HTTPS.", nameof(uri));
        return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
    }

    public async Task<PullRequestInventory> ReadAsync(CancellationToken ct)
    {
        var rows = new Dictionary<PrKey, PullRequestInventoryItem>();
        var warnings = new HashSet<string>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? continuation = null;
        var seenTokens = new HashSet<string>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            AdoProjectPage page;
            try
            {
                page = await projects(continuation, PageSize, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                warnings.Add("Project inventory could not be fully retrieved.");
                break;
            }
            foreach (var project in page.Projects)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(project.Name))
                {
                    warnings.Add("A project with missing identity was omitted.");
                    continue;
                }
                if (!seenProjects.Add(project.Name)) continue;
                var prSkip = 0;
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var prs = await pullRequests(project.Name, prSkip, PageSize, ct).ConfigureAwait(false);
                        if (prs.Count == 0) break;
                        prSkip += prs.Count;
                        foreach (var pr in prs)
                        {
                            if (pr.PullRequestId <= 0 || pr.Repository is null || pr.Repository.Id == Guid.Empty)
                            {
                                warnings.Add("A pull request with missing identity was omitted.");
                                continue;
                            }
                            var key = new PrKey(org, project.Name, pr.Repository.Id.ToString(), pr.PullRequestId);
                            var browserUrl = new Uri(_BrowserBase.AbsoluteUri.TrimEnd('/') + "/" +
                                Uri.EscapeDataString(project.Name) + "/_git/" + key.RepositoryId +
                                "/pullrequest/" + key.PrId).AbsoluteUri;
                            var candidate = new PullRequestCandidate(key,
                                new PullRequest(pr.PullRequestId, pr.Title ?? "", pr.Description,
                                    pr.LastMergeSourceCommit?.CommitId ?? "", pr.LastMergeTargetCommit?.CommitId ?? "",
                                    "", pr.IsDraft ?? false, pr.CreatedBy?.Id ?? "", pr.CreatedBy?.DisplayName ?? "", pr.SourceRefName),
                                (pr.TargetRefName ?? "").Replace("refs/heads/", "", StringComparison.Ordinal),
                                pr.CreatedBy?.Id ?? "", pr.CreatedBy?.DisplayName ?? "");
                            int? count = pr.WorkItemRefs?.Length;
                            IReadOnlyList<string> metadataWarnings = [];
                            if (count is null && linkedWorkItemCount is not null)
                            {
                                try
                                {
                                    // Sequential within the cached inventory refresh: at most one
                                    // cheap reference request in flight, never rich work-item content.
                                    count = await linkedWorkItemCount(key, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception)
                                {
                                    metadataWarnings = ["Linked work-item availability could not be retrieved."];
                                }
                            }
                            rows[key] = new(candidate, pr.Repository.Name ?? "", browserUrl, count)
                            { MetadataWarnings = metadataWarnings };
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    warnings.Add("Pull requests from one or more projects could not be fully retrieved.");
                }
            }
            continuation = page.ContinuationToken;
            if (string.IsNullOrEmpty(continuation)) break;
            if (!seenTokens.Add(continuation))
            {
                warnings.Add("Project inventory returned a repeated continuation token.");
                break;
            }
        }
        ct.ThrowIfCancellationRequested();
        return new([.. rows.Values], warnings.Count == 0, [.. warnings.Order(StringComparer.Ordinal)]);
    }
}
