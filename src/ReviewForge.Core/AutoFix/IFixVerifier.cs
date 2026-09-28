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
    bool RequiresWorkspaceWrites { get; }

    Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct)
        => VerifyAsync(repoDir, new[] { relativeFilePath }, ct);

    Task<FixVerdict> VerifyAsync(
        string repoDir, IReadOnlyList<string> editedFiles, CancellationToken ct)
        => editedFiles.Count == 0
            ? Task.FromResult(new FixVerdict(true, "no files to verify"))
            : VerifyAsync(repoDir, editedFiles[0], ct);
}

public sealed record FixVerdict(bool Passed, string Reason);

public sealed class NullFixVerifier : IFixVerifier
{
    public static readonly NullFixVerifier Instance = new();
    private NullFixVerifier() { }
    public string Name => "none";
    public bool RequiresWorkspaceWrites => false;

    public Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct)
        => Task.FromResult(new FixVerdict(true, "no verifier configured"));

    public Task<FixVerdict> VerifyAsync(
        string repoDir, IReadOnlyList<string> editedFiles, CancellationToken ct)
        => Task.FromResult(new FixVerdict(true, "no verifier configured"));
}
