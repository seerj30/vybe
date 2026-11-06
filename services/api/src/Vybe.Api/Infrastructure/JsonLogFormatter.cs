using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// One JSON object per line (spec §12): time, level, category, event, message, <c>traceId</c>, <c>spanId</c>,
/// <c>requestId</c>, the structured properties of VYBE log messages, and the exception.
/// Scopes are allowlisted: the framework's <c>RequestPath</c> scope is never written, because a path can hold
/// an invite token. Invite tokens in messages and properties are redacted. Headers, bodies and tokens are never logged.
/// </summary>
public sealed class JsonLogFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "vybe-json";
    private const string OwnCategoryPrefix = "Vybe.";
    private static readonly HashSet<string> ScopeAllowlist = new(StringComparer.Ordinal) { "RequestId" };

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var buffer = new ArrayBufferWriter<byte>();
        // Relaxed escaping keeps log lines readable; the output is a log stream, never HTML.
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("time", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString("level", logEntry.LogLevel.ToString());
            json.WriteString("category", logEntry.Category);
            if (logEntry.EventId.Id != 0)
            {
                json.WriteNumber("eventId", logEntry.EventId.Id);
            }

            json.WriteString("message", Redaction.Redact(message));

            if (Activity.Current is { } activity)
            {
                json.WriteString("traceId", activity.TraceId.ToHexString());
                json.WriteString("spanId", activity.SpanId.ToHexString());
            }

            scopeProvider?.ForEachScope(WriteScope, json);

            // Framework messages can carry paths and queries in their properties; only VYBE's own are written.
            if (logEntry.Category.StartsWith(OwnCategoryPrefix, StringComparison.Ordinal)
                && logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> properties)
            {
                foreach (var (key, value) in properties)
                {
                    if (key != "{OriginalFormat}")
                    {
                        WriteProperty(json, CamelCase(key), value);
                    }
                }
            }

            if (logEntry.Exception is not null)
            {
                json.WriteString("exception", Redaction.Redact(logEntry.Exception.ToString()));
            }

            json.WriteEndObject();
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteScope(object? scope, Utf8JsonWriter json)
    {
        if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return;
        }

        foreach (var (key, value) in pairs)
        {
            if (ScopeAllowlist.Contains(key))
            {
                WriteProperty(json, CamelCase(key), value);
            }
        }
    }

    private static void WriteProperty(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(name);
                break;
            case bool b:
                json.WriteBoolean(name, b);
                break;
            case double d when double.IsFinite(d):
                json.WriteNumber(name, d);
                break;
            case int or long or short or byte or uint or ulong or ushort or decimal:
                json.WritePropertyName(name);
                json.WriteRawValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
                break;
            default:
                json.WriteString(name, Redaction.Redact(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                break;
        }
    }

    private static string CamelCase(string key) =>
        key.Length > 0 && char.IsUpper(key[0]) ? char.ToLowerInvariant(key[0]) + key[1..] : key;
}
