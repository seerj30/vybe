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

    /// <summary>Span tags that identify the client (address, port, user agent).</summary>
    public static readonly string[] PeerTags =
        ["client.address", "client.port", "network.peer.address", "network.peer.port", "user_agent.original"];

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
                    // Outgoing URLs can carry a Supabase Auth user id (J4 deletion): redact it like a request path.
                    .AddHttpClientInstrumentation(options => options.EnrichWithHttpRequestMessage = (activity, request) =>
                        activity.SetTag("url.full", Redaction.Redact(request.RequestUri?.GetLeftPart(UriPartial.Path))))
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
                // Errors only: traces go to OpenTelemetry, so no Sentry transactions carry URLs.
                options.TracesSampleRate = 0;
                options.SetBeforeSend((sentryEvent, _) => Scrub(sentryEvent));
                options.SetBeforeBreadcrumb((breadcrumb, _) => Scrub(breadcrumb));
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
        // The exception handler clears the endpoint, so error responses use the route saved by EdgeSecurity.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Items[EdgeSecurity.RouteItem] as string;
        activity.SetTag("url.path", route is null ? "(unmatched)" : "/" + route.TrimStart('/'));
        foreach (var tag in PeerTags)
        {
            activity.SetTag(tag, null);
        }
    }

    /// <summary>
    /// Sentry events keep the redacted URL only: no query, cookies, headers, body or user address; invite tokens
    /// are redacted in the message, exception values, tags and extra data.
    /// </summary>
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

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = Redaction.Redact(exception.Value);
        }

        foreach (var (key, value) in sentryEvent.Tags.ToList())
        {
            sentryEvent.SetTag(key, Redaction.Redact(value) ?? string.Empty);
        }

        foreach (var (key, value) in sentryEvent.Extra.ToList())
        {
            if (value is string text)
            {
                sentryEvent.SetExtra(key, Redaction.Redact(text));
            }
        }

        sentryEvent.TransactionName = Redaction.Redact(sentryEvent.TransactionName);
        return sentryEvent;
    }

    /// <summary>Breadcrumbs (from logs and HTTP calls) with invite tokens redacted in the message and data.</summary>
    public static Breadcrumb Scrub(Breadcrumb breadcrumb)
    {
        ArgumentNullException.ThrowIfNull(breadcrumb);
        var data = breadcrumb.Data?.ToDictionary(pair => pair.Key, pair => Redaction.Redact(pair.Value) ?? string.Empty);
        return new Breadcrumb(Redaction.Redact(breadcrumb.Message) ?? string.Empty, breadcrumb.Type ?? string.Empty, data, breadcrumb.Category, breadcrumb.Level);
    }
}
