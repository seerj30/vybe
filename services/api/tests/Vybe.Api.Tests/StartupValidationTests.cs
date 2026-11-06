using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vybe.Api.Features;
using Vybe.Api.Jobs;

namespace Vybe.Api.Tests;

/// <summary>Configuration that must be valid before the API starts.</summary>
public sealed class StartupValidationTests
{
    private static readonly string Secret = Convert.ToBase64String(new byte[32]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://vybe.example")]
    [InlineData("/challenge")]
    [InlineData("not a url")]
    public void ShareBaseUrl_OutsideDevelopment_MustBeAbsoluteHttps(string? shareBaseUrl) =>
        Assert.Throws<InvalidOperationException>(() => Invites(Environments.Production, shareBaseUrl));

    [Fact]
    public void ShareBaseUrl_Https_BuildsTheShareLink()
    {
        var invites = Invites(Environments.Production, "https://vybe.example/");
        var id = Guid.CreateVersion7();

        Assert.Equal($"https://vybe.example/challenge/{invites.Token(id)}", invites.ShareUrl(id));
    }

    [Fact]
    public void ShareBaseUrl_InDevelopment_HasADefault() =>
        Assert.StartsWith("https://vybe.invalid/challenge/", Invites(Environments.Development, null).ShareUrl(Guid.CreateVersion7()), StringComparison.Ordinal);

    [Theory]
    [InlineData("Jobs:OutboxPollSeconds", "0")]
    [InlineData("Jobs:SweepSeconds", "0")]
    public void JobIntervals_BelowOneSecond_FailValidation(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        using var services = new ServiceCollection().AddVybeJobs(configuration).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<JobOptions>>().Value);
    }

    private static ChallengeInvites Invites(string environment, string? shareBaseUrl) =>
        new(new ChallengeOptions { InviteSecret = Secret, ShareBaseUrl = shareBaseUrl },
            new HostingEnvironment { EnvironmentName = environment },
            NullLogger<ChallengeInvites>.Instance);
}
