using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class PairLockTests(ApiFactory api)
{
    [Fact]
    public async Task SamePair_InEitherOrder_Blocks_DifferentPair_DoesNot()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await using var holder = api.CreateDbContext();
        await using var holding = await holder.Database.BeginTransactionAsync();
        await PairLock.AcquireAsync(holder, a, b, CancellationToken.None);

        await using var other = api.CreateDbContext();
        await using var waiting = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'");
        await PairLock.AcquireAsync(other, a, Guid.CreateVersion7(), CancellationToken.None); // different pair: free

        var blocked = await Assert.ThrowsAsync<PostgresException>(() => PairLock.AcquireAsync(other, b, a, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);
    }

    [Fact]
    public async Task OutsideATransaction_Throws()
    {
        await using var db = api.CreateDbContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PairLock.AcquireAsync(db, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
    }
}
