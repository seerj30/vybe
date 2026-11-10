using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>The container health check command (<c>dotnet Vybe.Api.dll healthcheck</c>) against a real Kestrel port.</summary>
public sealed class OpsHealthProbeTests
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/health/ready")]
    [InlineData("", "http://127.0.0.1:8080/health/ready")]
    [InlineData("9090", "http://127.0.0.1:9090/health/ready")]
    [InlineData("9090;8080", "http://127.0.0.1:9090/health/ready")]
    [InlineData("not-a-port", "http://127.0.0.1:8080/health/ready")]
    public void ReadyUrl_UsesTheFirstHttpPort(string? ports, string expected) =>
        Assert.Equal(new Uri(expected), HealthProbe.ReadyUrl(ports));

    [Fact]
    public void IsCommand_OnlyTheBareCommand()
    {
        Assert.True(HealthProbe.IsCommand(["healthcheck"]));
        Assert.False(HealthProbe.IsCommand([]));
        Assert.False(HealthProbe.IsCommand(["healthcheck", "--urls=http://evil"]));
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK, 0)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, 1)]
    public async Task Run_ExitCodeFollowsTheReadyStatus(int status, int exitCode)
    {
        await using var app = await StartAsync(status);

        Assert.Equal(exitCode, await HealthProbe.RunAsync(ReadyUrlOf(app), TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task Run_NothingListening_IsNotReady()
    {
        Uri url;
        await using (var app = await StartAsync(StatusCodes.Status200OK))
        {
            url = ReadyUrlOf(app);
        }

        Assert.Equal(1, await HealthProbe.RunAsync(url, TimeSpan.FromSeconds(4)));
    }

    private static async Task<WebApplication> StartAsync(int status)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet(Health.ReadyPath, () => Results.StatusCode(status));
        await app.StartAsync();
        return app;
    }

    private static Uri ReadyUrlOf(WebApplication app)
    {
        var address = new Uri(app.Urls.Single());
        return HealthProbe.ReadyUrl(address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
