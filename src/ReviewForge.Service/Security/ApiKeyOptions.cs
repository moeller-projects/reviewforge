namespace ReviewForge.Service.Security;

/// <summary>API-key authentication for the /reviews surface.</summary>
public sealed class ApiKeyOptions
{
    public ApiKeyOptions()
    {
    }

    public ApiKeyOptions(IEnumerable<string> keys)
        => Keys = [.. keys.Where(key => !string.IsNullOrWhiteSpace(key))];

    public const string SectionName = "Api";
    public const string KeysEnvironmentVariable = "REVIEWFORGE_API_KEYS";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Fixed-window rate-limit policy name for the submit/discover endpoints.</summary>
    internal const string SubmitPolicy = SectionName + ":submit";

    /// <summary>Fixed-window rate-limit policy name for the run-status endpoint.</summary>
    internal const string StatusPolicy = SectionName + ":status";

    /// <summary>Runtime keys loaded only from REVIEWFORGE_API_KEYS; configuration cannot bind this property.</summary>
    public string[] Keys { get; private set; } = [];

    internal void SetEnvironmentKeys(IEnumerable<string> keys)
        => Keys = [.. keys.Where(key => !string.IsNullOrWhiteSpace(key))];

    /// <summary>Escape hatch for local development only. Never set in deployed environments.</summary>
    public bool AllowUnauthenticatedForDevelopment { get; set; }

    /// <summary>Fixed-window submit limit per client IP (POST /reviews, POST /reviews/discover).</summary>
    public int SubmitPermitLimit { get; set; } = 10;

    public int SubmitWindowSeconds { get; set; } = 60;

    /// <summary>Fixed-window status-poll limit per client IP (GET /reviews/{runId}); generous by default so polling clients are not throttled.</summary>
    public int StatusPermitLimit { get; set; } = 600;

    public int StatusWindowSeconds { get; set; } = 60;
}
