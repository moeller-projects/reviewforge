namespace ReviewForge.Core.Domain;

public sealed record ResolveCommand(int ThreadId, string RequestText, DateTimeOffset PublishedAt);
