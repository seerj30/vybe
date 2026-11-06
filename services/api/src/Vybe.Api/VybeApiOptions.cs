namespace Vybe.Api;

/// <summary>Non-secret API settings (section <c>Vybe</c>). The connection string is <c>ConnectionStrings:Vybe</c>.</summary>
internal sealed class VybeApiOptions
{
    public const string SectionName = "Vybe";

    /// <summary>Guest session lifetime (spec §17 item 2: 30 days chosen for the beta).</summary>
    public int GuestSessionDays { get; init; } = 30;

    /// <summary>Guest creations per client IP per minute (spec §5: rate limited; value provisional).</summary>
    public int GuestCreationPermitsPerMinute { get; init; } = 10;

    /// <summary>Time from session creation to the start deadline (spec §4: 10 minutes, provisional).</summary>
    public int SessionStartDeadlineMinutes { get; init; } = 10;

    /// <summary>Web origins that CORS allows (spec §11). Empty = same origin only.</summary>
    public string[] CorsOrigins { get; init; } = [];

    public bool IsValid() =>
        GuestSessionDays > 0 && GuestCreationPermitsPerMinute > 0 && SessionStartDeadlineMinutes > 0;
}
