using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Infrastructure.Ado;

/// <summary>
/// Temporary PAT-compatible bot attribution. ADO reports the PAT identity as author for both
/// ReviewForge writes and human comments made by that same identity, so require ReviewForge's
/// fixed preamble as provenance when the author matches the configured identity. ADO system
/// comments are classified separately before author/content attribution is considered.
/// </summary>
internal static class AdoCommentBotClassifier
{
    public static ThreadCommentOrigin Classify(string? authorId, string botId, string? content, bool isSystem)
    {
        if (isSystem)
        {
            return ThreadCommentOrigin.System;
        }

        return IsBot(authorId, botId, content)
            ? ThreadCommentOrigin.Bot
            : ThreadCommentOrigin.Human;
    }

    public static bool IsBot(string? authorId, string botId, string? content)
        => !string.IsNullOrWhiteSpace(authorId)
           && string.Equals(authorId, botId, StringComparison.OrdinalIgnoreCase)
           && content?.StartsWith(CommentFormatter.BotPreamble, StringComparison.Ordinal) == true;
}
