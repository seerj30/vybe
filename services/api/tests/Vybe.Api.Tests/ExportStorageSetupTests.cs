using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Vybe.Api.Features;

namespace Vybe.Api.Tests;

/// <summary>LocalExportStorage is for Development only; elsewhere the API must not start without real storage.</summary>
public sealed class ExportStorageSetupTests
{
    [Fact]
    public void Development_UsesLocalStorage() =>
        Assert.IsType<LocalExportStorage>(Resolve(Environments.Development));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OtherEnvironments_WithoutStorage_FailToResolve(string environment) =>
        Assert.Contains("no export storage", Assert.Throws<InvalidOperationException>(() => Resolve(environment)).Message, StringComparison.Ordinal);

    [Fact]
    public void RegisteredStorage_IsKept()
    {
        var storage = new TestExportStorage(Path.GetTempPath());
        var services = new ServiceCollection().AddSingleton<IExportStorage>(storage).AddSingleton(new PrivacyOptions());
        services.AddExportStorage(new HostingEnvironment { EnvironmentName = Environments.Production });
        using var provider = services.BuildServiceProvider();
        Assert.Same(storage, provider.GetRequiredService<IExportStorage>());
    }

    private static IExportStorage Resolve(string environment)
    {
        var services = new ServiceCollection().AddSingleton(new PrivacyOptions());
        services.AddExportStorage(new HostingEnvironment { EnvironmentName = environment });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IExportStorage>();
    }
}
