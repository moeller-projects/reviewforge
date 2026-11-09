namespace ReviewForge.Core.Domain;

public enum ResolutionOutcome
{
    Fixed,
    AgentDeclined,
    NonIssue,
    Question,
    AlreadyFixed,
    OutOfScope,
    VerifyFailed,
    PushFailed,
    Deferred
}
