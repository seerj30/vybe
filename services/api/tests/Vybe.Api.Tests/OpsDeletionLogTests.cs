using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>A deletion log in memory, so the export job and the replay run against the real database.</summary>
public sealed class InMemoryDeletionLog : IDeletionLog
{
    public ConcurrentDictionary<Guid, DeletionLogEntry> Entries { get; } = new();

    public int Writes { get; private set; }

    public Task WriteAsync(DeletionLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entries[entry.RequestId] = entry;
        Writes++;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Guid> ListRequestIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var id in Entries.Keys)
        {
            yield return id;
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<DeletionLogEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entry in Entries.Values)
        {
            yield return entry;
        }

        await Task.CompletedTask;
    }
}

public sealed class OpsDeletionApiFactory : ApiFactory
{
    public InMemoryDeletionLog Log { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<IDeletionLog>(Log));
    }
}

/// <summary>HIGH review finding: a restore must not bring back the data of a player who asked for deletion.</summary>
public sealed class OpsDeletionLogTests(OpsDeletionApiFactory api) : IClassFixture<OpsDeletionApiFactory>
{
    [Fact]
    public async Task Exporter_CopiesEveryDeletionRequestOnce()
    {
        var player = await Player.CreateGuestAsync(api);
        var requestId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add(Idempotency.Header, requestId.ToString());
        Assert.Equal(HttpStatusCode.Accepted, (await player.Http.SendAsync(request)).StatusCode);
        var exporter = api.Services.GetRequiredService<DeletionLogExporter>();

        await exporter.RunOnceAsync(CancellationToken.None);
        var writes = api.Log.Writes;
        await exporter.RunOnceAsync(CancellationToken.None);

        Assert.Equal(player.Id, api.Log.Entries[requestId].PlayerId);
        Assert.Equal(writes, api.Log.Writes);
    }

    [Fact]
    public async Task Replay_AnonymizesLoggedPlayersThatTheRestoreBroughtBack()
    {
        // A request made after the backup: in the log, not in the restored database.
        var lost = await Player.CreateGuestAsync(api);
        var lostRequest = Guid.NewGuid();
        api.Log.Entries[lostRequest] = new DeletionLogEntry(lostRequest, lost.Id, DateTime.UtcNow);
        // A request that the backup caught while it was being processed.
        var interrupted = await Player.CreateGuestAsync(api);
        var interruptedRequest = Guid.NewGuid();
        await using (var db = api.CreateDbContext())
        {
            db.DeletionRequests.Add(new DeletionRequest
            {
                Id = interruptedRequest,
                PlayerId = interrupted.Id,
                Status = DeletionStatus.Processing,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        api.Log.Entries[interruptedRequest] = new DeletionLogEntry(interruptedRequest, interrupted.Id, DateTime.UtcNow);
        // A player created after the backup does not exist in the restored database.
        var unknownRequest = Guid.NewGuid();
        api.Log.Entries[unknownRequest] = new DeletionLogEntry(unknownRequest, Guid.NewGuid(), DateTime.UtcNow);

        var result = await api.Services.GetRequiredService<DeletionReplay>().RunAsync(CancellationToken.None);

        Assert.Equal(0, result.StillQueued);
        Assert.True(result.UnknownPlayers >= 1);
        await using var check = api.CreateDbContext();
        foreach (var (playerId, requestId) in new[] { (lost.Id, lostRequest), (interrupted.Id, interruptedRequest) })
        {
            var player = await check.Players.AsNoTracking().SingleAsync(p => p.Id == playerId);
            Assert.Equal(PlayerStatus.Deleted, player.Status);
            Assert.StartsWith("d_", player.Handle, StringComparison.Ordinal);
            Assert.Equal(DeletionStatus.Completed, (await check.DeletionRequests.AsNoTracking().SingleAsync(r => r.Id == requestId)).Status);
        }

        // The old guest cookie no longer works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await lost.Http.GetAsync("/api/v1/me")).StatusCode);

        // Running it again changes nothing.
        var again = await api.Services.GetRequiredService<DeletionReplay>().RunAsync(CancellationToken.None);
        Assert.Equal(0, again.Queued);
    }
}
