namespace ReviewForge.Core.Domain;

/// <summary>Deterministic = rule-registered fixer. LlmCommanded = agent-drafted after an
/// explicit /fixit command by the PR author. (LlmAutonomous is reserved — see the
/// AutoFix implementation plan appendix.)</summary>
public enum FixOrigin
{
    Deterministic,
    LlmCommanded
}
