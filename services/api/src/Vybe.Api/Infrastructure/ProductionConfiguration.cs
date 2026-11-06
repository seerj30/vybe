namespace Vybe.Api.Infrastructure;

/// <summary>
/// Fail-fast check of the settings that Production and Staging need (stream D). All problems are reported
/// together, by setting name only, so one failed deploy shows every missing secret and never a value.
/// The feature code keeps its own checks; this check runs first and before any service starts.
/// </summary>
public static class ProductionConfiguration
{
    public static bool Applies(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsProduction() || environment.IsStaging();
    }

    public static IReadOnlyList<string> Problems(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Vybe")))
        {
            problems.Add("ConnectionStrings:Vybe is not configured");
        }

        if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:Authority"]) && string.IsNullOrWhiteSpace(configuration["Auth:Supabase:MetadataAddress"]))
        {
            problems.Add("Auth:Supabase:Authority (or Auth:Supabase:MetadataAddress) is not configured");
        }

        if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:ApiKey"]))
        {
            problems.Add("Auth:Supabase:ApiKey is not configured");
        }

        if (!IsInviteSecret(configuration["Challenges:InviteSecret"]))
        {
            problems.Add("Challenges:InviteSecret must be base64 of at least 32 bytes");
        }

        if (!IsHttpsUrl(configuration["Challenges:ShareBaseUrl"]))
        {
            problems.Add("Challenges:ShareBaseUrl must be an absolute https URL");
        }

        var storage = configuration.GetSection(S3ExportStorageOptions.Section).Get<S3ExportStorageOptions>() ?? new S3ExportStorageOptions();
        problems.AddRange(storage.Problems());

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
