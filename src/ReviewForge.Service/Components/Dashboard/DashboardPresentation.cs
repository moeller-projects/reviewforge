using ReviewForge.Core.Domain;

namespace ReviewForge.Service.Components.Dashboard;

/// <summary>Presentation only; no eligibility or scheduling decisions.</summary>
public static class DashboardPresentation
{
    public static string Coverage(bool? reviewed) => reviewed switch { true => "Reviewed", false => "Unreviewed", _ => "Coverage unknown" };
    public static string? ExternalUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    public static int? Progress(DashboardRun run) => run.State == RunState.Completed ? 100 :
        run.CurrentStage is not null && run.LoadedStages.Contains(run.CurrentStage) ? run.ProgressPercent : null;
    public static string Stage(DashboardRun run) => run.CurrentStage is not null ? StageLabels.Label(run.CurrentStage) :
        run.State is RunState.Queued or RunState.Running || run.IsPending || run.IsClaimed ? "Waiting for worker" : "Stage unknown";
    public static string Outcome(DashboardRun run) => run.State?.ToString() ?? (run.IsClaimed ? "Claimed — state unknown" : "Outcome unknown");
    public static string Detail(DashboardRun run) => run.OutcomeDetail ?? run.State switch
    {
        RunState.Completed => "Review completed.",
        RunState.Failed => "Review failed. See service logs for investigation.",
        RunState.Skipped => "Review skipped.",
        _ => "No result yet.",
    };
    public static string Blocker(DashboardRow row) => row.ActiveBlocker is not { } run ? "No active blocker" :
        $"{(run.HeadSha is null ? "Unknown-head" : run.HeadSha == row.PullRequest.Candidate.Pr.SourceCommitSha ? "Current-head" : "Old-head")} {run.Kind?.ToString() ?? "unknown run kind"} · {Outcome(run)}";
    public static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Unknown";
    public static string Relative(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return "Time unknown";
        var age = now - value.Value;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
    public static bool Matches(DashboardRow row, string? search, string? project, string? repository, string? status)
    {
        var pr = row.PullRequest.Candidate;
        return (string.IsNullOrEmpty(project) || project == pr.Key.Project) &&
            (string.IsNullOrEmpty(repository) || repository == row.PullRequest.RepositoryName) &&
            (string.IsNullOrEmpty(status) || status == Coverage(row.CurrentHeadReviewed) || status == (row.LatestPrAttempt ?? row.LatestAttempt)?.State.ToString()) &&
            (string.IsNullOrEmpty(search) || $"{pr.Key.PrId} {pr.Pr.Title} {pr.CreatorName} {row.PullRequest.RepositoryName}".Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
