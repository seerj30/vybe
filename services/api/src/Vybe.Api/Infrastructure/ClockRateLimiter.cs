using System.Threading.RateLimiting;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// A fixed-window limiter on the application's <see cref="TimeProvider"/>, not on the wall clock, so window
/// ends are deterministic in tests. No queue: a request beyond the limit fails at once with
/// <c>Retry-After</c> = the rest of the window, rounded up to whole seconds.
/// </summary>
public sealed class ClockRateLimiter(int permits, TimeSpan window, TimeProvider clock) : RateLimiter
{
    private readonly Lock _gate = new();
    private DateTimeOffset _windowStart = clock.GetUtcNow();
    private int _used;

    /// <summary>Idle after its window ended; the partitioned limiter then drops it and starts a new one when needed.</summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_gate)
            {
                var elapsed = clock.GetUtcNow() - _windowStart;
                return elapsed >= window ? elapsed - window : null;
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_gate)
        {
            Roll(clock.GetUtcNow());
            return new RateLimiterStatistics { CurrentAvailablePermits = permits - _used };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            Roll(now);
            if (_used + permitCount <= permits)
            {
                _used += permitCount;
                return Lease.Acquired;
            }

            var rest = _windowStart + window - now;
            return new Lease(false, TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(rest.TotalSeconds))));
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AttemptAcquireCore(permitCount));

    private void Roll(DateTimeOffset now)
    {
        if (now - _windowStart >= window)
        {
            _windowStart = now;
            _used = 0;
        }
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public static readonly Lease Acquired = new(true, null);

        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = retryAfter;
            return retryAfter is not null && metadataName == MetadataName.RetryAfter.Name;
        }
    }
}
