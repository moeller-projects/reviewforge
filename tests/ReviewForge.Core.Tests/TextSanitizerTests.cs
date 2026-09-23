using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class TextSanitizerTests
{
    public static TheoryData<string> ForbiddenBmp => new()
    {
        "\u00AD", "\u034F", "\u115F", "\u1160", "\u180E",
        "\u200B", "\u200C", "\u200D", "\u200E", "\u200F",
        "\u202A", "\u202B", "\u202C", "\u202D", "\u202E",
        "\u2060", "\u2061", "\u2062", "\u2063", "\u2064",
        "\u2066", "\u2067", "\u2068", "\u2069", "\u2800", "\u3164", "\uFFA0", "\uFE00", "\uFE0F", "\uFEFF"
    };

    [Fact]
    public void Clean_input_returns_same_instance()
    {
        var input = new string('a', 200);

        var result = TextSanitizer.Sanitize(input);

        Assert.Same(input, result);
        Assert.False(TextSanitizer.ContainsInvisible(input));
    }

    [Theory]
    [MemberData(nameof(ForbiddenBmp))]
    public void Forbidden_bmp_characters_are_removed(string forbidden)
    {
        var input = $"abc{forbidden}def";

        var result = TextSanitizer.Sanitize(input);

        Assert.Equal("abcdef", result);
        Assert.True(TextSanitizer.ContainsInvisible(input));
        Assert.Equal(!ReferenceEquals(input, result), TextSanitizer.ContainsInvisible(input));
    }

    [Theory]
    [InlineData("\u200A")]
    [InlineData("\u2010")]
    [InlineData("\uFE10")]
    public void Nearby_bmp_characters_are_preserved(string value)
    {
        var input = $"a{value}b";

        Assert.Equal(input, TextSanitizer.Sanitize(input));
        Assert.False(TextSanitizer.ContainsInvisible(input));
    }

    [Fact]
    public void Nearby_astral_character_is_preserved()
    {
        var input = $"a{char.ConvertFromUtf32(0xE0080)}b";

        Assert.Equal(input, TextSanitizer.Sanitize(input));
        Assert.False(TextSanitizer.ContainsInvisible(input));
    }

    [Fact]
    public void Astral_tags_are_removed_but_emoji_survives()
    {
        var tag = char.ConvertFromUtf32(0xE0001);
        var emoji = char.ConvertFromUtf32(0x1F600);

        Assert.Equal($"a{emoji}b", TextSanitizer.Sanitize($"a{tag}{emoji}b"));
        Assert.True(TextSanitizer.ContainsInvisible(tag));
        Assert.False(TextSanitizer.ContainsInvisible(emoji));
    }

    [Fact]
    public void Lone_surrogates_pass_through()
    {
        Assert.Equal("\uD800", TextSanitizer.Sanitize("\uD800"));
        Assert.Equal("abc\uDC00", TextSanitizer.Sanitize("abc\uDC00"));
    }

    [Fact]
    public void Null_and_empty_are_empty()
    {
        Assert.Equal(string.Empty, TextSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, TextSanitizer.Sanitize(string.Empty));
        Assert.False(TextSanitizer.ContainsInvisible(null));
    }
}
