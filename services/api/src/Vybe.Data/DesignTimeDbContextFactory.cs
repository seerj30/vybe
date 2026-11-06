using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vybe.Data;

/// <summary>
/// Used by <c>dotnet ef</c> only. Adding a migration or scripting SQL needs no database; commands
/// that connect (e.g. <c>database update</c>) read <c>VYBE_DB_CONNECTION</c> from the environment.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VybeDbContext>
{
    private const string OfflinePlaceholder = "Host=localhost;Database=vybe_design_time";

    public VybeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("VYBE_DB_CONNECTION") ?? OfflinePlaceholder;
        var builder = VybeDbContextOptions.Configure(new DbContextOptionsBuilder<VybeDbContext>(), connectionString);
        return new VybeDbContext(builder.Options);
    }
}
