using System.ComponentModel.DataAnnotations;

namespace ReviewForge.Service;

public sealed class ModelProfilingOptions
{
    public const string SectionName = "ModelProfiling";
    [Range(1, 3650)] public int RetentionDays { get; set; } = 30;
    [Range(1, 10_000)] public int DetailLimit { get; set; } = 10_000;
    [Range(1, 100)] public int CleanupBatchSize { get; set; } = 10;
    [Range(1, 1440)] public int CleanupIntervalMinutes { get; set; } = 60;
}
