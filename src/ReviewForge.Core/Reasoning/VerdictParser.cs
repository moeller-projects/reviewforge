using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Parses the findings verifier's schema-constrained JSON response. Unknown and missing finding
/// keys are ignored (per-finding fail-open); only a "rejected" verdict removes a finding. The
/// verifier can only subtract — it never edits titles, severities, or anchors.
/// </summary>
public static class VerdictParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Verdicts keyed by dedupe key; null when the response is not a valid object
    /// containing a verdicts array (the caller retries once, then fails open).</summary>
    public static IReadOnlyDictionary<string, FindingsVerifierPrompt.Verdict>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ResponseEnvelope? response;
        try
        {
            response = JsonSerializer.Deserialize<ResponseEnvelope>(text, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (response?.Verdicts is not { } entries)
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

    internal sealed class ResponseEnvelope
    {
        public ResponseEnvelope()
        {
        }

        [JsonPropertyName("verdicts")]
        public List<ResponseEntry> Verdicts { get; init; } = null!;
    }

    internal sealed class ResponseEntry
    {
        public ResponseEntry()
        {
        }

        [JsonPropertyName("key")]
        public string Key { get; init; } = null!;

        [JsonPropertyName("verdict")]
        public string Verdict { get; init; } = null!;

        [JsonPropertyName("reason")]
        public string Reason { get; init; } = null!;
    }
}

internal static class FindingsVerifierResponseFormat
{
    internal static ChatResponseFormat Format { get; } =
        ChatResponseFormat.ForJsonSchema<VerdictParser.ResponseEnvelope>();
}