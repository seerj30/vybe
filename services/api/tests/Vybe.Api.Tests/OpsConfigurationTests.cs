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
        ["ReverseProxy:KnownNetworks:0"] = "10.0.0.0/8",
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
            "ReverseProxy:KnownNetworks",
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
    [InlineData("Storage:Exports:ServiceUrl", "http://storage.example")]
    [InlineData("Storage:Exports:SecretAccessKey", "")]
    [InlineData("Auth:Supabase:Authority", "http://ref.supabase.co/auth/v1")]
    [InlineData("Auth:Supabase:MetadataAddress", "http://ref.supabase.co/auth/v1/.well-known/openid-configuration")]
    [InlineData("Auth:Supabase:Issuer", "http://ref.supabase.co/auth/v1")]
    [InlineData("Auth:Supabase:Authority", "ref.supabase.co/auth/v1")]
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

    [Theory]
    [InlineData("Staging")]
    [InlineData("Prod")]
    [InlineData("preview")]
    public void Validate_EveryDeployedEnvironment_Applies(string environment) =>
        Assert.Throws<InvalidOperationException>(() => ProductionConfiguration.Validate(Build([]), Env(environment)));

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_OtherEnvironments_DoNotApply(string environment) =>
        ProductionConfiguration.Validate(Build([]), Env(environment));

    private static IConfiguration Build(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static HostingEnvironment Env(string name) => new() { EnvironmentName = name };
}
