namespace ReviewForge.Core.Analysis;

/// <summary>Applies the required normalization and sanitization order to review text.</summary>
public static class PipelineText
{
    /// <summary>
    /// Returns NFKC-normalized text with explicitly unsafe invisible characters removed.
    /// The order is intentional: normalize first, then sanitize.
    /// </summary>
    public static string Preprocess(string? raw)
        => TextSanitizer.Sanitize(TextNormalizer.Normalize(raw));
}
