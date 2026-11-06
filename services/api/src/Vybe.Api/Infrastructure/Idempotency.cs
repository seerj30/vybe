using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Infrastructure;

/// <summary>A response produced by an idempotent operation (stored and replayed as-is).</summary>
public sealed record OperationResult(int Status, object Body);

/// <summary>
/// The idempotency protocol of spec §4 (idempotency_records):
/// 1. claim the key with a 30 s lease in its own statement;
/// 2. replay a completed record, reject a mismatch, or take over an elapsed lease;
/// 3. run the operation and complete the record in the same transaction, fenced on the lease.
/// Business 4xx outcomes are stored and replayed; 5xx and crashes store nothing.
/// </summary>
public sealed class Idempotency(VybeDbContext db)
{
    public const string Header = "Idempotency-Key";

    public static Guid ReadKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.TryGetValue(Header, out var values) || string.IsNullOrWhiteSpace(values))
        {
            throw Problem.Of(428, "idempotency_key_required");
        }

        return Guid.TryParse(values.ToString(), out var key) ? key : throw Problem.Of(422, "invalid_idempotency_key");
    }

    public static string Hash(string operation, string target, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var prefix = Encoding.UTF8.GetBytes($"{operation}\n{target}\n");
        return Convert.ToHexStringLower(SHA256.HashData([.. prefix, .. body]));
    }

    public async Task<IResult> RunAsync(
        HttpContext context,
        string actor,
        string operation,
        string requestHash,
        Func<CancellationToken, Task<OperationResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        var key = ReadKey(context.Request);

        var claim = await ClaimAsync(actor, operation, key, requestHash, cancellationToken);
        if (claim.Replay is not null)
        {
            return claim.Replay;
        }

        var lease = claim.Lease!.Value;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await work(cancellationToken);
            var body = JsonSerializer.Serialize(result.Body, ApiJson.Options);
            await CompleteAsync(actor, operation, key, lease, result.Status, body, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Text(body, "application/json", Encoding.UTF8, result.Status);
        }
        catch (ApiProblemException problem) when (problem.Status is >= 400 and < 500 && problem.Code != "request_in_progress")
        {
            // The business transaction rolled back; record the outcome so a retry gets the same answer.
            db.ChangeTracker.Clear();
            var body = JsonSerializer.Serialize(
                ProblemWriter.Body(context, problem.Status, problem.Code, problem.Detail, problem.Extensions), ApiJson.Options);
            await CompleteAsync(actor, operation, key, lease, problem.Status, body, cancellationToken);
            throw;
        }
    }

    private async Task<(DateTime? Lease, IResult? Replay)> ClaimAsync(
        string actor, string operation, Guid key, string requestHash, CancellationToken cancellationToken)
    {
        var inserted = await db.Database.SqlQuery<DateTime>($"""
            INSERT INTO vybe.idempotency_records (actor_subject, operation, key, request_hash, status, locked_until, expires_at)
            VALUES ({actor}, {operation}, {key}, {requestHash}, 'in_progress', now() + interval '30 seconds', now() + interval '72 hours')
            ON CONFLICT DO NOTHING
            RETURNING locked_until AS "Value"
            """).ToListAsync(cancellationToken);
        if (inserted.Count == 1)
        {
            return (inserted[0], null);
        }

        var record = await db.IdempotencyRecords.AsNoTracking()
            .SingleAsync(r => r.ActorSubject == actor && r.Operation == operation && r.Key == key, cancellationToken);
        if (record.RequestHash != requestHash)
        {
            throw Problem.Of(422, "idempotency_key_mismatch");
        }

        if (record.Status == IdempotencyStatus.Completed)
        {
            var contentType = record.ResponseStatus >= 400 ? ProblemWriter.ContentType : "application/json";
            return (null, Results.Text(record.ResponseBody ?? "{}", contentType, Encoding.UTF8, record.ResponseStatus));
        }

        var takenOver = await db.Database.SqlQuery<DateTime>($"""
            UPDATE vybe.idempotency_records
            SET locked_until = now() + interval '30 seconds'
            WHERE actor_subject = {actor} AND operation = {operation} AND key = {key}
              AND status = 'in_progress' AND locked_until < now()
            RETURNING locked_until AS "Value"
            """).ToListAsync(cancellationToken);
        return takenOver.Count == 1 ? (takenOver[0], null) : throw Problem.Of(409, "request_in_progress");
    }

    private async Task CompleteAsync(
        string actor, string operation, Guid key, DateTime lease, int status, string body, CancellationToken cancellationToken)
    {
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE vybe.idempotency_records
            SET status = 'completed', locked_until = NULL, response_status = {status}, response_body = {body}::jsonb
            WHERE actor_subject = {actor} AND operation = {operation} AND key = {key}
              AND status = 'in_progress' AND locked_until = {lease}
            """, cancellationToken);
        if (updated != 1)
        {
            // Another request took over the elapsed lease; this one must not commit.
            throw Problem.Of(409, "request_in_progress");
        }
    }
}

/// <summary>Opaque cursors for keyset pagination.</summary>
public static class Cursor
{
    public static string Encode(string value) => GuestTokens.Base64Url(Encoding.UTF8.GetBytes(value));

    public static string? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            throw Problem.ValidationFailed("/cursor", "invalid_cursor", "cursor is not valid");
        }
    }

    public static int Limit(int? limit, int fallback) =>
        limit switch
        {
            null => fallback,
            < 1 or > 100 => throw Problem.ValidationFailed("/limit", "out_of_range", "limit must be between 1 and 100"),
            _ => limit.Value,
        };
}
