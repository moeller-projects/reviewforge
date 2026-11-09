using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Resolve.Domain;

public sealed record ThreadVerdict(
    int ThreadId,
    TriageVerdict Verdict,
    string Evidence,
    string Confidence,
    string Category = "bug",
    string? Answer = null);
