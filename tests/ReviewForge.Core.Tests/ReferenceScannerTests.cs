using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ReferenceScannerTests
{
    [Fact]
    public void ScanLines_matches_boundaries_case_insensitively()
    {
        var matches = ReferenceScanner.ScanLines(["Foo", "FooBar", "MyFoo", "x.Foo()"], "Foo").ToArray();
        Assert.Equal([1, 4], matches.Select(m => m.LineNo));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("123Foo")]
    [InlineData("Foo-")]
    [InlineData("Foo.")]
    [InlineData(".Foo")]
    [InlineData("Foo..Bar")]
    [InlineData("")]
    public void IsValidIdentifier_rejects_invalid_shapes(string identifier)
        => Assert.False(ReferenceScanner.IsValidIdentifier(identifier));

    [Fact]
    public void IsValidIdentifier_rejects_overlong_identifier()
        => Assert.False(ReferenceScanner.IsValidIdentifier(new string('a', 129)));
}