using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vybe.GameRules.Tests;

/// <summary>Loads the shared golden fixtures from packages/game-rules/fixtures.</summary>
internal static class FixtureFiles
{
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static readonly string Root = FindFixturesRoot();

    public static string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public static JsonDocument ReadDocument(string relative) => JsonDocument.Parse(File.ReadAllText(Path(relative)));

    public static T Read<T>(string relative) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path(relative)), JsonOptions)
        ?? throw new InvalidDataException($"fixture {relative} is empty");

    public static IEnumerable<string> List(string directory) =>
        Directory.EnumerateFiles(Path(directory), "*.json")
            .Select(f => $"{directory}/{System.IO.Path.GetFileName(f)}")
            .Order(StringComparer.Ordinal);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private static string FindFixturesRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "packages", "game-rules", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("packages/game-rules/fixtures not found above the test output directory");
    }
}

internal sealed record Fixture(
    string Name,
    string Description,
    IReadOnlyList<Waypoint> Waypoints,
    EvaluateInput Input,
    JsonElement Expected);

internal sealed record PcgVector(string InitState, string InitSeq, string Source, IReadOnlyList<uint> Outputs);

internal sealed record PcgFile(IReadOnlyList<PcgVector> Vectors);

internal sealed record RulesetFile(string Id, string GameId, int Version, StreetHoopsConfig Config, string Checksum);
