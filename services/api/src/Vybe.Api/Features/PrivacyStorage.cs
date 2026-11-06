using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vybe.Api.Features;

public sealed class PrivacyOptions
{
    public const string Section = "Privacy";

    /// <summary>Directory of <see cref="LocalExportStorage"/> (Development only).</summary>
    public string ExportDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "vybe-exports");

    /// <summary>How long a ready export can be downloaded (provisional).</summary>
    public int ExportDownloadHours { get; init; } = 24;

    public int PollSeconds { get; init; } = 10;
}

/// <summary>Where data exports are stored (spec §2 J4). Production uses object storage with signed URLs (stream D).</summary>
public interface IExportStorage
{
    /// <summary>Stores the export and returns its storage key. Writing the same export again replaces it.</summary>
    Task<string> WriteAsync(Guid exportId, byte[] content, CancellationToken cancellationToken);

    /// <summary>A URL that is valid until <paramref name="expiresAt"/> and that only the export's player receives.</summary>
    Uri DownloadUrl(string storageKey, DateTime expiresAt);

    /// <summary>Deletes the object; a missing object is not an error.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Every stored object with the export id it belongs to (orphan sweep).</summary>
    IAsyncEnumerable<(Guid ExportId, string StorageKey)> ListAsync(CancellationToken cancellationToken);
}

public static class ExportStorageSetup
{
    /// <summary>
    /// Development uses <see cref="LocalExportStorage"/>. Every other environment must register its own
    /// <see cref="IExportStorage"/> (stream D); until then, resolving it fails, and the API resolves it at start-up.
    /// </summary>
    public static IServiceCollection AddExportStorage(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsDevelopment())
        {
            services.TryAddSingleton<IExportStorage, LocalExportStorage>();
        }
        else
        {
            services.TryAddSingleton<IExportStorage>(_ => throw new InvalidOperationException(
                "no export storage is configured: LocalExportStorage is for Development only; register production object storage"));
        }

        return services;
    }
}

/// <summary>Development storage: files in a local directory; the URL is a <c>file:</c> URI and cannot expire.</summary>
public sealed class LocalExportStorage(PrivacyOptions options) : IExportStorage
{
    private const string Extension = ".json";

    public async Task<string> WriteAsync(Guid exportId, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ExportDirectory);
        var key = $"{exportId}{Extension}";
        await File.WriteAllBytesAsync(PathOf(key), content, cancellationToken);
        return key;
    }

    public Uri DownloadUrl(string storageKey, DateTime expiresAt) => new(PathOf(storageKey));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(storageKey));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(Guid ExportId, string StorageKey)> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.ExportDirectory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(options.ExportDirectory, "*" + Extension))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFileName(path);
            if (IsKey(key))
            {
                yield return (Guid.Parse(key.AsSpan(0, 36)), key);
            }
        }

        await Task.CompletedTask;
    }

    // Keys are written by this class only ("{guid}.json"); a different shape is a bug, not user input.
    private string PathOf(string key) =>
        IsKey(key)
            ? Path.GetFullPath(Path.Combine(options.ExportDirectory, key))
            : throw new ArgumentException("not a storage key of this storage", nameof(key));

    private static bool IsKey(string key) =>
        key.Length == 36 + Extension.Length && Guid.TryParseExact(key.AsSpan(0, 36), "D", out _)
        && key.EndsWith(Extension, StringComparison.Ordinal);
}
