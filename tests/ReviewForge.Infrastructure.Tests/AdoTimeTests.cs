using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class AdoTimeTests
{
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ToUtc_normalizes_to_zero_offset(DateTimeKind kind)
    {
        var dt = new DateTime(2026, 9, 17, 12, 0, 0, kind);

        var result = AdoTime.ToUtc(dt);

        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(2026, result.Year);
    }
}