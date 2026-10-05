using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class VerifyBuildStage(
    IProcessRunner runner,
    IReadOnlyList<string> command,
    TimeSpan timeout,
    bool singleCommit) : IReviewStage
{
    public string Name => "verify-build";
    public int Order => 65;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (command.Count == 0 || ctx.AppliedResolutions.Count == 0) return;
        ctx.ResolveVerificationStatus = "passed";
        var outcomes = new Dictionary<int, ResolutionOutcome>(ctx.ResolutionOutcomes);
        var details = new Dictionary<int, string>(ctx.ResolutionDetails);
        var surviving = new List<AppliedResolution>();
        var failed = new List<(AppliedResolution[] Items, string Detail)>();
        foreach (var group in ctx.AppliedResolutions
                     .GroupBy(r => string.Join("\0", r.Files.Order(RepoPath.PathComparer)), StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var items = group.ToArray();
            var result = await runner.RunAsync(command, ctx.RequireRepoDir(), timeout, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                surviving.AddRange(items);
                continue;
            }
            var output = (result.StandardOutput + "\n" + result.StandardError).Trim();
            if (output.Length > 4000) output = output[^4000..];
            var detail = result.TimedOut
                ? $"Verification timed out after {timeout.TotalSeconds:0} seconds. {output}"
                : $"Verification exited with code {result.ExitCode}. {output}";
            foreach (var item in items)
            {
                foreach (var file in item.Files)
                {
                    if (ctx.ResolutionEditors.TryGetValue(item.ThreadId, out var editor)
                        && editor.GetSessionChange(file) is not null)
                        editor.RevertFile(file);
                }
                failed.Add((items, detail));
                break;
            }
        }
        if (failed.Count > 0) ctx.ResolveVerificationStatus = "failed";

        if (singleCommit && failed.Count > 0)
        {
            var detail = failed[0].Detail;
            foreach (var item in surviving)
            {
                foreach (var file in item.Files)
                {
                    if (ctx.ResolutionEditors.TryGetValue(item.ThreadId, out var editor)
                        && editor.GetSessionChange(file) is not null)
                        editor.RevertFile(file);
                }
                outcomes[item.ThreadId] = ResolutionOutcome.VerifyFailed;
                details[item.ThreadId] = detail;
            }
            ReviewForgeTelemetry.ResolveVerifyFailed.Add(surviving.Count);
            surviving.Clear();
        }
        foreach (var (items, detail) in failed)
        {
            ReviewForgeTelemetry.ResolveVerifyFailed.Add(items.Length);
            foreach (var item in items)
            {
                outcomes[item.ThreadId] = ResolutionOutcome.VerifyFailed;
                details[item.ThreadId] = detail;
            }
        }
        ctx.AppliedResolutions = surviving;
        ctx.ResolutionOutcomes = outcomes;
        ctx.ResolutionDetails = details;
    }
}
