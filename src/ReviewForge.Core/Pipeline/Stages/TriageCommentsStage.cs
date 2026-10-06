using System.Diagnostics;
using System.Text.RegularExpressions;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class TriageCommentsStage(NativeReviewAgent agent, int batchSize = 20) : IReviewStage
{
    private static readonly Regex EvidenceCitation = new(@"(?:^|\s|\()([^\s():]+):([1-9][0-9]*)(?:\b|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public string Name => "triage-comments";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var resolve = ctx.RequireResolveState();
        var comments = resolve.ResolvableComments;
        if (comments.Count == 0) { resolve.ThreadVerdicts = []; return; }
        var all = new List<ThreadVerdict>();
        foreach (var batch in comments.Chunk(batchSize))
        {
            var batchIds = batch.Select(c => (long)c.ThreadId).ToHashSet();
            var verdicts = await agent.RunTriageAsync(
                ResolvePromptBuilder.BuildTriagePrompt(batch, ctx.WorkItems),
                ctx.Collector = new ReviewCollector(), ctx.ContextStore,
                ctx.RequireRepoDir(), ctx.ChangedFiles.ToHashSet(RepoPath.PathComparer),
                ctx.Diff, ctx.DiffText, batchIds, ct).ConfigureAwait(false);
            // Belt and suspenders: the tool rejects out-of-batch verdicts already; a verdict
            // that still slips through (custom agent) is dropped here, never pre-answering a
            // thread the model was not shown.
            all.AddRange(verdicts.Where(v => batchIds.Contains(v.ThreadId)));
        }
        var validIds = comments.Select(c => c.ThreadId).ToHashSet();
        var unknownVerdicts = all.Count(verdict => !validIds.Contains(verdict.ThreadId));
        if (unknownVerdicts > 0) ResolveTelemetry.ResolveUnknownVerdicts.Add(unknownVerdicts);
        var accepted = new Dictionary<int, ThreadVerdict>();
        foreach (var group in all.Where(v => validIds.Contains(v.ThreadId)).GroupBy(v => v.ThreadId))
        {
            accepted[group.Key] = group.Count() == 1
                ? group.Single()
                : new ThreadVerdict(group.Key, TriageVerdict.OutOfScope, "duplicate verdict", "low");
        }
        foreach (var comment in comments)
        {
            if (!accepted.ContainsKey(comment.ThreadId))
                accepted[comment.ThreadId] = new ThreadVerdict(comment.ThreadId, TriageVerdict.OutOfScope, "no verdict", "low");
        }
        foreach (var id in accepted.Keys.ToArray())
        {
            var verdict = accepted[id];
            if ((verdict.Verdict is TriageVerdict.NonIssue or TriageVerdict.AlreadyFixed)
                && !EvidenceCitation.IsMatch(verdict.Evidence))
            {
                accepted[id] = verdict with { Verdict = TriageVerdict.OutOfScope, Evidence = "evidence did not include a file:line citation" };
                ResolveTelemetry.ResolveEvidenceDowngrades.Add(1);
            }
        }
        resolve.ThreadVerdicts = accepted.Values.OrderBy(v => v.ThreadId).ToArray();
        foreach (var verdict in resolve.ThreadVerdicts)
            ResolveTelemetry.ResolveThreadsTriaged.Add(1, new TagList
            {
                { "verdict", verdict.Verdict.ToString().ToLowerInvariant() },
                { "confidence", verdict.Confidence.ToLowerInvariant() },
            });
    }
}
