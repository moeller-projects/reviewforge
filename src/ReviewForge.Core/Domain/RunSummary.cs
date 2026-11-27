namespace ReviewForge.Core.Domain;

/// <summary>Requested current-head identity for bounded dashboard history reads.</summary>
public sealed record CurrentHeadQuery(PrKey Pr, string HeadSha);

/// <summary>Durable outcome only. No findings JSON, errors, or transient stage history.</summary>
public sealed record RunSummary(Guid RunId, PrKey Pr, string HeadSha, string Pipeline,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool Success, int ReviewFindings);
