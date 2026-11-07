using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

/// <summary>
/// Runs queued data exports and deletion requests (spec §2 J4), then removes export files that no longer
/// belong to a ready export. Each request is claimed with <c>FOR UPDATE SKIP LOCKED</c> and finished in one
/// transaction, so two workers never run the same request. Lock order in every path: player row first, then
/// export rows (ADR 0005), so an export and a deletion of one player cannot deadlock.
/// </summary>
public sealed partial class PrivacyProcessor(
    IServiceScopeFactory scopes, IExportStorage storage, ContentCatalog content, PrivacyOptions options, TimeProvider clock,
    IAuthUserAdmin authUsers, ILogger<PrivacyProcessor> logger)
{
    public const string PlayerDeletedCode = "player_deleted";
    private const string PseudonymAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly TimeSpan OrphanSweepInterval = TimeSpan.FromHours(1);
    private readonly Lock _sweepGate = new();
    private DateTime _nextOrphanSweep = DateTime.MinValue;

    public async Task RunOnceAsync(int max, CancellationToken cancellationToken)
    {
        var busy = new HashSet<Guid>();
        for (var i = 0; i < max && await ExportNextAsync(busy, cancellationToken); i++)
        {
        }

        for (var i = 0; i < max && await DeleteNextAsync(cancellationToken); i++)
        {
        }

        // The first failure ends the Auth step of this pass: an Auth outage costs one timeout per pass, not max.
        for (var i = 0; i < max && await DeleteAuthUserNextAsync(cancellationToken) == AuthStep.Deleted; i++)
        {
        }

        var failed = new HashSet<Guid>();
        for (var i = 0; i < max && await DeleteFileNextAsync(failed, cancellationToken); i++)
        {
        }

        await SweepOrphansAsync(cancellationToken);
    }

    private async Task<bool> ExportNextAsync(HashSet<Guid> busy, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var candidate = await db.DataExportRequests.AsNoTracking()
            .Where(x => x.Status == ExportStatus.Queued && !busy.Contains(x.Id))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.PlayerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Player row first: FOR SHARE waits for a running anonymization (FOR NO KEY UPDATE).
        var status = (await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {candidate.PlayerId} FOR SHARE")
            .AsNoTracking().SingleAsync(cancellationToken)).Status;
        var export = await db.DataExportRequests
            .FromSql($"SELECT * FROM vybe.data_export_requests WHERE id = {candidate.Id} AND status = 'queued' FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(cancellationToken);
        if (export is null)
        {
            busy.Add(candidate.Id); // another worker has it, or it is already done
            return true;
        }

        var now = clock.UtcNowForDb();
        if (status is PlayerStatus.Deleting or PlayerStatus.Deleted)
        {
            // Never export anonymized data or keep a file of it.
            export.Status = ExportStatus.Failed;
            export.FailureCode = PlayerDeletedCode;
        }
        else
        {
            try
            {
                var document = await PlayerDataExport.BuildAsync(db, export.PlayerId, now, cancellationToken);
                // The key is derived from the export id: if the commit fails, the retry replaces the file, and the
                // orphan sweep removes it if the export never becomes ready.
                export.StorageKey = await storage.WriteAsync(
                    export.Id, JsonSerializer.SerializeToUtf8Bytes(document, ApiJson.Options), cancellationToken);
                export.Status = ExportStatus.Ready;
                export.DownloadExpiresAt = now.AddHours(options.ExportDownloadHours);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogExportFailed(logger, exception, export.Id);
                export.Status = ExportStatus.Failed;
                export.FailureCode = "export_failed";
            }
        }

        export.CompletedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// ADR 0002 #11 anonymization: the player row and its pseudonymous id stay (the ledger references it);
    /// personal fields, avatar, outfits, blocks, report texts and export files are removed.
    /// </summary>
    private async Task<bool> DeleteNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var request = await db.DeletionRequests
            .FromSql($"""
                SELECT * FROM vybe.deletion_requests WHERE status = 'queued'
                ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            return false;
        }

        var playerId = request.PlayerId;
        var now = clock.UtcNowForDb();
        var player = await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE").SingleAsync(cancellationToken);
        if (player.AuthSubject is { } subject)
        {
            // Spec §17 decision 8: the Supabase Auth user (email, login) goes too. The call happens after the
            // commit (DeleteAuthUserNextAsync), so an Auth outage never blocks or undoes the anonymization.
            await AuthUserDeletions.QueueAsync(db, subject, now, cancellationToken);
        }

        var pseudonym = "d_" + RandomNumberGenerator.GetString(PseudonymAlphabet, 18);
        player.Handle = pseudonym;
        player.HandleNormalized = pseudonym;
        player.DisplayName = null;
        player.AuthSubject = null;
        player.Status = PlayerStatus.Deleted;
        player.DeletedAt = now;
        player.SuspendedUntil = null;
        player.SuspensionReason = null;
        player.AgeConfirmedAt = null;
        player.AgePolicyVersion = null;
        player.AgeBand = null;
        player.UpdatedAt = now;

        var defaults = content.Avatar.Defaults;
        var avatar = await db.AvatarConfigs.SingleAsync(a => a.PlayerId == playerId, cancellationToken);
        avatar.BodyPresetId = defaults.BodyPresetId;
        avatar.SkinToneId = defaults.SkinToneId;
        avatar.FacePresetId = defaults.FacePresetId;
        avatar.HairPresetId = defaults.HairPresetId;
        avatar.HairColorId = defaults.HairColorId;
        avatar.EquippedJson = Equipped.Empty.ToJson();
        request.Status = DeletionStatus.Completed;
        request.CompletedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await db.GuestSessions.Where(s => s.PlayerId == playerId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now), cancellationToken);
        await db.SavedOutfits.Where(o => o.PlayerId == playerId).ExecuteDeleteAsync(cancellationToken);
        await db.PlayerBlocks.Where(b => b.BlockerPlayerId == playerId || b.BlockedPlayerId == playerId).ExecuteDeleteAsync(cancellationToken);
        await db.PlayerReports.Where(r => r.ReporterPlayerId == playerId || r.TargetPlayerId == playerId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Details, (string?)null), cancellationToken);
        await db.Challenges
            .Where(c => (c.CreatorPlayerId == playerId || c.RecipientPlayerId == playerId)
                && (c.Status == ChallengeStatus.Open || c.Status == ChallengeStatus.Accepted))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChallengeStatus.Cancelled), cancellationToken);
        // Every export of the player fails; a stored file keeps its key, so DeleteFileNextAsync removes it
        // and retries until the storage confirms. The deletion never depends on the storage being up.
        await db.DataExportRequests
            .Where(x => x.PlayerId == playerId && x.Status != ExportStatus.Failed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, ExportStatus.Failed)
                .SetProperty(x => x.FailureCode, PlayerDeletedCode)
                .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private enum AuthStep
    {
        Nothing,
        Deleted,
        Failed,
    }

    /// <summary>A claimed Auth deletion is not taken by another worker for this long (one HTTP call fits easily).</summary>
    public static readonly TimeSpan AuthClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Deletes one queued Supabase Auth user whose attempt is due. Three short steps, so no transaction or row
    /// lock is held during the HTTP call: claim (move <c>next_attempt_at</c> forward by the lease and commit),
    /// call Auth, then record (delete the row, or count the failure and set the backoff). A worker that stops
    /// after the claim leaves the row for the next pass after the lease. Without a service-role key the rows wait.
    /// </summary>
    private async Task<AuthStep> DeleteAuthUserNextAsync(CancellationToken cancellationToken)
    {
        if (!authUsers.IsConfigured)
        {
            return AuthStep.Nothing;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var now = clock.UtcNowForDb();
        var claimed = (await db.Database.SqlQuery<string>($"""
            WITH due AS (
                SELECT auth_subject FROM vybe.auth_user_deletions WHERE next_attempt_at <= {now}
                ORDER BY next_attempt_at, auth_subject LIMIT 1 FOR UPDATE SKIP LOCKED)
            UPDATE vybe.auth_user_deletions d SET next_attempt_at = {now + AuthClaimLease}
            FROM due WHERE d.auth_subject = due.auth_subject
            RETURNING d.auth_subject AS "Value"
            """).ToListAsync(cancellationToken)).SingleOrDefault();
        if (claimed is null)
        {
            return AuthStep.Nothing;
        }

        if (!Guid.TryParse(claimed, out var userId))
        {
            // Supabase Auth user ids are UUIDs: this subject cannot be an Auth user, so there is nothing to delete.
            LogAuthSubjectNotUuid(logger);
            await db.AuthUserDeletions.Where(d => d.AuthSubject == claimed).ExecuteDeleteAsync(cancellationToken);
            return AuthStep.Deleted;
        }

        try
        {
            await authUsers.DeleteUserAsync(userId, cancellationToken);
        }
        catch (AuthUserDeletionException exception)
        {
            var attempts = await db.AuthUserDeletions.Where(d => d.AuthSubject == claimed).Select(d => d.Attempts).SingleOrDefaultAsync(cancellationToken) + 1;
            var retryAt = clock.UtcNowForDb() + AuthRetryDelay(attempts);
            await db.AuthUserDeletions.Where(d => d.AuthSubject == claimed)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.Attempts, attempts).SetProperty(d => d.NextAttemptAt, retryAt), cancellationToken);
            LogAuthUserDeleteFailed(logger, exception, attempts);
            return AuthStep.Failed;
        }

        await db.AuthUserDeletions.Where(d => d.AuthSubject == claimed).ExecuteDeleteAsync(cancellationToken);
        return AuthStep.Deleted;
    }

    public static readonly TimeSpan MaxAuthRetryDelay = TimeSpan.FromHours(6);

    /// <summary>1, 2, 4 … minutes after the failed attempts, at most <see cref="MaxAuthRetryDelay"/>.</summary>
    public static TimeSpan AuthRetryDelay(int attempts)
    {
        var minutes = Math.Pow(2, Math.Clamp(attempts - 1, 0, 20));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxAuthRetryDelay.TotalMinutes));
    }

    /// <summary>Deletes the file of one failed export (for example of a deleted player) and clears its key.</summary>
    private async Task<bool> DeleteFileNextAsync(HashSet<Guid> failed, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var ids = failed.ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var export = await db.DataExportRequests
            .FromSql($"""
                SELECT * FROM vybe.data_export_requests
                WHERE status = 'failed' AND storage_key IS NOT NULL AND id <> ALL({ids})
                ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (export is null)
        {
            return false;
        }

        try
        {
            await storage.DeleteAsync(export.StorageKey!, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFileDeleteFailed(logger, exception, export.Id);
            failed.Add(export.Id); // the key stays, so the next pass retries
            return true;
        }

        export.StorageKey = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// At most once per hour: deletes stored files whose export is unknown or failed without a key (a file
    /// written by an export whose transaction never committed). Files of queued, running and ready exports stay.
    /// </summary>
    private async Task SweepOrphansAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNowForDb();
        lock (_sweepGate)
        {
            if (now < _nextOrphanSweep)
            {
                return;
            }

            _nextOrphanSweep = now + OrphanSweepInterval;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        await foreach (var (exportId, key) in storage.ListAsync(cancellationToken))
        {
            var row = await db.DataExportRequests.AsNoTracking().Where(x => x.Id == exportId)
                .Select(x => new { x.Status, x.StorageKey }).SingleOrDefaultAsync(cancellationToken);
            if (row is null || (row.Status == ExportStatus.Failed && row.StorageKey is null))
            {
                try
                {
                    await storage.DeleteAsync(key, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFileDeleteFailed(logger, exception, exportId);
                }
            }
        }
    }

    // The user id and the key are never logged: only the attempt count.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting a Supabase Auth user failed (attempt {Attempts}); retrying later")]
    private static partial void LogAuthUserDeleteFailed(ILogger logger, Exception exception, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A queued Supabase Auth user id is not a UUID; the row is removed")]
    private static partial void LogAuthSubjectNotUuid(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting the file of data export {ExportId} failed; retrying later")]
    private static partial void LogFileDeleteFailed(ILogger logger, Exception exception, Guid exportId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Data export {ExportId} failed")]
    private static partial void LogExportFailed(ILogger logger, Exception exception, Guid exportId);
}

/// <summary>Runs <see cref="PrivacyProcessor"/> in the background unless <c>Jobs:Enabled = false</c>.</summary>
internal sealed partial class PrivacyWorker(PrivacyProcessor processor, PrivacyOptions options, ILogger<PrivacyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do
        {
            try
            {
                await processor.RunOnceAsync(100, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPassFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Privacy pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
