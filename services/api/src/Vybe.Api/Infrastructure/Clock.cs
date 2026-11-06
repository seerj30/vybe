namespace Vybe.Api.Infrastructure;

public static class Clock
{
    /// <summary>
    /// The current UTC time at PostgreSQL <c>timestamptz</c> precision (microseconds). A value stored
    /// and read back is then equal to the value returned in the first response (e.g. a repeated start).
    /// </summary>
    public static DateTime UtcNowForDb(this TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % 10), DateTimeKind.Utc);
    }
}
