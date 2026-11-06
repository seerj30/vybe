using System.Text.Json;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Features;

/// <summary>Spec §4 <c>equipped_json</c>: one item per slot, two accessory attachment points.</summary>
public sealed record Equipped(Guid? Hair, Guid? Top, Guid? Bottom, Guid? Shoes, Guid? Emote, Guid?[] Accessories)
{
    public static Equipped Empty => new(null, null, null, null, null, [null, null]);

    public static Equipped FromJson(string json) =>
        JsonSerializer.Deserialize<Equipped>(json, ApiJson.Options) ?? throw new InvalidDataException("equipped_json is empty");

    public string ToJson() => JsonSerializer.Serialize(this, ApiJson.Options);

    public IEnumerable<Guid> ItemIds() =>
        new[] { Hair, Top, Bottom, Shoes, Emote }.Concat(Accessories).Where(id => id.HasValue).Select(id => id!.Value).Distinct();

    public Equipped With(ItemSlot slot, Guid? itemId, int? attachIndex) => slot switch
    {
        ItemSlot.Hair => this with { Hair = itemId },
        ItemSlot.Top => this with { Top = itemId },
        ItemSlot.Bottom => this with { Bottom = itemId },
        ItemSlot.Shoes => this with { Shoes = itemId },
        ItemSlot.Emote => this with { Emote = itemId },
        ItemSlot.Accessory => this with
        {
            Accessories = attachIndex == 0 ? [itemId, Accessories[1]] : [Accessories[0], itemId],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };
}
