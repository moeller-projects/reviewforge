using System.Text;

namespace ReviewForge.Core.Analysis;

/// <summary>Provides allocation-free NFKC normalization for review text.</summary>
public static class TextNormalizer
{
    /// <summary>
    /// Normalizes input to Unicode NFKC. Returns the original instance when already normalized.
    /// Callers must apply this before <see cref="TextSanitizer.Sanitize"/>.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        return input.IsNormalized(NormalizationForm.FormKC)
            ? input
            : input.Normalize(NormalizationForm.FormKC);
    }
}
