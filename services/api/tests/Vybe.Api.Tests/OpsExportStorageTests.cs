using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.Minio;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary><see cref="S3ExportStorage"/> against a real S3-compatible store (MinIO).</summary>
public sealed class OpsExportStorageTests : IAsyncLifetime, IDisposable
{
    // The official minio/minio image is no longer on Docker Hub; pgsty/minio is a maintained build of the same server.
    public const string MinioImage = "pgsty/minio:RELEASE.2026-08-04T00-00-00Z";
    private const string Bucket = "vybe-exports";
    private readonly MinioContainer _minio = new MinioBuilder(MinioImage).Build();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private S3ExportStorage? _storage;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        using var admin = new AmazonS3Client(
            new BasicAWSCredentials(_minio.GetAccessKey(), _minio.GetSecretKey()),
            new AmazonS3Config { ServiceURL = _minio.GetConnectionString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await admin.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        _storage = new S3ExportStorage(Options(), _clock);
    }

    public void Dispose() => _storage?.Dispose();

    public async Task DisposeAsync() => await _minio.DisposeAsync();

    [Fact]
    public async Task Write_SignedUrl_DownloadsTheContent()
    {
        var exportId = Guid.CreateVersion7();
        var key = await _storage!.WriteAsync(exportId, Encoding.UTF8.GetBytes("{\"a\":1}"), CancellationToken.None);
        using var http = new HttpClient();

        var url = _storage!.DownloadUrl(key, _clock.GetUtcNow().UtcDateTime.AddHours(24));
        using var response = await http.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"a\":1}", await response.Content.ReadAsStringAsync());
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Equal($"exports/{exportId:D}.json", key);
    }

    [Fact]
    public async Task SignedUrl_IsValidForAtMostTenMinutes()
    {
        var key = await _storage!.WriteAsync(Guid.CreateVersion7(), [1], CancellationToken.None);

        var url = _storage!.DownloadUrl(key, _clock.GetUtcNow().UtcDateTime.AddHours(24));
        var shorter = _storage!.DownloadUrl(key, _clock.GetUtcNow().UtcDateTime.AddMinutes(3));

        // The SDK signs with the wall clock, which is a few seconds past the test clock.
        Assert.InRange(ExpiresSeconds(url), 590, 600);
        Assert.InRange(ExpiresSeconds(shorter), 170, 180);
        Assert.Throws<InvalidOperationException>(() => _storage!.DownloadUrl(key, _clock.GetUtcNow().UtcDateTime.AddSeconds(-1)));
    }

    [Fact]
    public async Task List_ReturnsExportObjects_Delete_IsRepeatable()
    {
        var exportId = Guid.CreateVersion7();
        var key = await _storage!.WriteAsync(exportId, [1], CancellationToken.None);

        Assert.Contains((exportId, key), await ListAsync());

        await _storage!.DeleteAsync(key, CancellationToken.None);
        await _storage!.DeleteAsync(key, CancellationToken.None);

        Assert.DoesNotContain((exportId, key), await ListAsync());
    }

    [Fact]
    public async Task Write_SameExportTwice_ReplacesTheObject()
    {
        var exportId = Guid.CreateVersion7();
        await _storage!.WriteAsync(exportId, [1], CancellationToken.None);
        await _storage!.WriteAsync(exportId, [2], CancellationToken.None);

        Assert.Single(await ListAsync(), e => e.ExportId == exportId);
    }

    [Theory]
    [InlineData("exports/../secret.json")]
    [InlineData("other/0198f0a4-0000-7000-8000-000000000000.json")]
    [InlineData("exports/not-a-guid.json")]
    public void ForeignKey_IsRejected(string key) =>
        Assert.Throws<ArgumentException>(() => _storage!.DownloadUrl(key, DateTime.UtcNow.AddMinutes(5)));

    [Fact]
    public async Task DeletionLog_WritesListsAndReadsEntries_NotSeenAsExports()
    {
        using var log = new S3DeletionLog(Options());
        var entry = new DeletionLogEntry(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));

        await log.WriteAsync(entry, CancellationToken.None);
        await log.WriteAsync(entry, CancellationToken.None);
        var entries = new List<DeletionLogEntry>();
        await foreach (var read in log.ReadAllAsync(CancellationToken.None))
        {
            entries.Add(read);
        }

        Assert.Equal(entry, Assert.Single(entries, e => e.RequestId == entry.RequestId));
        Assert.DoesNotContain(await ListAsync(), e => e.ExportId == entry.RequestId);
    }

    // The weekly database dumps share the bucket (docs/runbooks/backup-restore.md); the orphan sweep deletes only
    // what ListAsync returns, so it must never see them, also with an empty export prefix.
    [Theory]
    [InlineData("exports/")]
    [InlineData("")]
    public async Task List_NeverReturnsBackups(string prefix)
    {
        using var storage = new S3ExportStorage(new S3ExportStorageOptions
        {
            ServiceUrl = _minio.GetConnectionString(),
            Region = "us-east-1",
            Bucket = Bucket,
            AccessKeyId = _minio.GetAccessKey(),
            SecretAccessKey = _minio.GetSecretKey(),
            Prefix = prefix,
        }, _clock);
        using var admin = new AmazonS3Client(
            new BasicAWSCredentials(_minio.GetAccessKey(), _minio.GetSecretKey()),
            new AmazonS3Config { ServiceURL = _minio.GetConnectionString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        foreach (var key in new[] { "backups/vybe-20261110T031700Z.dump.age", $"backups/{Guid.NewGuid():D}.json" })
        {
            await admin.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, ContentBody = "x" });
        }

        var exportId = Guid.CreateVersion7();
        var exportKey = await storage.WriteAsync(exportId, [1], CancellationToken.None);
        var listed = new List<string>();
        await foreach (var (_, key) in storage.ListAsync(CancellationToken.None))
        {
            listed.Add(key);
        }

        Assert.Contains(exportKey, listed);
        Assert.DoesNotContain(listed, key => key.StartsWith("backups/", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteOptions_FailOnConstruction() =>
        Assert.Throws<InvalidOperationException>(() => new S3ExportStorage(new S3ExportStorageOptions { Bucket = Bucket }, _clock));

    private static int ExpiresSeconds(Uri url) =>
        int.Parse(System.Web.HttpUtility.ParseQueryString(url.Query)["X-Amz-Expires"]!, System.Globalization.CultureInfo.InvariantCulture);

    private S3ExportStorageOptions Options() => new()
    {
        ServiceUrl = _minio.GetConnectionString(),
        Region = "us-east-1",
        Bucket = Bucket,
        AccessKeyId = _minio.GetAccessKey(),
        SecretAccessKey = _minio.GetSecretKey(),
    };

    private async Task<List<(Guid ExportId, string StorageKey)>> ListAsync()
    {
        var all = new List<(Guid, string)>();
        await foreach (var entry in _storage!.ListAsync(CancellationToken.None))
        {
            all.Add(entry);
        }

        return all;
    }
}
