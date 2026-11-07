using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Infrastructure;

/// <summary>One J4 deletion request, kept outside the database so a restore can apply it again.</summary>
public sealed record DeletionLogEntry(Guid RequestId, Guid PlayerId, DateTime RequestedAt);

/// <summary>
/// The deletion log (spec §2 J4, backup-restore runbook): a copy of every deletion request outside the
/// database. A database restore brings back players that asked for deletion after the backup; the
/// <c>replay-deletions</c> command reads this log and anonymizes them again before the API takes traffic.
/// Entries hold ids and a time only, no personal data.
/// </summary>
public interface IDeletionLog
{
    /// <summary>Writes the entry; writing it again replaces it.</summary>
    Task WriteAsync(DeletionLogEntry entry, CancellationToken cancellationToken);

    IAsyncEnumerable<Guid> ListRequestIdsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<DeletionLogEntry> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>The deletion log in the export bucket under <c>deletion-log/</c> (not under the export prefix and its sweep).</summary>
public sealed class S3DeletionLog : IDeletionLog, IDisposable
{
    public const string Prefix = "deletion-log/";
    private const string Extension = ".json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _bucket;
    private readonly AmazonS3Client _client;

    public S3DeletionLog(S3ExportStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _bucket = options.Bucket ?? throw new InvalidOperationException($"{S3ExportStorageOptions.Section}:Bucket is not configured");
        _client = S3ExportStorage.CreateClient(options, out _);
    }

    public async Task WriteAsync(DeletionLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucket,
                Key = $"{Prefix}{entry.RequestId:D}{Extension}",
                ContentBody = JsonSerializer.Serialize(entry, Json),
                ContentType = "application/json",
            },
            cancellationToken);
    }

    public async IAsyncEnumerable<Guid> ListRequestIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = Prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                var name = item.Key.AsSpan(Prefix.Length);
                if (name.Length == 36 + Extension.Length && name.EndsWith(Extension, StringComparison.Ordinal)
                    && Guid.TryParseExact(name[..36], "D", out var id))
                {
                    yield return id;
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    public async IAsyncEnumerable<DeletionLogEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var id in ListRequestIdsAsync(cancellationToken))
        {
            using var response = await _client.GetObjectAsync(_bucket, $"{Prefix}{id:D}{Extension}", cancellationToken);
            var entry = await JsonSerializer.DeserializeAsync<DeletionLogEntry>(response.ResponseStream, Json, cancellationToken)
                ?? throw new InvalidDataException($"deletion log entry {id} is empty");
            yield return entry.RequestId == id ? entry : throw new InvalidDataException($"deletion log entry {id} has another request id");
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Copies deletion requests of the last <see cref="WindowDays"/> days that are not in the log yet. Idempotent, so a
/// failed pass is repeated by the next one. The window is longer than the backup retention (35 days).
/// </summary>
public sealed class DeletionLogExporter(IServiceScopeFactory scopes, IDeletionLog log, TimeProvider clock)
{
    public const int WindowDays = 40;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var logged = new HashSet<Guid>();
        await foreach (var id in log.ListRequestIdsAsync(cancellationToken))
        {
            logged.Add(id);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var since = clock.UtcNowForDb().AddDays(-WindowDays);
        var requests = await db.DeletionRequests.AsNoTracking()
            .Where(r => r.CreatedAt >= since)
            .Select(r => new DeletionLogEntry(r.Id, r.PlayerId, r.CreatedAt))
            .ToListAsync(cancellationToken);
        var written = 0;
        foreach (var entry in requests.Where(r => !logged.Contains(r.RequestId)))
        {
            await log.WriteAsync(entry, cancellationToken);
            written++;
        }

        return written;
    }
}

/// <summary>
/// After a restore: every logged deletion whose player is not deleted in this database is queued again
/// (the player becomes <c>deleting</c>, so its cookie and token stop working), then the privacy processor
/// anonymizes it. Players created after the backup are not in the database and are skipped.
/// </summary>
public sealed partial class DeletionReplay(
    IServiceScopeFactory scopes, IDeletionLog log, PrivacyProcessor processor, TimeProvider clock, ILogger<DeletionReplay> logger)
{
    public sealed record Result(int Logged, int Queued, int AlreadyDeleted, int UnknownPlayers, int StillQueued);

    public async Task<Result> RunAsync(CancellationToken cancellationToken)
    {
        int logged = 0, queued = 0, deleted = 0, unknown = 0;
        await foreach (var entry in log.ReadAllAsync(cancellationToken))
        {
            logged++;
            switch (await QueueAsync(entry, cancellationToken))
            {
                case Outcome.Queued: queued++; break;
                case Outcome.AlreadyDeleted: deleted++; break;
                default: unknown++; break;
            }
        }

        // The processor anonymizes up to 100 players per pass; stop when nothing is queued or nothing moves.
        int remaining = await QueuedCountAsync(cancellationToken), before;
        do
        {
            before = remaining;
            await processor.RunOnceAsync(100, cancellationToken);
            remaining = await QueuedCountAsync(cancellationToken);
        }
        while (remaining > 0 && remaining < before);

        var result = new Result(logged, queued, deleted, unknown, remaining);
        LogReplayed(logger, result.Logged, result.Queued, result.AlreadyDeleted, result.UnknownPlayers, result.StillQueued);
        return result;
    }

    private enum Outcome
    {
        Queued,
        AlreadyDeleted,
        UnknownPlayer,
    }

    private async Task<Outcome> QueueAsync(DeletionLogEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var player = await db.Players
            .FromSql($"SELECT * FROM vybe.players WHERE id = {entry.PlayerId} FOR NO KEY UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (player is null)
        {
            return Outcome.UnknownPlayer;
        }

        if (player.Status == PlayerStatus.Deleted)
        {
            return Outcome.AlreadyDeleted;
        }

        var now = clock.UtcNowForDb();
        var request = await db.DeletionRequests.SingleOrDefaultAsync(r => r.Id == entry.RequestId, cancellationToken);
        if (request is null)
        {
            db.DeletionRequests.Add(new DeletionRequest
            {
                Id = entry.RequestId,
                PlayerId = entry.PlayerId,
                Status = DeletionStatus.Queued,
                CreatedAt = entry.RequestedAt,
            });
        }
        else
        {
            request.Status = DeletionStatus.Queued;
            request.CompletedAt = null;
        }

        player.Status = PlayerStatus.Deleting;
        player.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Queued;
    }

    private async Task<int> QueuedCountAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        return await db.DeletionRequests.CountAsync(r => r.Status == DeletionStatus.Queued, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Deletion replay: {Logged} logged, {Queued} queued again, {AlreadyDeleted} already deleted, {UnknownPlayers} players not in this database, {StillQueued} still queued")]
    private static partial void LogReplayed(ILogger logger, int logged, int queued, int alreadyDeleted, int unknownPlayers, int stillQueued);
}

internal sealed partial class DeletionLogWorker(DeletionLogExporter exporter, IConfiguration configuration, ILogger<DeletionLogWorker> logger)
    : BackgroundService
{
    public const string IntervalKey = "Jobs:DeletionLogSeconds";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue(IntervalKey, 300)));
        do
        {
            try
            {
                await exporter.RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Deletion log export failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
