namespace ReviewForge.Core.Domain;

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
            if (Find(thread, creatorId, watermark) is { } command)
                commands.Add(command);
        }

        return commands;
    }

    internal static ResolveCommand? Find(
        ReviewThread thread,
        string creatorId,
        DateTimeOffset? watermark)
    {
        if (thread.Status is ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed || thread.Comments.Count == 0)
            return null;
        var last = thread.Comments.LastOrDefault(comment => comment.IsHuman);
        if (last is null || !string.Equals(last.AuthorId, creatorId, StringComparison.OrdinalIgnoreCase)
                       || (watermark is { } at && last.PublishedAt <= at))
            return null;
        var text = last.Text.TrimStart();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || (text.Length > Prefix.Length && !char.IsWhiteSpace(text[Prefix.Length])))
            return null;
        return new ResolveCommand(thread.Id,
            text.Length <= Prefix.Length ? string.Empty : text[Prefix.Length..].Trim(), last.PublishedAt);
    }
}
