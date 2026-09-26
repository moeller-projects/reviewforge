namespace ReviewForge.Core.AutoFix.Fixers;

/// <summary>
/// preserving indentation. Declines URL sources (http/https/git), local archive sources
/// (.tar, .tar.gz, .tgz, .tar.bz2, .tar.xz, .tbz2, .txz, .zip), and line continuations —
/// ADD is semantically required for those.
/// </summary>
public sealed class DockerAddToCopyFixer : IFindingFixer
{
    private static readonly string[] ArchiveExtensions =
        [".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tar.xz", ".tbz2", ".txz", ".zip"];
    public string RuleId => "docker.add-vs-copy";

    public FixProposal? TryPropose(FixContext context)
    {
        var anchor = context.Finding.Anchor;
        if (anchor is null || anchor.StartLine != anchor.EndLine)
        {
            return null;
        }

        var lineIndex = anchor.StartLine - 1;
        if (lineIndex < 0 || lineIndex >= context.FileLines.Length)
        {
            return null;
        }

        var line = context.FileLines[lineIndex];
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("ADD ", StringComparison.Ordinal)
            && !trimmed.Equals("ADD", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.EndsWith('\\'))
        {
            return null; // continuation lines may carry archive/URL semantics on the next line
        }

        var args = trimmed.Length > 4 ? trimmed[4..] : string.Empty;
        if (args.Contains("://", StringComparison.Ordinal) || args.StartsWith("git@", StringComparison.Ordinal))
        {
            return null; // remote sources require ADD
        }

        if (args.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('"', '\'', '[', ']', ','))
            .Any(tok => ArchiveExtensions.Any(ext => tok.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
        {
            return null; // local archives require ADD
        }

        var indentLength = line.Length - trimmed.Length;
        var fixedLine = line[..indentLength] + "COPY " + args;
        return new FixProposal(
            context.FilePath,
            anchor.StartLine,
            anchor.StartLine,
            fixedLine.TrimEnd(),
            "uses COPY instead of ADD — ADD has surprising tar-extraction and remote-fetch semantics");
    }
}
