using Vybe.Api.Features;

namespace Vybe.Api.Tests;

/// <summary><see cref="LocalExportStorage"/> with a switch that makes deletes fail, as an unavailable object store would.</summary>
public sealed class TestExportStorage(string directory) : IExportStorage
{
    private readonly LocalExportStorage _inner = new(new PrivacyOptions { ExportDirectory = directory });

    public bool FailDeletes { get; set; }

    public Task<string> WriteAsync(Guid exportId, byte[] content, CancellationToken cancellationToken) =>
        _inner.WriteAsync(exportId, content, cancellationToken);

    public Uri DownloadUrl(string storageKey, DateTime expiresAt) => _inner.DownloadUrl(storageKey, expiresAt);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) =>
        FailDeletes ? throw new IOException("storage unavailable") : _inner.DeleteAsync(storageKey, cancellationToken);

    public IAsyncEnumerable<(Guid ExportId, string StorageKey)> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    public bool Exists(Guid exportId) => File.Exists(Path.Combine(directory, $"{exportId}.json"));
}
