namespace ReviewForge.Core.Domain;

public enum ResolveGateDecision { Continue, Draft, AuthorNotAllowed, NoComments, NoNewComments }

public static class ResolveGate
{
    public static ResolveGateDecision Evaluate(
        PullRequest pr,
        IReadOnlyList<ReviewThread> threads,
        DateTimeOffset? resolveWatermark,
        bool authorAllowed)
    {
        if (pr.IsDraft) return ResolveGateDecision.Draft;
        if (!authorAllowed) return ResolveGateDecision.AuthorNotAllowed;
        var humanComments = threads.SelectMany(t => t.Comments).Where(c => !c.IsBot).ToArray();
        if (humanComments.Length == 0) return ResolveGateDecision.NoComments;
        return resolveWatermark is { } watermark && humanComments.All(c => c.PublishedAt <= watermark)
            ? ResolveGateDecision.NoNewComments
            : ResolveGateDecision.Continue;
    }
}
