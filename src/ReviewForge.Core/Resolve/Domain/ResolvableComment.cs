using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Resolve.Domain;

public sealed record ResolvableComment(
    int ThreadId,
    ThreadAnchor? Anchor,
    string RequesterId,
    string RequesterName,
    IReadOnlyList<ThreadComment> History,
    string RequestText,
    bool CommenterAllowed);
