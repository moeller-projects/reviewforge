namespace ReviewForge.Core.Domain;

/// <summary>A fix that passed all gates this run and will be published — as a suggestion
/// (Suggestion mode, or degraded) or as a pushed commit (CommitOnHead mode).</summary>
public sealed record AppliedFix(
    string DedupeKey, // finding key, or "thread-{ThreadId}" for commanded fixes
    FixProposal Proposal)
{
    /// <summary>Prefix of audit-only keys for commanded fixes; never a finding identity.</summary>
    public const string CommandKeyPrefix = "thread-";

    /// <summary>CommitOnHead only: the edit was materialized in the private checkout and is
    /// pending commit by stage 10. False in Suggestion mode and for degraded fixes.</summary>
    public bool AppliedToTree { get; set; }

    /// <summary>Set by stage 10 after the commit landed; null = publish as suggestion.</summary>
    public string? CommitSha { get; set; }
    /// <summary>Subject line of <see cref="CommitSha"/> (reply bodies).</summary>
    public string? CommitSubject { get; set; }
}
