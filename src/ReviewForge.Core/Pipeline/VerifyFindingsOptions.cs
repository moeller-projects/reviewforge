using System.ComponentModel.DataAnnotations;

namespace ReviewForge.Core.Pipeline;

/// <summary>Configuration for the verify-findings challenge stage (section "VerifyFindings").
/// Everything is off by default: with <see cref="Enabled"/> false the pipeline is
/// byte-identical to a run without the stage. The stage costs ONE bounded Fast-tier request
/// per run (two only when the first response is unparseable) — no agent loop, no tools.</summary>
public sealed class VerifyFindingsOptions
{
    public const string SectionName = "VerifyFindings";

    public bool Enabled { get; init; } = false;

    /// <summary>Maximum findings verified per run; the rest pass unverified. Highest-severity first.</summary>
    [Range(1, 50)] public int MaxFindings { get; init; } = 20;

    /// <summary>File context lines quoted above and below each finding anchor.</summary>
    [Range(3, 60)] public int ContextLines { get; init; } = 15;

    /// <summary>Wall-clock cap for the verifier call (both attempts share it).</summary>
    [Range(10, 300)] public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Hard cap on the assembled prompt; file slices are dropped beyond it.</summary>
    [Range(4_000, 100_000)] public int MaxPromptChars { get; init; } = 24_000;
}
