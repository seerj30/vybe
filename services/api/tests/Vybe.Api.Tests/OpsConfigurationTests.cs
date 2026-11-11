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
        ["Auth:Supabase:ServiceRoleKey"] = "service-role-key",
        ["Challenges:InviteSecret"] = Convert.ToBase64String(new byte[32]),
        ["Challenges:ShareBaseUrl"] = "https://vybe.example",
        ["Storage:Exports:Bucket"] = "exports",
        ["Storage:Exports:Region"] = "eu-central-1",
        ["Storage:Exports:ServiceUrl"] = "https://ref.storage.supabase.co/storage/v1/s3",
        ["Storage:Exports:AccessKeyId"] = "key-id",
        ["Storage:Exports:SecretAccessKey"] = "key-secret",
        ["Content:Root"] = "/app/content",
        ["Content:AssetBaseUrl"] = "https://vybe.example/assets",
        ["ReverseProxy:KnownNetworks:0"] = "10.0.0.0/8",
    };

    [Fact]
    public void Complete_HasNoProblems() => Assert.Empty(ProductionConfiguration.Problems(Build(Complete)));

    [Theory]
    [InlineData("Host=pooler;Port=6543;Database=postgres;Max Auto Prepare=0", true)]
    [InlineData("Host=pooler;Port=6543;Database=postgres;No Reset On Close=true", false)] // Max Auto Prepare is 0 by default
    [InlineData("Host=pooler;Port=6543;Database=postgres;No Reset On Close=true;Max Auto Prepare=20", true)]
    [InlineData("Host=pooler;Port=6543;Database=postgres;Max Auto Prepare=0;No Reset On Close=true", false)]
    [InlineData("Host=pooler;Port=5432;Database=postgres", false)]
    [InlineData("Host=db;Database=vybe", false)]
    public void TransactionPooler_NeedsNoResetOnCloseAndNoAutoPrepare(string connectionString, bool isProblem)
    {
        var settings = new Dictionary<string, string?>(Complete) { ["ConnectionStrings:Vybe"] = connectionString };

        var problems = ProductionConfiguration.Problems(Build(settings));

        Assert.Equal(isProblem, problems.Any(p => p.Contains("transaction pooler", StringComparison.Ordinal)));
        Assert.DoesNotContain(problems, p => p.Contains("pooler;", StringComparison.Ordinal)); // never the value
    }

    [Fact]
    public void Empty_NamesEveryRequiredSetting()
    {
        var problems = string.Join("\n", ProductionConfiguration.Problems(Build([])));

        foreach (var name in new[]
        {
            "ConnectionStrings:Vybe", "Auth:Supabase:Authority", "Auth:Supabase:ApiKey", "Challenges:InviteSecret",
            "Challenges:ShareBaseUrl", "Storage:Exports:Bucket", "Storage:Exports:Region", "Content:Root", "Content:AssetBaseUrl",
            "ReverseProxy:KnownNetworks", "Auth:Supabase:ServiceRoleKey",
        })
        {
            Assert.Contains(name, problems, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Challenges:InviteSecret", "c2hvcnQ=")] // 5 bytes
    [InlineData("Challenges:InviteSecret", "not base64!")]
    [InlineData("Challenges:ShareBaseUrl", "http://vybe.example")]
    [InlineData("Content:AssetBaseUrl", "http://vybe.example/assets")]
    [InlineData("Content:AssetBaseUrl", "https://cdn.vybe.invalid/assets")]
    [InlineData("Storage:Exports:ServiceUrl", "ftp://storage.example")]
    [InlineData("Storage:Exports:ServiceUrl", "http://storage.example")]
    [InlineData("Storage:Exports:SecretAccessKey", "")]
    [InlineData("Auth:Supabase:ServiceRoleKey", "")]
    [InlineData("Auth:Supabase:Authority", "http://ref.supabase.co/auth/v1")]
    [InlineData("Auth:Supabase:MetadataAddress", "http://ref.supabase.co/auth/v1/.well-known/openid-configuration")]
    [InlineData("Auth:Supabase:Issuer", "http://ref.supabase.co/auth/v1")]
    [InlineData("Auth:Supabase:Authority", "ref.supabase.co/auth/v1")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector.example:4318")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "not a url")]
    public void InvalidValue_IsAProblem(string key, string value)
    {
        var settings = new Dictionary<string, string?>(Complete) { [key] = value };

        Assert.Single(ProductionConfiguration.Problems(Build(settings)));
    }

    [Theory]
    [InlineData("https://collector.example:4318")]
    [InlineData("http://localhost:4318")]
    [InlineData("http://127.0.0.1:4317")]
    public void OtlpEndpoint_HttpsOrLocalCollector_IsAccepted(string endpoint) =>
        Assert.Empty(ProductionConfiguration.Problems(Build(new Dictionary<string, string?>(Complete) { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint })));

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void ProxyNetworks_AnyHop_NeedsTheExplicitSwitch(string network)
    {
        var settings = new Dictionary<string, string?> { [$"{EdgeSecurity.ProxySection}:0"] = network };

        Assert.Throws<InvalidOperationException>(() => EdgeSecurity.ProxyNetworks(Build(settings)));
        settings[EdgeSecurity.TrustAnyHopKey] = "true";
        Assert.Single(EdgeSecurity.ProxyNetworks(Build(settings)));
    }

    [Fact]
    public void ProxyNetworks_NotACidr_Throws() =>
        Assert.Throws<InvalidOperationException>(() => EdgeSecurity.ProxyNetworks(Build(new() { [$"{EdgeSecurity.ProxySection}:0"] = "10.0.0.1/33" })));

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

    [Fact]
    public async Task Development_WithoutObjectStorage_BuildsWithServiceValidation()
    {
        // Development validates every registration at build time (the integration tests run as Testing).
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vybe"] = "Host=127.0.0.1;Port=1",
            ["Content:Root"] = ApiFactory.RepoRoot,
        });
        builder.Services.AddVybeApi(builder.Configuration, builder.Environment);
        builder.AddVybeOperations();

        await using var app = builder.Build();

        Assert.Null(app.Services.GetService(typeof(IDeletionLog)));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_OtherEnvironments_DoNotApply(string environment) =>
        ProductionConfiguration.Validate(Build([]), Env(environment));

    private static IConfiguration Build(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static HostingEnvironment Env(string name) => new() { EnvironmentName = name };
}
