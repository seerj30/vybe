namespace Vybe.Api.Features;

public sealed class PrivacyOptions
{
    public const string Section = "Privacy";

    /// <summary>Directory of <see cref="LocalExportStorage"/>. Production storage replaces it (stream D).</summary>
    public string ExportDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "vybe-exports");

    /// <summary>How long a ready export can be downloaded (provisional).</summary>
    public int ExportDownloadHours { get; init; } = 24;

    public int PollSeconds { get; init; } = 10;
}

/// <summary>Where data exports are stored (spec §2 J4). Production uses object storage with signed URLs.</summary>
public interface IExportStorage
{
    /// <summary>Stores the export and returns its storage key.</summary>
    Task<string> WriteAsync(Guid exportId, byte[] content, CancellationToken cancellationToken);

    /// <summary>A short-lived URL that only the export's player receives.</summary>
    Uri DownloadUrl(string storageKey, DateTime expiresAt);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

/// <summary>Development storage: files in a local directory; the URL is a <c>file:</c> URI.</summary>
public sealed class LocalExportStorage(PrivacyOptions options) : IExportStorage
{
    public async Task<string> WriteAsync(Guid exportId, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ExportDirectory);
        var key = $"{exportId}.json";
        await File.WriteAllBytesAsync(PathOf(key), content, cancellationToken);
        return key;
    }

    public Uri DownloadUrl(string storageKey, DateTime expiresAt) => new(PathOf(storageKey));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(storageKey));
        return Task.CompletedTask;
    }

    // Keys are written by this class only ("{guid}.json"); a different shape is a bug, not user input.
    private string PathOf(string key) =>
        key.Length == 41 && Guid.TryParse(key.AsSpan(0, 36), out _) && key.EndsWith(".json", StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(options.ExportDirectory, key))
            : throw new ArgumentException("not a storage key of this storage", nameof(key));
}
