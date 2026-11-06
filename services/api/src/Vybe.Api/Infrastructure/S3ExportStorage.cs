using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Vybe.Api.Features;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Settings of <see cref="S3ExportStorage"/> (section <c>Storage:Exports</c>). Any S3-compatible store:
/// Supabase Storage (<c>https://{ref}.storage.supabase.co/storage/v1/s3</c>), AWS S3, MinIO.
/// </summary>
public sealed class S3ExportStorageOptions
{
    public const string Section = "Storage:Exports";

    /// <summary>Endpoint of an S3-compatible store; empty for AWS S3 (the region selects the endpoint).</summary>
    public string? ServiceUrl { get; init; }

    public string? Region { get; init; }

    public string? Bucket { get; init; }

    /// <summary>Access key pair; both empty = the SDK's default credential chain (for example a workload role).</summary>
    public string? AccessKeyId { get; init; }

    public string? SecretAccessKey { get; init; }

    /// <summary>Supabase Storage and MinIO need path-style addressing.</summary>
    public bool ForcePathStyle { get; init; } = true;

    public string Prefix { get; init; } = "exports/";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Bucket);

    /// <summary>The names of missing or invalid settings (never their values).</summary>
    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
        {
            yield return $"{Section}:Bucket is not configured";
        }

        if (string.IsNullOrWhiteSpace(Region))
        {
            yield return $"{Section}:Region is not configured";
        }

        if (!string.IsNullOrEmpty(ServiceUrl)
            && !(Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
        {
            yield return $"{Section}:ServiceUrl must be an absolute http(s) URL";
        }

        if (string.IsNullOrEmpty(AccessKeyId) != string.IsNullOrEmpty(SecretAccessKey))
        {
            yield return $"{Section}:AccessKeyId and {Section}:SecretAccessKey must be set together";
        }
    }
}

/// <summary>
/// Production export storage (spec §2 J4): one private object per export, presigned GET URLs valid for
/// at most <see cref="MaxUrlLifetime"/>. The objects stay private; nobody can list or read them without a URL.
/// </summary>
public sealed class S3ExportStorage : IExportStorage, IDisposable
{
    public static readonly TimeSpan MaxUrlLifetime = TimeSpan.FromMinutes(10);
    private const string Extension = ".json";

    private readonly S3ExportStorageOptions _options;
    private readonly TimeProvider _clock;
    private readonly AmazonS3Client _client;
    private readonly Protocol _protocol;

    public S3ExportStorage(S3ExportStorageOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = options.Problems().ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", problems));
        }

        _options = options;
        _clock = clock;
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            // S3-compatible stores do not all accept the SDK v4 default CRC checksum headers.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (string.IsNullOrEmpty(options.ServiceUrl))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
            _protocol = Protocol.HTTPS;
        }
        else
        {
            config.ServiceURL = options.ServiceUrl;
            _protocol = new Uri(options.ServiceUrl).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
        }

        _client = string.IsNullOrEmpty(options.AccessKeyId)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    public async Task<string> WriteAsync(Guid exportId, byte[] content, CancellationToken cancellationToken)
    {
        var key = KeyOf(exportId);
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(
            new PutObjectRequest { BucketName = _options.Bucket, Key = key, InputStream = stream, ContentType = "application/json" },
            cancellationToken);
        return key;
    }

    public Uri DownloadUrl(string storageKey, DateTime expiresAt)
    {
        EnsureKey(storageKey);
        var now = _clock.GetUtcNow().UtcDateTime;
        var limit = now + MaxUrlLifetime;
        var expires = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc) < limit ? DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc) : limit;
        if (expires <= now)
        {
            throw new InvalidOperationException("the export download window has ended");
        }

        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = storageKey,
            Verb = HttpVerb.GET,
            Protocol = _protocol,
            Expires = expires,
            ResponseHeaderOverrides = { ContentDisposition = "attachment; filename=\"vybe-export.json\"" },
        });
        return new Uri(url);
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        EnsureKey(storageKey);
        // S3 answers 204 for a missing object, so a repeated delete is not an error.
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.Bucket, Key = storageKey }, cancellationToken);
    }

    /// <summary>Every export object with its export id (orphan sweep). Objects with other names are skipped.</summary>
    public async IAsyncEnumerable<(Guid ExportId, string StorageKey)> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = _options.Bucket, Prefix = _options.Prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                if (TryParseKey(item.Key, out var exportId))
                {
                    yield return (exportId, item.Key);
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    public void Dispose() => _client.Dispose();

    private string KeyOf(Guid exportId) => $"{_options.Prefix}{exportId:D}{Extension}";

    private bool TryParseKey(string key, out Guid exportId)
    {
        exportId = Guid.Empty;
        return key.Length == _options.Prefix.Length + 36 + Extension.Length
            && key.StartsWith(_options.Prefix, StringComparison.Ordinal)
            && key.EndsWith(Extension, StringComparison.Ordinal)
            && Guid.TryParseExact(key.AsSpan(_options.Prefix.Length, 36), "D", out exportId);
    }

    // Keys are written by this class only; a different shape is a bug, not user input.
    private void EnsureKey(string key)
    {
        if (!TryParseKey(key, out _))
        {
            throw new ArgumentException("not a storage key of this storage", nameof(key));
        }
    }
}
