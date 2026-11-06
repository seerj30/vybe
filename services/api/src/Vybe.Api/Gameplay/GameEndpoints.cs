using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vybe.Api.Auth;
using Vybe.Api.Http;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Gameplay;

/// <summary>Rulesets and Street Hoops game sessions (spec §5 Gameplay, §6 Game session).</summary>
internal static partial class GameEndpoints
{
    /// <summary>A 16 KB body of unknown keys must not produce a response many times its size.</summary>
    private const int MaxFieldErrors = 20;

    [GeneratedRegex("^[a-z0-9-]{1,64}\\z")]
    private static partial Regex RulesetIdFormat();

    public static void Map(RouteGroupBuilder api)
    {
        var games = api.MapGroup("/games").RequireAuthorization();
        games.MapGet("/rulesets/{rulesetId}", GetRulesetAsync).AllowAnonymous();
        games.MapPost("/street-hoops/sessions", CreateSessionAsync).RequireCsrfHeader();
        games.MapGet("/sessions/{sessionId:guid}", GetSessionAsync);
        games.MapPost("/sessions/{sessionId:guid}/start", StartSessionAsync).RequireCsrfHeader();
        games.MapPost(
                "/sessions/{sessionId:guid}/submit",
                (Guid sessionId, HttpContext http, SubmitGameSession handler, CancellationToken cancellationToken) =>
                    handler.HandleAsync(http, sessionId, cancellationToken))
            .RequireCsrfHeader();
    }

    private static async Task<IResult> GetRulesetAsync(
        string rulesetId,
        HttpContext http,
        RulesetStore rulesets,
        CancellationToken cancellationToken)
    {
        var ruleset = RulesetIdFormat().IsMatch(rulesetId)
            ? await rulesets.FindAsync(rulesetId, cancellationToken)
            : null;
        if (ruleset is null)
        {
            return ApiProblem.Result(http, StatusCodes.Status404NotFound, ProblemCodes.NotFound);
        }

        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return TypedResults.Ok(new RulesetResponse(
            ruleset.Id,
            ruleset.GameId,
            ruleset.Version,
            ruleset.ConfigJson,
            ruleset.Checksum));
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext http,
        VybeDbContext db,
        RulesetStore rulesets,
        TimeProvider clock,
        IOptions<VybeApiOptions> options,
        CancellationToken cancellationToken)
    {
        var body = await JsonBody.ReadAsync(http.Request, cancellationToken);
        using var document = body.Document;
        if (body.Status == BodyStatus.TooLarge)
        {
            return ApiProblem.Result(http, StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge);
        }

        if (document?.RootElement.ValueKind != JsonValueKind.Object)
        {
            return ApiProblem.Result(http, StatusCodes.Status400BadRequest, ProblemCodes.MalformedRequest);
        }

        Guid? challengeId = null;
        var errors = new List<FieldError>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (errors.Count == MaxFieldErrors)
            {
                break;
            }

            if (property.Name != "challengeId")
            {
                errors.Add(new FieldError(JsonBody.Pointer(property.Name), "additional_property", "unknown field"));
            }
            else if (property.Value.ValueKind != JsonValueKind.String)
            {
                return ApiProblem.Result(http, StatusCodes.Status400BadRequest, ProblemCodes.MalformedRequest);
            }
            else if (Guid.TryParseExact(property.Value.GetString(), "D", out var parsed))
            {
                challengeId = parsed;
            }
            else
            {
                errors.Add(new FieldError(JsonBody.Pointer(property.Name), "format", "must be a UUID"));
            }
        }

        if (errors.Count > 0)
        {
            return ApiProblem.Result(
                http,
                StatusCodes.Status422UnprocessableEntity,
                ProblemCodes.ValidationFailed,
                errors: errors);
        }

        var playerId = http.User.PlayerId();
        var now = clock.UtcNowMicros();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serializes session creation per player (one open session, spec §4).
        var player = await RowLocks.PlayerAsync(db, playerId, cancellationToken);
        if (player.Status == PlayerStatus.Suspended && (player.SuspendedUntil is null || player.SuspendedUntil > now))
        {
            return ApiProblem.Result(http, StatusCodes.Status403Forbidden, ProblemCodes.AccountSuspended);
        }

        if (challengeId is not null)
        {
            // Challenge (recipient) sessions arrive with the challenge endpoints. Until then no challenge
            // can be accepted, so every challenge is unavailable to the caller.
            return ApiProblem.Result(http, StatusCodes.Status404NotFound, ProblemCodes.ChallengeUnavailable);
        }

        var ruleset = await rulesets.FindActiveAsync(RulesetStore.StreetHoopsGameId, now, cancellationToken)
                      ?? throw new InvalidOperationException("no active street-hoops ruleset");

        // One open session per player: a new session replaces the open one.
        await db.GameSessions
            .Where(s => s.PlayerId == playerId
                        && (s.Status == GameSessionStatus.Created || s.Status == GameSessionStatus.Active))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GameSessionStatus.Expired), cancellationToken);

        var session = new GameSession
        {
            Id = Guid.CreateVersion7(),
            PlayerId = playerId,
            GameId = ruleset.GameId,
            RulesetId = ruleset.Id,
            Seed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(sizeof(uint))),
            Status = GameSessionStatus.Created,
            CreatedAt = now,
            StartDeadline = now.AddMinutes(options.Value.SessionStartDeadlineMinutes),
        };
        db.GameSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Json(
            new SessionCreatedResponse(session.Id, "created", ruleset.Id, ruleset.Checksum, session.StartDeadline),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> StartSessionAsync(
        Guid sessionId,
        HttpContext http,
        VybeDbContext db,
        RulesetStore rulesets,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var playerId = http.User.PlayerId();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await RowLocks.SessionAsync(db, sessionId, playerId, cancellationToken);
        if (session is null)
        {
            return ApiProblem.Result(http, StatusCodes.Status404NotFound, ProblemCodes.NotFound);
        }

        // Idempotent: a started session that was not replaced or expired returns the same values.
        if (session is { StartedAt: not null, SubmitDeadline: not null } && session.Status != GameSessionStatus.Expired)
        {
            return TypedResults.Ok(new SessionStartedResponse(session.Seed, session.StartedAt.Value, session.SubmitDeadline.Value));
        }

        var now = clock.UtcNowMicros();
        if (session.Status == GameSessionStatus.Created && now < session.StartDeadline)
        {
            var config = (await rulesets.GetAsync(session.RulesetId, cancellationToken)).Config;
            session.Status = GameSessionStatus.Active;
            session.StartedAt = now;
            session.SubmitDeadline = now.AddMilliseconds(config.DurationMs + config.GraceMs);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return TypedResults.Ok(new SessionStartedResponse(session.Seed, now, session.SubmitDeadline.Value));
        }

        if (session.Status == GameSessionStatus.Created)
        {
            // Not started before start_deadline: created -> expired (spec §6), as the sweeper would.
            session.Status = GameSessionStatus.Expired;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return ApiProblem.Result(http, StatusCodes.Status410Gone, ProblemCodes.SessionExpired);
    }

    private static async Task<IResult> GetSessionAsync(
        Guid sessionId,
        HttpContext http,
        VybeDbContext db,
        CancellationToken cancellationToken)
    {
        var playerId = http.User.PlayerId();
        var session = await db.GameSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.PlayerId == playerId, cancellationToken);
        if (session is null)
        {
            return ApiProblem.Result(http, StatusCodes.Status404NotFound, ProblemCodes.NotFound);
        }

        RewardsResponse? rewards = null;
        if (session.Status == GameSessionStatus.Verified)
        {
            var level = await db.Players.Where(p => p.Id == playerId).Select(p => p.Level).SingleAsync(cancellationToken);
            rewards = SubmitGameSession.Rewards(session, level);
        }

        return TypedResults.Ok(new GameSessionResponse(
            session.Id,
            session.GameId,
            session.RulesetId,
            TextEnums.ToDatabase(session.Status),
            session.ChallengeId,
            session.CreatedAt,
            session.StartDeadline,
            session.StartedAt,
            session.SubmitDeadline,
            session.Status == GameSessionStatus.Verified ? session.Score : null,
            rewards,
            session.RejectionReason is { } reason ? TextEnums.ToDatabase(reason) : null));
    }
}
