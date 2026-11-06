using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

/// <summary>The caller of an admin endpoint: Supabase subject and role (spec §5 Admin, §11 separate claims).</summary>
public sealed record AdminActor(string Subject, AdminRole Role);

/// <summary>
/// Admin roles come from the Supabase <c>app_metadata.vybe_role</c> claim (<c>moderator</c> or
/// <c>administrator</c>). Only the service role can write <c>app_metadata</c>; <c>user_metadata</c> is
/// writable by the user and is never read here. An administrator may also do every moderator action.
/// </summary>
public static class AdminAccess
{
    public const string RoleClaim = "vybe_role";

    public static AdminRole? RoleOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.FindFirstValue(AuthSchemes.AuthKindClaim) == AuthSchemes.Guest
            || user.FindFirstValue("app_metadata") is not { Length: > 0 } metadata)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(RoleClaim, out var role)
                && role.ValueKind == JsonValueKind.String
                ? role.GetString() switch
                {
                    "moderator" => AdminRole.Moderator,
                    "administrator" => AdminRole.Administrator,
                    _ => null,
                }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Bearer token only, with at least the given role.</summary>
    public static void Require(AuthorizationPolicyBuilder policy, AdminRole minimum)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.AddAuthenticationSchemes(AuthSchemes.Supabase)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => RoleOf(context.User) is { } role && (role == AdminRole.Administrator || role == minimum));
    }

    public static AdminActor Actor(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new AdminActor(user.FindFirstValue("sub") ?? throw Problem.Forbidden(), RoleOf(user) ?? throw Problem.Forbidden());
    }

    /// <summary>Adds the audit row of an admin write; the caller saves it in the same transaction (spec §5 Admin).</summary>
    public static AdminAuditLog Audit(
        VybeDbContext db, AdminActor actor, string action, string entityType, string entityId, object? before, object? after, string reason,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        var entry = new AdminAuditLog
        {
            Id = Guid.CreateVersion7(),
            ActorSubject = actor.Subject,
            ActorRole = actor.Role,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, ApiJson.Options),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, ApiJson.Options),
            Reason = reason,
            CreatedAt = now,
        };
        db.AdminAuditLogs.Add(entry);
        return entry;
    }

    /// <summary>Contract <c>AdminReason</c>: 3 to 500 characters.</summary>
    public static string ValidReason(string? reason) =>
        reason is { Length: >= 3 and <= 500 } valid
            ? valid
            : throw Problem.ValidationFailed("/reason", "invalid", "reason must be 3 to 500 characters");
}
