namespace Vybe.Api.Infrastructure;

/// <summary>
/// <c>dotnet Vybe.Api.dll healthcheck</c>: the container health check (Docker <c>HEALTHCHECK</c>, compose
/// <c>healthcheck</c>). The chiseled image has no shell and no curl, so the image itself calls
/// <see cref="Health.ReadyPath"/> on its own port. Exit code 0 = ready, 1 = not ready. No host, no configuration,
/// no output: it starts in well under a second.
/// </summary>
public static class HealthProbe
{
    public const string Command = "healthcheck";

    public static bool IsCommand(string[] args) => args is [Command];

    /// <summary>The first port of <c>ASPNETCORE_HTTP_PORTS</c> (the image sets 8080).</summary>
    public static Uri ReadyUrl(string? httpPorts)
    {
        var first = (httpPorts ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var port = int.TryParse(first, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 65535 ? value : 8080;
        return new Uri($"http://127.0.0.1:{port}{Health.ReadyPath}");
    }

    public static async Task<int> RunAsync(Uri url, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = timeout };
        try
        {
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
