namespace ReviewForge.Core.AutoFix;

/// <summary>
/// Optional verification of fixed files. The default <see cref="NullFixVerifier"/> passes
/// everything and requires no workspace writes — with it, the deterministic fix path
/// performs zero disk writes. A process verifier runs a configured command in the
/// checkout (see the AutoFix implementation plan's security note: the command executes
/// PR-author-controlled code; enable only with a strict author allowlist).
/// </summary>
public interface IFixVerifier
{
    string Name { get; }

    /// <summary>True when applying fixes to the checkout is required before verification.</summary>
    bool RequiresWorkspaceWrites { get; }

    Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct);
}

/// <summary>Outcome of one file verification.</summary>
public sealed record FixVerdict(bool Passed, string Reason);

/// <summary>Default verifier: always passes, never touches the checkout. This flag is what
/// lets the deterministic path skip disk writes entirely.</summary>
public sealed class NullFixVerifier : IFixVerifier
{
    public static readonly NullFixVerifier Instance = new();

    private NullFixVerifier()
    {
    }

    public string Name => "none";
    public bool RequiresWorkspaceWrites => false;

    public Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct)
        => Task.FromResult(new FixVerdict(true, "no verifier configured"));
}
