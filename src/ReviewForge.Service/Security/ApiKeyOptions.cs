namespace ReviewForge.Service.Security;

/// <summary>API-key authentication for the /reviews surface.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Api";
    public const string KeysEnvironmentVariable = "REVIEWFORGE_API_KEYS";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Fixed-window rate-limit policy name for the submit/discover endpoints.</summary>
    internal const string SubmitPolicy = SectionName + ":submit";

    /// <summary>Configured keys. Populated from REVIEWFORGE_API_KEYS (env wins) or Api:Keys.</summary>
    public string[] Keys { get; set; } = [];

    /// <summary>Escape hatch for local development only. Never set in deployed environments.</summary>
    public bool AllowUnauthenticatedForDevelopment { get; set; }

    /// <summary>Fixed-window submit limit per API key (POST /reviews, POST /reviews/discover).</summary>
    public int SubmitPermitLimit { get; set; } = 10;

    public int SubmitWindowSeconds { get; set; } = 60;
}
