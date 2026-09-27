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

/// <summary>Builds the model client for the configured reasoning provider.</summary>
public interface IChatClientFactory
{
    /// <summary>Model id the agent should address (without any provider routing prefix).</summary>
    string ModelName { get; }

    /// <summary>Model id for <paramref name="tier"/> (without any provider routing prefix).</summary>
    string ModelName(ChatTier tier);

    IChatClient Create();

    IChatClient Create(ChatTier tier);
}
