namespace ReviewForge.Core.Domain;

/// <summary>An author-issued fix command on a PR thread.</summary>
public sealed record FixCommand(
    int ThreadId,
    ThreadAnchor Anchor, // the thread's file/line; required
    string? Instruction, // free text after "/fixit", may be null
    string QuotedComment); // the comment being answered (bounded)
