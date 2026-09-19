using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace ReviewForge.Infrastructure.Ado;

/// <summary>Typed Azure DevOps configuration; PAT comes from the environment only, never config files.</summary>
[ExcludeFromCodeCoverage]
public sealed class AdoOptions
{
    public const string SectionName = "Ado";
    public const string PatEnvironmentVariable = "REVIEWFORGE_ADO_PAT";

    [Required, Url] public required string OrgUrl { get; init; }

    [Required] public required string Project { get; init; }

    /// <summary>Resolved from <see cref="PatEnvironmentVariable"/>; never logged, never serialized.</summary>
    public string? Pat { get; set; }
}