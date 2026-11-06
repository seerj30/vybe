namespace Vybe.Api.Http;

internal static class Clock
{
    /// <summary>
    /// Server time truncated to microseconds, the precision of PostgreSQL <c>timestamptz</c>, so a value
    /// written and read back compares equal (idempotency leases, repeated start responses).
    /// </summary>
    public static DateTime UtcNowMicros(this TimeProvider clock)
    {
        var ticks = clock.GetUtcNow().UtcTicks;
        return new DateTime(ticks - ticks % 10, DateTimeKind.Utc);
    }

    public static long ToUnixMs(this DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
