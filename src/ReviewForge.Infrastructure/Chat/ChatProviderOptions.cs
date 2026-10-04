using System.ComponentModel.DataAnnotations;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>
/// Typed reasoning-provider configuration. Provider format: "openai-codex" (OAuth file
/// credential) or "openai" (API key from OPENAI_API_KEY). Model may carry a routing
/// prefix ("openai-codex:gpt-5.6-luna") — stripped before addressing the model.
/// </summary>
public sealed class ChatProviderOptions : IValidatableObject
{
    public const string SectionName = "Reasoning";

    [Required, AllowedValues("openai-codex", "openai")]
    public required string Provider { get; init; }

    [Required] public required string Model { get; init; }

    /// <summary>
    /// Optional cheaper/faster model for the Fast tier (follow-up reviews and fix
    /// passes). Must carry the same provider routing prefix as <see cref="Model"/>.
    /// Null (default) aliases the Fast tier to <see cref="Model"/> — byte-identical
    /// behavior, zero config.
    /// </summary>
    public string? FollowUpModel { get; init; }

    /// <summary>OAuth credential file for openai-codex; defaults to ~/.codex/auth.json.</summary>
    public string? CredentialPath { get; init; }

    /// <summary>
    /// Process-wide cap on concurrent provider HTTP requests (across both model tiers).
    /// Null (default) resolves to Host:WorkerCount × 2 — permissive on purpose;
    /// tighten from the reviewforge.llm.governor.wait_ms histogram.
    /// </summary>
    [Range(1, 10_000)]
    public int? MaxConcurrentRequests { get; init; }

    /// <summary>Seconds a request waits for a governor slot before the run fails with a
    /// visible <see cref="LlmGovernorTimeoutException"/>. Default 300.</summary>
    [Range(1, 3_600)]
    public int GovernorAcquireTimeoutSeconds { get; init; } = 300;

    public static string DefaultCredentialPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            yield break; // [Required] reports a missing Model cleanly; nothing to cross-check
        }

        if (!string.IsNullOrWhiteSpace(FollowUpModel)
            && !string.Equals(ProviderPrefix(FollowUpModel), ProviderPrefix(Model), StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "FollowUpModel must use the same provider routing prefix as Model " +
                $"(got '{FollowUpModel}' vs '{Model}')",
                [nameof(FollowUpModel)]);
        }
    }

    /// <summary>The routing prefix before ':' (empty when absent); the provider decides
    /// the transport, so a cross-prefix pair would silently hit the wrong endpoint.</summary>
    private static string ProviderPrefix(string model)
        => model.Split(':', 2) is [var prefix, _] ? prefix : string.Empty;
}
