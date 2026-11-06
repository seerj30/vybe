using Vybe.Api.Features;

namespace Vybe.Api.Infrastructure;

/// <summary>Hosting and operations hooks (stream D). <c>Program</c> calls them around the feature setup.</summary>
public static class Operations
{
    /// <summary>Call after <c>AddVybeApi</c>: the export storage registered here replaces the Development default.</summary>
    public static WebApplicationBuilder AddVybeOperations(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var storage = builder.Configuration.GetSection(S3ExportStorageOptions.Section).Get<S3ExportStorageOptions>();
        if (storage is { IsConfigured: true })
        {
            builder.Services.AddSingleton(storage);
            builder.Services.AddSingleton<IExportStorage, S3ExportStorage>();
        }

        builder.Services.AddVybeHealth();
        return builder;
    }
}
