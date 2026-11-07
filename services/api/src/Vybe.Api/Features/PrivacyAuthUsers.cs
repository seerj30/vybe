using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Vybe.Api.Features;

/// <summary>Supabase Auth admin settings for J4 (spec §17 decision 8). Server-side only; never logged.</summary>
public sealed class SupabaseAdminOptions
{
    public const string Section = "Auth:Supabase";

    /// <summary>Base URL of Supabase Auth, for example <c>https://{ref}.supabase.co/auth/v1</c>.</summary>
    public string? Authority { get; init; }

    /// <summary>The project's service-role (secret) key. It can delete any Auth user: keep it only on the server.</summary>
    public string? ServiceRoleKey { get; init; }
}

/// <summary>Supabase Auth did not delete the user (network error, timeout or an unexpected status).</summary>
public sealed class AuthUserDeletionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Deletes users from Supabase Auth (spec §2 J4: the email and login identity go with the account).</summary>
public interface IAuthUserAdmin
{
    /// <summary>False without a service-role key (Development, tests): the queue keeps its rows.</summary>
    bool IsConfigured { get; }

    /// <summary>Deletes the user; a user that does not exist counts as deleted. Throws <see cref="AuthUserDeletionException"/>.</summary>
    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// <c>DELETE {Authority}/admin/users/{id}</c> with the service-role key (hard delete). The id is a parsed UUID, so
/// the request path cannot change; the key goes only into the request headers, never into a log or a message.
/// </summary>
public sealed class SupabaseAuthUserAdmin(IHttpClientFactory httpClients, IOptions<SupabaseAdminOptions> options) : IAuthUserAdmin
{
    public const string HttpClientName = "supabase-auth-admin";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(options.Value.ServiceRoleKey) && Uri.TryCreate(options.Value.Authority, UriKind.Absolute, out _);

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new AuthUserDeletionException("Auth:Supabase:ServiceRoleKey or Auth:Supabase:Authority is not configured");
        }

        var key = options.Value.ServiceRoleKey!;
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{options.Value.Authority!.TrimEnd('/')}/admin/users/{userId:D}");
        request.Headers.Add("apikey", key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new AuthUserDeletionException("Supabase Auth did not answer", e);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode || await IsUserNotFoundAsync(response, cancellationToken))
            {
                return;
            }

            throw new AuthUserDeletionException($"Supabase Auth answered {(int)response.StatusCode}");
        }
    }

    /// <summary>
    /// Only Auth's own "user not found" answer means "already deleted". Any other 404 (for example a wrong
    /// Authority path) is a failure, so a configuration error never empties the queue.
    /// </summary>
    private static async Task<bool> IsUserNotFoundAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            return false;
        }

        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("error_code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == "user_not_found";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
