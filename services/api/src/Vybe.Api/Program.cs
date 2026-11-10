using Vybe.Api;
using Vybe.Api.Infrastructure;

// Container health check (the chiseled image has no curl): calls /health/ready on the local port and exits.
if (HealthProbe.IsCommand(args))
{
    return await HealthProbe.RunAsync(HealthProbe.ReadyUrl(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")), TimeSpan.FromSeconds(4));
}

// Deploy steps (migrate, seed) run as their own process and exit; the API never migrates at start-up.
if (OpsCommands.IsCommand(args))
{
    return await OpsCommands.RunAsync(args);
}

try
{
    var builder = WebApplication.CreateBuilder(args);
    ProductionConfiguration.Validate(builder.Configuration, builder.Environment);
    builder.Services.AddVybeApi(builder.Configuration, builder.Environment);
    builder.AddVybeOperations();

    var app = builder.Build();
    app.UseVybeEdge(); // before UseVybeApi: forwarded headers, security headers and CORS come first
    app.UseVybeApi();
    app.MapVybeHealth();
    await app.SeedContentIfConfiguredAsync();
    await app.RunAsync();
    return 0;
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    // As PID 1 in a container an unhandled exception can hang instead of exiting; exit with a code.
    await Console.Error.WriteLineAsync($"The VYBE API stopped: {exception}");
    return 1;
}

/// <summary>Entry point; public for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
