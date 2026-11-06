using Microsoft.Extensions.Options;

namespace Vybe.Api.Jobs;

public sealed class JobOptions
{
    public const string Section = "Jobs";

    /// <summary>Runs the background workers in this process. Tests turn it off and run the jobs directly.</summary>
    public bool Enabled { get; init; } = true;

    public int OutboxPollSeconds { get; init; } = 5;

    public int SweepSeconds { get; init; } = 30;
}

public static class JobsSetup
{
    public static IServiceCollection AddVybeJobs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<JobOptions>(configuration.GetSection(JobOptions.Section));
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<Sweeper>();
        if (configuration.GetSection(JobOptions.Section).Get<JobOptions>()?.Enabled ?? true)
        {
            services.AddHostedService<OutboxWorker>();
            services.AddHostedService<SweeperWorker>();
        }

        return services;
    }
}

internal sealed partial class OutboxWorker(OutboxProcessor processor, IOptions<JobOptions> options, ILogger<OutboxWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.OutboxPollSeconds));
        do
        {
            try
            {
                await processor.RunOnceAsync(100, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPassFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}

internal sealed partial class SweeperWorker(Sweeper sweeper, IOptions<JobOptions> options, ILogger<SweeperWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SweepSeconds));
        do
        {
            try
            {
                await sweeper.RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSweepFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
