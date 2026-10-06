using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

public sealed class ResolveContextInitializer
{
    public void Initialize(ReviewRequest request, ReviewContext context)
    {
        if (request.Kind != RunKind.Resolve)
            throw new ArgumentException("The request must be a resolve run.", nameof(request));

        context.RunKind = RunKind.Resolve;
        context.RequestedHeadSha = request.HeadSha;
        context.Trigger = request.Trigger;
        context.EnqueueContext = request.EnqueueContext;
    }
}
