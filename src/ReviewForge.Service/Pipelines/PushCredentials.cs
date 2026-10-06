namespace ReviewForge.Service;

/// <summary>Host-scoped credentials used by pipeline stages that commit and push changes.</summary>
public readonly record struct PushCredentials(string? Pat, string? AuthorName, string? AuthorEmail)
{
    public override string ToString() => "PushCredentials { Pat = [REDACTED], AuthorName = [REDACTED], AuthorEmail = [REDACTED] }";
}
