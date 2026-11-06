using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Vybe.Data;

/// <summary>
/// Maps C# enums to snake_case <c>text</c> columns (e.g. <c>StarterGrant</c> ↔ <c>starter_grant</c>)
/// and builds the matching CHECK constraints, so the database and the code list the same values.
/// </summary>
public static class TextEnums
{
    public static IReadOnlyCollection<string> Values<TEnum>()
        where TEnum : struct, Enum => Cache<TEnum>.ToText.Values;

    public static string ToDatabase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Cache<TEnum>.ToText.TryGetValue(value, out var text)
            ? text
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"undefined {typeof(TEnum).Name}");

    public static TEnum FromDatabase<TEnum>(string text)
        where TEnum : struct, Enum =>
        Cache<TEnum>.FromText.TryGetValue(text, out var value)
            ? value
            : throw new InvalidOperationException($"unknown {typeof(TEnum).Name} value '{text}' in database");

    public static ValueConverter<TEnum, string> Converter<TEnum>()
        where TEnum : struct, Enum => Cache<TEnum>.Converter;

    /// <summary>SQL for a CHECK constraint: <c>column IN ('a', 'b')</c>; NULL passes, as in SQL.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Values<TEnum>().Order(StringComparer.Ordinal).Select(v => $"'{v}'"))})";

    public static PropertyBuilder<TEnum> HasTextEnum<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum =>
        property.HasConversion(Converter<TEnum>()).HasColumnType("text");

    public static PropertyBuilder<TEnum?> HasTextEnum<TEnum>(this PropertyBuilder<TEnum?> property)
        where TEnum : struct, Enum =>
        property.HasConversion(Converter<TEnum>()).HasColumnType("text");

    private static class Cache<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly FrozenDictionary<TEnum, string> ToText =
            Enum.GetValues<TEnum>().ToFrozenDictionary(
                v => v,
                v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()));

        public static readonly FrozenDictionary<string, TEnum> FromText =
            ToText.ToFrozenDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

        public static readonly ValueConverter<TEnum, string> Converter =
            new(v => ToDatabase(v), s => FromDatabase<TEnum>(s));
    }
}
