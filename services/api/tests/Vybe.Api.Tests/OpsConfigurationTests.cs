using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>Production and Staging fail fast on missing settings and name every one of them.</summary>
public sealed class OpsConfigurationTests
{
    private static readonly Dictionary<string, string?> Complete = new()
    {
        ["ConnectionStrings:Vybe"] = "Host=db;Database=vybe",
        ["Auth:Supabase:Authority"] = "https://ref.supabase.co/auth/v1",
        ["Auth:Supabase:ApiKey"] = "publishable-key",
        ["Challenges:InviteSecret"] = Convert.ToBase64String(new byte[32]),
        ["Challenges:ShareBaseUrl"] = "https://vybe.example",
        ["Storage:Exports:Bucket"] = "exports",
        ["Storage:Exports:Region"] = "eu-central-1",
        ["Storage:Exports:ServiceUrl"] = "https://ref.storage.supabase.co/storage/v1/s3",
        ["Storage:Exports:AccessKeyId"] = "key-id",
        ["Storage:Exports:SecretAccessKey"] = "key-secret",
        ["Content:Root"] = "/app/content",
    };

    [Fact]
    public void Complete_HasNoProblems() => Assert.Empty(ProductionConfiguration.Problems(Build(Complete)));

    [Fact]
    public void Empty_NamesEveryRequiredSetting()
    {
        var problems = string.Join("\n", ProductionConfiguration.Problems(Build([])));

        foreach (var name in new[]
        {
            "ConnectionStrings:Vybe", "Auth:Supabase:Authority", "Auth:Supabase:ApiKey", "Challenges:InviteSecret",
            "Challenges:ShareBaseUrl", "Storage:Exports:Bucket", "Storage:Exports:Region", "Content:Root",
        })
        {
            Assert.Contains(name, problems, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Challenges:InviteSecret", "c2hvcnQ=")] // 5 bytes
    [InlineData("Challenges:InviteSecret", "not base64!")]
    [InlineData("Challenges:ShareBaseUrl", "http://vybe.example")]
    [InlineData("Storage:Exports:ServiceUrl", "ftp://storage.example")]
    [InlineData("Storage:Exports:SecretAccessKey", "")]
    public void InvalidValue_IsAProblem(string key, string value)
    {
        var settings = new Dictionary<string, string?>(Complete) { [key] = value };

        Assert.Single(ProductionConfiguration.Problems(Build(settings)));
    }

    [Fact]
    public void Validate_InProduction_ThrowsWithNamesButNoValues()
    {
        var settings = new Dictionary<string, string?>(Complete) { ["Auth:Supabase:ApiKey"] = null, ["Challenges:InviteSecret"] = "c2hvcnQ=" };

        var error = Assert.Throws<InvalidOperationException>(() => ProductionConfiguration.Validate(Build(settings), Env(Environments.Production)));

        Assert.Contains("Auth:Supabase:ApiKey", error.Message, StringComparison.Ordinal);
        Assert.Contains("Challenges:InviteSecret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("c2hvcnQ=", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_InStaging_Applies() =>
        Assert.Throws<InvalidOperationException>(() => ProductionConfiguration.Validate(Build([]), Env(Environments.Staging)));

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_OtherEnvironments_DoNotApply(string environment) =>
        ProductionConfiguration.Validate(Build([]), Env(environment));

    private static IConfiguration Build(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static HostingEnvironment Env(string name) => new() { EnvironmentName = name };
}
