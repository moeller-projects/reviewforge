using ReviewForge.Core.Workspaces;

namespace ReviewForge.Service;

/// <summary>Repository workspace root and checkout lifecycle settings.</summary>
public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    /// <summary>Root for repository checkouts (one subdir per repository).</summary>
    public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "reviewforge");

    public CheckoutEvictionOptions Checkout { get; init; } = new();
}