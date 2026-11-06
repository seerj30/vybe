using System.Net;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>Container probes: anonymous, no details, readiness checks the migrated database.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class OpsHealthTests(ApiFactory api)
{
    [Theory]
    [InlineData(Health.LivePath)]
    [InlineData(Health.ReadyPath)]
    public async Task Probe_IsAnonymousAndHealthy(string path)
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
