namespace ReviewForge.Core.Domain;

public sealed record ResolveCommand(int ThreadId, string RequestText, DateTimeOffset PublishedAt);

public static class ResolveCommandDetector
{
    public const string Prefix = "/resolve";

    public static IReadOnlyList<ResolveCommand> Scan(
        IReadOnlyList<ReviewThread> threads,
        string creatorId,
        DateTimeOffset? watermark)
    {
        var commands = new List<ResolveCommand>();
        foreach (var thread in threads)
        {
            if (thread.Status is ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed || thread.Comments.Count == 0)
                continue;
            var last = thread.Comments[^1];
            if (last.IsBot || !string.Equals(last.AuthorId, creatorId, StringComparison.OrdinalIgnoreCase)
                           || (watermark is { } at && last.PublishedAt <= at))
                continue;
            var text = last.Text.TrimStart();
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                || (text.Length > Prefix.Length && !char.IsWhiteSpace(text[Prefix.Length])))
                continue;
            commands.Add(new ResolveCommand(thread.Id,
                text.Length <= Prefix.Length ? string.Empty : text[Prefix.Length..].Trim(), last.PublishedAt));
        }

        return commands;
    }
}