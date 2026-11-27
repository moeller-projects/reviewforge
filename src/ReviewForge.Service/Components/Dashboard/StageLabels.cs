namespace ReviewForge.Service.Components.Dashboard;

/// <summary>Display labels only. Stage identity and order always come from the run.</summary>
public static class StageLabels
{
    public static string Label(string? id) => id switch
    {
        null or "" => "Unknown stage",
        "FetchPrContext" => "Fetch PR context",
        "ReviewGate" => "Check review eligibility",
        "BeginDraftRun" => "Start draft review",
        "ResolveGate" => "Check resolution eligibility",
        "PrepareRepository" => "Prepare repository",
        "ClassifyRun" => "Classify review",
        "EnrichContext" => "Gather context",
        "ExecuteReasoning" => "Analyze changes",
        "ValidateFindings" => "Validate findings",
        "VerifyFindings" => "Verify findings",
        "AutoFixFindings" => "Prepare fixes",
        "BeginRun" => "Record review start",
        "CommitFixes" => "Commit fixes",
        "TriageThreads" => "Triage existing threads",
        "PublishFindings" => "Publish findings",
        "CollectComments" => "Collect comments",
        "TriageComments" => "Triage comments",
        "PlanFixes" => "Plan fixes",
        "ApplyFixes" => "Apply fixes",
        "VerifyBuild" => "Verify build",
        "PersistDraftRun" => "Save draft review",
        "ResolveCommitPush" => "Commit and push resolutions",
        "ReplyComments" => "Reply to comments",
        "PersistRun" => "Save review result",
        _ => Humanize(id),
    };

    private static string Humanize(string id)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(id, "([a-z0-9])([A-Z])", "$1 $2");
        return char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }

    public static string Mark(DashboardRun run, string stage)
    {
        if (run.State == RunState.Completed) return "Completed";
        if (run.State == RunState.Queued) return "Not started";
        var stages = run.LoadedStages.ToList();
        var current = stages.IndexOf(run.CurrentStage ?? "");
        var index = stages.IndexOf(stage);
        if (current < 0 || index < 0) return "Unknown";
        if (index < current) return "Completed";
        if (index > current) return "Not started";
        return run.State switch
        {
            RunState.Failed => "Failed",
            RunState.Skipped => "Completed",
            RunState.Running => "Current",
            _ => "Unknown",
        };
    }
}
