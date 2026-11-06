using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

/// <summary>
/// Runs queued data exports and deletion requests (spec §2 J4). Each request is claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> and finished in one transaction, so two workers never run the same request.
/// </summary>
public sealed partial class PrivacyProcessor(
    IServiceScopeFactory scopes, IExportStorage storage, ContentCatalog content, PrivacyOptions options, TimeProvider clock,
    ILogger<PrivacyProcessor> logger)
{
    private const string PseudonymAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public async Task RunOnceAsync(int max, CancellationToken cancellationToken)
    {
        for (var i = 0; i < max && await ExportNextAsync(cancellationToken); i++)
        {
        }

        for (var i = 0; i < max && await DeleteNextAsync(cancellationToken); i++)
        {
        }
    }

    private async Task<bool> ExportNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var export = await db.DataExportRequests
            .FromSql($"""
                SELECT * FROM vybe.data_export_requests WHERE status = 'queued'
                ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (export is null)
        {
            return false;
        }

        var now = clock.UtcNowForDb();
        try
        {
            var document = await PlayerDataExport.BuildAsync(db, export.PlayerId, now, cancellationToken);
            export.StorageKey = await storage.WriteAsync(export.Id, JsonSerializer.SerializeToUtf8Bytes(document, ApiJson.Options), cancellationToken);
            export.Status = ExportStatus.Ready;
            export.DownloadExpiresAt = now.AddHours(options.ExportDownloadHours);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogExportFailed(logger, exception, export.Id);
            export.Status = ExportStatus.Failed;
            export.FailureCode = "export_failed";
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
        var pseudonym = "d_" + RandomNumberGenerator.GetString(PseudonymAlphabet, 18);
        player.Handle = pseudonym;
        player.HandleNormalized = pseudonym;
        player.DisplayName = null;
        player.AuthSubject = null;
        player.Status = PlayerStatus.Deleted;
        player.DeletedAt = now;
        player.SuspendedUntil = null;
        player.SuspensionReason = null;
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
        await db.DataExportRequests
            .Where(x => x.PlayerId == playerId && (x.Status == ExportStatus.Queued || x.Status == ExportStatus.Running))
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, ExportStatus.Failed)
                .SetProperty(x => x.FailureCode, "player_deleted")
                .SetProperty(x => x.CompletedAt, now), cancellationToken);
        var files = await db.DataExportRequests.Where(x => x.PlayerId == playerId && x.StorageKey != null)
            .Select(x => x.StorageKey!).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // After the commit: a failed transaction must not lose the export of a player who still exists.
        foreach (var file in files)
        {
            await storage.DeleteAsync(file, cancellationToken);
        }

        return true;
    }

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
