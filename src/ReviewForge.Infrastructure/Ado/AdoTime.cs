namespace ReviewForge.Infrastructure.Ado;

public static class AdoTime
{
    public static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
        // ADO returns DateTimeKind.Unspecified for its wire timestamps; they are UTC.
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };
}