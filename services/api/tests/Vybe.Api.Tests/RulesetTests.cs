using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary><c>GET /games/rulesets/{id}</c> and the seeded ruleset row (spec §4, §7).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RulesetTests(ApiFixture api)
{
    [Fact]
    public async Task GetRuleset_SeededStreetHoopsV1_MatchesTheSharedFixtureAndItsCanonicalChecksum()
    {
        using var response = await api.CreateClient().GetAsync("/api/v1/games/rulesets/street-hoops-v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var expected = fixture.RootElement;

        Assert.Equal("street-hoops-v1", body.GetProperty("id").GetString());
        Assert.Equal(expected.GetProperty("gameId").GetString(), body.GetProperty("gameId").GetString());
        Assert.Equal(expected.GetProperty("version").GetInt32(), body.GetProperty("version").GetInt32());
        Assert.Equal(
            CanonicalJson.Serialize(expected.GetProperty("config")),
            CanonicalJson.Serialize(body.GetProperty("config")));
        Assert.Equal(expected.GetProperty("checksum").GetString(), body.GetProperty("checksum").GetString());
        Assert.Equal(CanonicalJson.Checksum(body.GetProperty("config")), body.GetProperty("checksum").GetString());

        // The trace builder of these tests uses the same config.
        var config = body.GetProperty("config").Deserialize<StreetHoopsConfig>(JsonSerializerOptions.Web);
        Assert.Equal(Traces.Config, config);
    }

    [Theory]
    [InlineData("no-such-ruleset")]
    [InlineData("Not_Valid")]
    public async Task GetRuleset_UnknownOrInvalidId_Returns404(string id)
    {
        using var response = await api.CreateClient().GetAsync($"/api/v1/games/rulesets/{id}");
        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    private static string FixturePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packages", "game-rules", "fixtures", "rulesets", "street-hoops-v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("packages/game-rules/fixtures/rulesets/street-hoops-v1.json not found");
    }
}
