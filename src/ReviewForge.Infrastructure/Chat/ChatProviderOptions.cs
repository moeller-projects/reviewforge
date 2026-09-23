using System.ComponentModel.DataAnnotations;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>
/// Typed reasoning-provider configuration. Provider format: "openai-codex" (OAuth file
/// credential) or "openai" (API key from OPENAI_API_KEY). Model may carry a routing
/// prefix ("openai-codex:gpt-5.6-luna") — stripped before addressing the model.
/// </summary>
public sealed class ChatProviderOptions
{
    public const string SectionName = "Reasoning";

    [Required, AllowedValues("openai-codex", "openai")]
    public required string Provider { get; init; }

    [Required] public required string Model { get; init; }

    /// <summary>OAuth credential file for openai-codex; defaults to ~/.codex/auth.json.</summary>
    public string? CredentialPath { get; init; }

    public static string DefaultCredentialPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
}