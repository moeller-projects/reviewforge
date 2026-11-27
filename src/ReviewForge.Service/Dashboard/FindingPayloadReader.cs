using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>One safe interpretation of stored payloads for projection and publication eligibility.</summary>
internal static class FindingPayloadReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RichFinding? Read(string? json)
    {
        if (json is null) return null;
        RichFinding? finding;
        try { finding = JsonSerializer.Deserialize<RichFinding>(json, JsonOptions); }
        catch (JsonException) { return null; }
        return finding is not null && Validator.TryValidateObject(finding, new ValidationContext(finding),
            new List<ValidationResult>(), validateAllProperties: true) ? finding : null;
    }
}
