using ReviewForge.Core.Pipeline;

namespace ReviewForge.Infrastructure.Ado;

/// <summary>
/// Temporary PAT-compatible bot attribution. ADO reports the PAT identity as author for both
/// ReviewForge writes and human comments made by that same identity, so require ReviewForge's
/// fixed preamble as provenance when the author matches the configured identity.
/// Replace with provider-authored comment IDs or a dedicated bot identity when available.
/// </summary>
internal static class AdoCommentBotClassifier
{
    public static bool IsBot(string? authorId, string botId, string? content)
        => !string.IsNullOrWhiteSpace(authorId)
           && string.Equals(authorId, botId, StringComparison.OrdinalIgnoreCase)
           && content?.StartsWith(CommentFormatter.BotPreamble, StringComparison.Ordinal) == true;
}