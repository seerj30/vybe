using System.Diagnostics;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Logs, traces, metrics and error reports (spec §12). Provider-neutral: OpenTelemetry exports over OTLP only
/// when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; Sentry runs only when <c>Sentry:Dsn</c> is set.
/// No request bodies, headers, cookies, client addresses or invite tokens leave the process.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "vybe-api";
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Outside Development the console writes one JSON object per line (<see cref="JsonLogFormatter"/>).</summary>
    public static void AddVybeLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = JsonLogFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<JsonLogFormatter, ConsoleFormatterOptions>();
    }

    public static void AddVybeTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]))
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(
                    builder.Configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } name ? name : ServiceName,
                    serviceVersion: typeof(Telemetry).Assembly.GetName().Version?.ToString()))
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal);
                        options.EnrichWithHttpResponse = (activity, response) => ScrubServerSpan(activity, response.HttpContext);
                    })
                    .AddHttpClientInstrumentation()
                    .AddSource("Npgsql"))
                .WithMetrics(metrics => metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter("Npgsql", "System.Runtime", "Microsoft.AspNetCore.RateLimiting"))
                .UseOtlpExporter();
        }

        var dsn = builder.Configuration["Sentry:Dsn"];
        if (!string.IsNullOrWhiteSpace(dsn))
        {
            builder.WebHost.UseSentry(options =>
            {
                options.Dsn = dsn;
                options.Environment = builder.Environment.EnvironmentName;
                options.Release = builder.Configuration["Sentry:Release"];
                options.SendDefaultPii = false;
                options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
                // Traces go to OpenTelemetry; Sentry gets errors only unless a rate is configured.
                options.TracesSampleRate = builder.Configuration.GetValue("Sentry:TracesSampleRate", 0.0);
                options.SetBeforeSend((sentryEvent, _) => Scrub(sentryEvent));
            });
        }
    }

    /// <summary>
    /// The span keeps the route template, not the raw path (invite tokens, handles); no client address or user agent.
    /// </summary>
    public static void ScrubServerSpan(Activity activity, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(context);
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        activity.SetTag("url.path", route is null ? "(unmatched)" : "/" + route.TrimStart('/'));
        activity.SetTag("client.address", null);
        activity.SetTag("user_agent.original", null);
    }

    /// <summary>Sentry events keep the redacted URL only: no query, cookies, headers, body or user address.</summary>
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        ArgumentNullException.ThrowIfNull(sentryEvent);
        var request = sentryEvent.Request;
        request.Url = Redaction.Redact(request.Url);
        request.QueryString = null;
        request.Cookies = null;
        request.Data = null;
        request.Headers.Clear();
        request.Env.Clear();
        sentryEvent.User = new SentryUser();
        sentryEvent.ServerName = null;
        if (sentryEvent.Message is { } message)
        {
            message.Formatted = Redaction.Redact(message.Formatted);
            message.Message = Redaction.Redact(message.Message);
        }

        return sentryEvent;
    }
}
