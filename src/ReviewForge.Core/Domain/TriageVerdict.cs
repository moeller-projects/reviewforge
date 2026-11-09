namespace ReviewForge.Core.Domain;

// Kept in Domain because ResolveAction is part of the IFindingStore port contract.
public enum TriageVerdict
{
    Actionable,
    NonIssue,
    Question,
    AlreadyFixed,
    OutOfScope
}
