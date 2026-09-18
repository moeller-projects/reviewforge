namespace ReviewForge.Core.Reasoning;

/// <summary>
/// In-memory staged context for the agent (enrichment payloads, CRG output, notes).
/// Replaces the Python staging dir on disk — no temp-dir lifecycle.
/// </summary>
public sealed class ContextStore
{
    public const int MaxReadChars = 64 * 1024;

    private readonly Dictionary<string, string> _Entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _Entries.Keys;

    public void Put(string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _Entries[name] = content ?? string.Empty;
    }

    /// <summary>Reads an entry, capped at <see cref="MaxReadChars"/>; null when unknown.</summary>
    public string? Read(string name)
    {
        if (!_Entries.TryGetValue(name, out var content))
        {
            return null;
        }

        return content.Length <= MaxReadChars ? content : content[..MaxReadChars] + "\n…[truncated]";
    }
}