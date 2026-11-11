using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

namespace Vybe.Data.Tests;

/// <summary>The provider setup turns GSS encryption off (no Kerberos library load) and keeps every other setting.</summary>
public sealed class ConnectionOptionsTests
{
    private const string Pooler =
        "Host=pooler.example;Port=6543;Database=postgres;Username=vybe_api.ref;Password=secret;SSL Mode=Require;" +
        "Max Auto Prepare=0;Maximum Pool Size=20;No Reset On Close=true";

    [Fact]
    public void WithoutGss_DisablesGss_AndKeepsTheOtherSettings()
    {
        var result = new NpgsqlConnectionStringBuilder(VybeDbContextOptions.WithoutGss(Pooler));

        Assert.Equal(GssEncryptionMode.Disable, result.GssEncryptionMode);
        Assert.Equal(6543, result.Port);
        Assert.True(result.NoResetOnClose);
        Assert.Equal(0, result.MaxAutoPrepare);
        Assert.Equal(20, result.MaxPoolSize);
        Assert.Equal(SslMode.Require, result.SslMode);
        Assert.Equal("secret", result.Password);
    }

    [Fact]
    public void Configure_UsesTheConnectionStringWithoutGss()
    {
        var options = VybeDbContextOptions.Configure(new DbContextOptionsBuilder<VybeDbContext>(), Pooler).Options;
#pragma warning disable EF1001 // the internal extension holds the connection string the provider uses
        var used = options.FindExtension<NpgsqlOptionsExtension>()!.ConnectionString!;
#pragma warning restore EF1001

        Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(used).GssEncryptionMode);
    }
}
