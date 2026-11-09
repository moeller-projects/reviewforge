namespace ReviewForge.Core.Domain;

/// <summary>A proposed fix: replace Proposal range [StartLine..EndLine] (1-based inclusive)
/// of FilePath with Replacement ('\n'-joined; "" deletes the range).</summary>
public sealed record FixProposal
{
    public FixProposal(
        string FilePath,
        int StartLine,
        int EndLine,
        string Replacement,
        string Rationale,
        FixOrigin Origin = FixOrigin.Deterministic,
        int? SourceThreadId = null)
    {
        if ((Origin == FixOrigin.LlmCommanded && SourceThreadId is not > 0)
            || (Origin != FixOrigin.LlmCommanded && SourceThreadId is not null))
        {
            throw new ArgumentException(
                "SourceThreadId must be a positive value if and only if Origin is LlmCommanded");
        }

        this.FilePath = FilePath;
        this.StartLine = StartLine;
        this.EndLine = EndLine;
        this.Replacement = Replacement;
        this.Rationale = Rationale;
        this.Origin = Origin;
        this.SourceThreadId = SourceThreadId;
    }

    public string FilePath { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Replacement { get; init; }
    public string Rationale { get; init; }
    public FixOrigin Origin { get; init; }
    public int? SourceThreadId { get; init; }

    public void Deconstruct(
        out string filePath,
        out int startLine,
        out int endLine,
        out string replacement,
        out string rationale,
        out FixOrigin origin,
        out int? sourceThreadId)
    {
        filePath = FilePath;
        startLine = StartLine;
        endLine = EndLine;
        replacement = Replacement;
        rationale = Rationale;
        origin = Origin;
        sourceThreadId = SourceThreadId;
    }
}
