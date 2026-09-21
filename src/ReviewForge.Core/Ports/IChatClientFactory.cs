using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Ports;

/// <summary>Builds the model client for the configured reasoning provider.</summary>
public interface IChatClientFactory
{
    /// <summary>Model id the agent should address (without any provider routing prefix).</summary>
    string ModelName { get; }

    IChatClient Create();
}