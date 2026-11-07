using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vybe.Data;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Probes for the container platform. <c>/health/live</c>: the process answers (no dependencies), so a
/// database outage does not restart every instance. <c>/health/ready</c>: the database answers and has
/// every migration of this build, so traffic waits until the migration step has run.
/// The responses are plain <c>Healthy</c>/<c>Unhealthy</c> without details.
/// </summary>
public static class Health
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    private const string ReadyTag = "ready";

    public static IServiceCollection AddVybeHealth(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseReadyCheck>("database", HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(5));
        return services;
    }

    public static WebApplication MapVybeHealth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        return app;
    }
}

public sealed partial class DatabaseReadyCheck(VybeDbContext db, ILogger<DatabaseReadyCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("database unavailable");
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            if (pending > 0)
            {
                LogPendingMigrations(logger, pending);
                return HealthCheckResult.Unhealthy("pending migrations");
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCheckFailed(logger, exception);
            return HealthCheckResult.Unhealthy("database check failed");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not ready: {Count} migrations are not applied; run the migrate command")]
    private static partial void LogPendingMigrations(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Readiness check of the database failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
