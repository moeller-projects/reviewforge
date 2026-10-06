using ReviewForge.Core.Ports;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Service;

public sealed class AgentFactory(
    IChatClientFactory chatClientFactory,
    IOptions<ReviewOptions> options,
    ILoggerFactory loggerFactory)
{
    public NativeReviewAgent Create()
    {
        var review = options.Value;
        return new NativeReviewAgent(chatClientFactory, new AgentOptions
        {
            MaxContextTokens = review.MaxContextTokens,
            MaxIterations = review.MaxIterations,
            PromptOverridePath = review.PromptOverridePath,
            RuleSetsPath = review.RuleSetsPath,
            Effort = review.ReasoningEffort,
            DebugLogging = review.AgentDebugLogging,
            GrepMaxMs = review.GrepMaxMs,
            GrepMaxLines = review.GrepMaxLines,
        }, loggerFactory.CreateLogger<NativeReviewAgent>());
    }
}
