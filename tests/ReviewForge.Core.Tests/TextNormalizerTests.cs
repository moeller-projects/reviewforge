using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class TextNormalizerTests
{
    [Fact]
    public void Already_normalized_input_returns_same_instance()
    {
        var input = "plain ASCII review text";

        Assert.Same(input, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Canonical_variants_converge()
    {
        var composed = "Café";
        var decomposed = "Cafe\u0301";

        Assert.Equal(TextNormalizer.Normalize(composed), TextNormalizer.Normalize(decomposed));
    }

    [Theory]
    [InlineData("𝐛𝐨𝐥𝐝", "bold")]
    [InlineData("ｒｅｖｉｅｗ", "review")]
    [InlineData("ᴛʜɪs", "This")]
    [InlineData("ⓗⓔⓛⓛⓞ", "hello")]
    [InlineData("ﬁ", "fi")]
    [InlineData("ｶ", "カ")]
    public void Compatibility_forms_collapse(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once = TextNormalizer.Normalize("ｒｅｖｉｅｗ\u200B ﬁ");

        Assert.Same(once, TextNormalizer.Normalize(once));
    }

    [Fact]
    public void Pipeline_normalizes_before_sanitizing()
    {
        var result = PipelineText.Preprocess("ｒｅｖｉｅｗ\u200B");

        Assert.Equal("review", result);
    }

    [Fact]
    public void Null_and_empty_inputs_are_empty()
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(null));
        Assert.Equal(string.Empty, TextNormalizer.Normalize(string.Empty));
        Assert.Equal(string.Empty, PipelineText.Preprocess(null));
    }

    [Fact]
    public void Dedupe_snippets_use_preprocessed_text()
    {
        var normalized = DedupeKey.Compute("rule", "src/file.cs", "Café");
        var compatibility = DedupeKey.Compute("rule", "src/file.cs", "Cafe\u0301\u200B");

        Assert.Equal(normalized, compatibility);
    }
}
