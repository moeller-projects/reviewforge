using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Ports;

/// <summary>Model quality tier. <see cref="Full"/> is the strong review model;
/// <see cref="Fast"/> is the cheaper/faster tier for follow-up reviews and fix passes.
/// Factories alias <see cref="Fast"/> to <see cref="Full"/> when no fast model is
/// configured — identical behavior, zero config.</summary>
public enum ChatTier
{
    Full,
    Fast,
}

/// <summary>Builds the model client for the configured reasoning provider. Callers pick a
/// <see cref="ChatTier"/> explicitly: follow-up reviews and fix passes use
/// <see cref="ChatTier.Fast"/> (aliasing <see cref="ChatTier.Full"/> when no fast model is
/// configured), full reviews use <see cref="ChatTier.Full"/>.</summary>
public interface IChatClientFactory
{
    /// <summary>Model id for <paramref name="tier"/> (without any provider routing prefix).</summary>
    string ModelName(ChatTier tier);

    /// <summary>Creates request options with the model selected for <paramref name="tier"/>.
    /// Callers can add request-specific instructions, tools, and other options.</summary>
    ChatOptions CreateChatOptions(ChatTier tier, ReasoningEffort? reasoningEffort = null)
        => new()
        {
            ModelId = ModelName(tier),
            Reasoning = reasoningEffort is { } effort ? new ReasoningOptions {Effort = effort} : null,
        };

    IChatClient Create(ChatTier tier);

    /// <summary>Whether a provider transport failure is safe to retry before a response is observed.
    /// Custom providers can override this to classify their own transport exceptions.</summary>
    bool IsTransientFailure(Exception exception) => exception is TimeoutException;
}
