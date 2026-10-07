using System.Text.Json;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Parses the findings verifier's JSON reply. Extracts from the first '[' to the last ']'
/// and deserializes a small DTO; unknown keys are ignored (per-finding fail-open) and any
/// verdict value other than "rejected" (case-insensitive) keeps the finding. The verifier
/// can only subtract — it never edits titles, severities, or anchors.
/// </summary>
public static class VerdictParser
{
    /// <summary>Verdicts keyed by dedupe key; null when the response carries no parseable
    /// JSON array at all (the caller retries once, then fails open).</summary>
    public static IReadOnlyDictionary<string, FindingsVerifierPrompt.Verdict>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        Entry?[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Entry?[]>(
                text.AsSpan(start, end - start + 1),
                new JsonSerializerOptions {PropertyNameCaseInsensitive = true});
        }
        catch (JsonException)
        {
            return null;
        }

        if (entries is null)
        {
            return null;
        }

        var verdicts = new Dictionary<string, FindingsVerifierPrompt.Verdict>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not {Key: {Length: > 0} key})
            {
                continue;
            }

            var rejected = string.Equals(entry.Verdict, "rejected", StringComparison.OrdinalIgnoreCase);
            verdicts[key] = new FindingsVerifierPrompt.Verdict(rejected, entry.Reason ?? string.Empty);
        }

        return verdicts;
    }

    private sealed record Entry(string? Key, string? Verdict, string? Reason);
}