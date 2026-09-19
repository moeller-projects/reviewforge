namespace ReviewForge.Service;

/// <summary>
/// Opt-in API documentation (OpenAPI + Scalar UI). Disabled by default: the API has no
/// authentication, so the schema must not be published in production unless explicitly enabled.
/// </summary>
public sealed class ApiDocsOptions
{
    public const string SectionName = "ApiDocs";

    public bool Enabled { get; init; }

    public string Title { get; init; } = "reviewforge API";

    public string Version { get; init; } = "v1";
}