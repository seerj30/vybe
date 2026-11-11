namespace Vybe.Api.Infrastructure;

/// <summary>
/// Fail-fast check of the settings that every deployed environment needs (stream D). All problems are reported
/// together, by setting name only, so one failed deploy shows every missing secret and never a value.
/// The feature code keeps its own checks; this check runs first and before any service starts.
/// It applies to every environment except Development and <see cref="TestingEnvironment"/> (the integration
/// tests), so a misspelled environment name such as "Prod" cannot skip it.
/// </summary>
public static class ProductionConfiguration
{
    public const string TestingEnvironment = "Testing";

    public static bool Applies(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !environment.IsDevelopment() && !environment.IsEnvironment(TestingEnvironment);
    }

    public static IReadOnlyList<string> Problems(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Vybe")))
        {
            problems.Add("ConnectionStrings:Vybe is not configured");
        }
        else if (TransactionPoolerProblem(configuration.GetConnectionString("Vybe")!) is { } poolerProblem)
        {
            problems.Add(poolerProblem);
        }

        if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:Authority"]) && string.IsNullOrWhiteSpace(configuration["Auth:Supabase:MetadataAddress"]))
        {
            problems.Add("Auth:Supabase:Authority (or Auth:Supabase:MetadataAddress) is not configured");
        }

        // Tokens and signing keys must not come over plain http (only Development accepts it, for the local fake Auth).
        foreach (var key in new[] { "Auth:Supabase:Authority", "Auth:Supabase:MetadataAddress", "Auth:Supabase:Issuer" })
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]) && !IsHttpsUrl(configuration[key]))
            {
                problems.Add($"{key} must be an absolute https URL");
            }
        }

        if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:ApiKey"]))
        {
            problems.Add("Auth:Supabase:ApiKey is not configured");
        }

        // J4 deletes the Supabase Auth user (spec §17 decision 8); without this key the deletions only queue up.
        if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:ServiceRoleKey"]))
        {
            problems.Add("Auth:Supabase:ServiceRoleKey is not configured");
        }
        else if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:Authority"]))
        {
            problems.Add("Auth:Supabase:Authority is not configured (the Auth admin API for J4 deletion uses it)");
        }

        if (!IsInviteSecret(configuration["Challenges:InviteSecret"]))
        {
            problems.Add("Challenges:InviteSecret must be base64 of at least 32 bytes");
        }

        if (!IsHttpsUrl(configuration["Challenges:ShareBaseUrl"]))
        {
            problems.Add("Challenges:ShareBaseUrl must be an absolute https URL");
        }

        // Item picture URLs (previewUrl) are built on it; optional, the default is Challenges:ShareBaseUrl.
        if (!string.IsNullOrWhiteSpace(configuration["Content:AssetOrigin"]) && !IsHttpsUrl(configuration["Content:AssetOrigin"]))
        {
            problems.Add("Content:AssetOrigin must be an absolute https URL");
        }

        var storage = configuration.GetSection(S3ExportStorageOptions.Section).Get<S3ExportStorageOptions>() ?? new S3ExportStorageOptions();
        problems.AddRange(storage.Problems());
        if (Uri.TryCreate(storage.ServiceUrl, UriKind.Absolute, out var storageUrl) && storageUrl.Scheme == Uri.UriSchemeHttp)
        {
            // Presigned export URLs use the scheme of the endpoint: personal data must not travel in plain text.
            problems.Add($"{S3ExportStorageOptions.Section}:ServiceUrl must be an https URL");
        }

        // Without it, behind a proxy every client has the proxy's address and shares one guest rate-limit bucket.
        if (configuration.GetSection(EdgeSecurity.ProxySection).Get<string[]>() is not { Length: > 0 })
        {
            problems.Add($"{EdgeSecurity.ProxySection} is not configured (the proxy CIDR ranges; 127.0.0.1/32 when there is no proxy)");
        }

        // Telemetry can carry route names, trace ids and error text: plain http only to a collector on the same host.
        if (configuration[Telemetry.OtlpEndpointKey] is { Length: > 0 } otlp
            && !(Uri.TryCreate(otlp, UriKind.Absolute, out var otlpUrl)
                 && (otlpUrl.Scheme == Uri.UriSchemeHttps || (otlpUrl.Scheme == Uri.UriSchemeHttp && otlpUrl.IsLoopback))))
        {
            problems.Add($"{Telemetry.OtlpEndpointKey} must be an https URL (http only to a collector on localhost)");
        }

        if (string.IsNullOrWhiteSpace(configuration["Content:Root"]))
        {
            problems.Add("Content:Root is not configured");
        }

        return problems;
    }

    /// <summary>Throws one exception that lists every problem when the environment needs the check.</summary>
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!Applies(environment))
        {
            return;
        }

        var problems = Problems(configuration);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The {environment.EnvironmentName} configuration is not complete: {string.Join("; ", problems)}");
        }
    }

    /// <summary>
    /// The Supabase transaction pooler (Supavisor, port 6543) rejects Npgsql's session reset ("DISCARD ALL cannot run
    /// inside a transaction block") and server-side prepared statements; the broken connection then hangs every
    /// later query until the command timeout. Both settings are required there. The message names settings only.
    /// </summary>
    internal static string? TransactionPoolerProblem(string connectionString)
    {
        Npgsql.NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return "ConnectionStrings:Vybe is not a valid connection string";
        }

        if (builder.Port != 6543)
        {
            return null;
        }

        return builder.NoResetOnClose && builder.MaxAutoPrepare == 0
            ? null
            : "ConnectionStrings:Vybe uses the transaction pooler (port 6543): add 'No Reset On Close=true' and 'Max Auto Prepare=0'";
    }

    private static bool IsInviteSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var length) && length >= 32;
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
