using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vybe.Data.Configuration;

internal static class ConfigurationHelpers
{
    public const string Now = "now()";

    /// <summary>Spec §4 <c>equipped_json</c> shape: all slot keys present, exactly two accessory points.</summary>
    public const string EquippedJsonShape =
        "jsonb_typeof(equipped_json) = 'object' " +
        "AND equipped_json ?& ARRAY['hair', 'top', 'bottom', 'shoes', 'emote', 'accessories'] " +
        "AND CASE WHEN jsonb_typeof(equipped_json -> 'accessories') = 'array' " +
        "THEN jsonb_array_length(equipped_json -> 'accessories') = 2 ELSE false END";

    /// <summary>CHECK constraint named <c>ck_{table}_{name}</c>.</summary>
    public static void Check<TEntity>(this TableBuilder<TEntity> table, string name, string sql)
        where TEntity : class =>
        table.HasCheckConstraint($"ck_{table.Name}_{name}", sql);

    /// <summary>CHECK constraint listing the snake_case values of <typeparamref name="TEnum"/>.</summary>
    public static void EnumCheck<TEntity, TEnum>(this TableBuilder<TEntity> table, string column)
        where TEntity : class
        where TEnum : struct, Enum =>
        table.HasCheckConstraint($"ck_{table.Name}_{column}", TextEnums.CheckSql<TEnum>(column));

    public static PropertyBuilder<DateTime> DefaultNow(this PropertyBuilder<DateTime> property) =>
        property.HasDefaultValueSql(Now);

    /// <summary>Maps a raw JSON string (nullable or not) to <c>jsonb</c>.</summary>
    public static PropertyBuilder<TJson> Jsonb<TJson>(this PropertyBuilder<TJson> property) =>
        property.HasColumnType("jsonb");
}
