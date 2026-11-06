using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sentry;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>Logs, spans and error reports carry no invite tokens, paths, headers or client addresses.</summary>
public sealed class OpsTelemetryTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde";

    [Theory]
    [InlineData("/api/v1/invites/" + Token, "/api/v1/invites/[redacted]")]
    [InlineData("GET /api/v1/invites/" + Token + "/accept?x=1", "GET /api/v1/invites/[redacted]/accept?x=1")]
    [InlineData("/api/v1/me", "/api/v1/me")]
    [InlineData(null, null)]
    public void Redact_RemovesInviteTokens(string? text, string? expected) => Assert.Equal(expected, Redaction.Redact(text));

    [Fact]
    public void JsonLog_HasRequestIdAndTrace_ButNoPathScopeOrToken()
    {
        var scopes = new LoggerExternalScopeProvider();
        using var activity = new Activity("test").Start();
        using var hosting = scopes.Push(new Dictionary<string, object?> { ["RequestId"] = "0HN1:00000001", ["RequestPath"] = "/api/v1/invites/" + Token });

        var line = Format("Vybe.Api.Infrastructure.ProblemExceptionHandler", "Unhandled error on {Path}", "/api/v1/invites/" + Token, scopes);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;

        Assert.Equal("Unhandled error on /api/v1/invites/[redacted]", root.GetProperty("message").GetString());
        Assert.Equal("/api/v1/invites/[redacted]", root.GetProperty("path").GetString());
        Assert.Equal("0HN1:00000001", root.GetProperty("requestId").GetString());
        Assert.Equal(activity.TraceId.ToHexString(), root.GetProperty("traceId").GetString());
        Assert.False(root.TryGetProperty("requestPath", out _));
        Assert.DoesNotContain(Token, line, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLog_FrameworkCategory_WritesNoProperties()
    {
        var line = Format("Microsoft.AspNetCore.Hosting.Diagnostics", "Request starting {Path}", "/x", null);

        using var json = JsonDocument.Parse(line);
        Assert.False(json.RootElement.TryGetProperty("path", out _));
    }

    [Fact]
    public void ServerSpan_KeepsTheRouteTemplate_NotThePathOrClient()
    {
        using var activity = new Activity("GET");
        activity.SetTag("url.path", "/api/v1/invites/" + Token);
        activity.SetTag("client.address", "203.0.113.7");
        activity.SetTag("user_agent.original", "browser");
        var context = new DefaultHttpContext();
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v1/invites/{token}"), 0, null, null));

        Telemetry.ScrubServerSpan(activity, context);

        Assert.Equal("/api/v1/invites/{token}", activity.GetTagItem("url.path"));
        Assert.Null(activity.GetTagItem("client.address"));
        Assert.Null(activity.GetTagItem("user_agent.original"));
    }

    [Fact]
    public void ServerSpan_Unmatched_HasNoPath()
    {
        using var activity = new Activity("GET");
        activity.SetTag("url.path", "/api/v1/invites/" + Token + "/typo");

        Telemetry.ScrubServerSpan(activity, new DefaultHttpContext());

        Assert.Equal("(unmatched)", activity.GetTagItem("url.path"));
    }

    [Fact]
    public void SentryEvent_KeepsOnlyTheRedactedUrl()
    {
        var sentryEvent = new SentryEvent(new InvalidOperationException("boom"))
        {
            Request = new SentryRequest
            {
                Url = "https://vybe.example/api/v1/invites/" + Token,
                QueryString = "a=b",
                Cookies = "__Host-vybe_guest=secret",
                Data = "{\"x\":1}",
            },
            User = new SentryUser { IpAddress = "203.0.113.7" },
        };
        sentryEvent.Request.Headers["Authorization"] = "Bearer secret";

        var scrubbed = Telemetry.Scrub(sentryEvent);

        Assert.Equal("https://vybe.example/api/v1/invites/[redacted]", scrubbed.Request.Url);
        Assert.Null(scrubbed.Request.QueryString);
        Assert.Null(scrubbed.Request.Cookies);
        Assert.Null(scrubbed.Request.Data);
        Assert.Empty(scrubbed.Request.Headers);
        Assert.Null(scrubbed.User.IpAddress);
    }

    private static string Format(string category, string template, object argument, IExternalScopeProvider? scopes)
    {
        var state = new FormattedState(template, argument);
        using var writer = new StringWriter();
        new JsonLogFormatter().Write(
            new Microsoft.Extensions.Logging.Abstractions.LogEntry<FormattedState>(LogLevel.Error, category, new EventId(7), state, null, (s, _) => s.ToString()),
            scopes,
            writer);
        return writer.ToString();
    }

    /// <summary>The state shape of a <c>[LoggerMessage]</c> call with one argument named Path.</summary>
    private sealed class FormattedState(string template, object argument) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _items = [new("Path", argument), new("{OriginalFormat}", template)];

        public int Count => _items.Length;

        public KeyValuePair<string, object?> this[int index] => _items[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public override string ToString() => template.Replace("{Path}", argument.ToString(), StringComparison.Ordinal);
    }
}
