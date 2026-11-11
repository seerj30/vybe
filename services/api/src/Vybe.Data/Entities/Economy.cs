namespace Vybe.Data.Entities;

// Spec v1.3 §4: catalog, inventory, outfits, wallet and ledger.

public sealed class CatalogItem
{
    public Guid Id { get; set; }
    public required string Sku { get; set; }
    public ItemSlot Slot { get; set; }
    public required string Name { get; set; }
    public long Price { get; set; }
    public int MinLevel { get; set; } = 1;
    public required string AssetKey { get; set; }
    public string[] CompatibleBodyPresets { get; set; } = [];
    public Rarity Rarity { get; set; }
    public bool IsActive { get; set; }
    public int CatalogVersion { get; set; }
    public DateTime? AvailableFrom { get; set; }
    public DateTime? AvailableUntil { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PlayerInventoryItem
{
    public Guid PlayerId { get; set; }
    public Guid ItemId { get; set; }
    public DateTime AcquiredAt { get; set; }
    public InventorySource Source { get; set; }
    public Guid? SourceTransactionId { get; set; }
    public Guid? SourceReferenceId { get; set; }
}

public sealed class SavedOutfit
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }

    /// <summary>1–3; <c>UNIQUE(player_id, slot)</c> enforces the MVP limit of 3 outfits in the database.</summary>
    public short Slot { get; set; }

    public required string Name { get; set; }
    public required string EquippedJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Wallet
{
    public Guid PlayerId { get; set; }
    public long Balance { get; set; }
    public long Revision { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class WalletTransaction
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public long Delta { get; set; }
    public long BalanceAfter { get; set; }
    public LedgerReason Reason { get; set; }
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public required string LedgerKey { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>My Room (docs/design/p1-my-room.md): one room per player; the API validates every layout.</summary>
public sealed class PlayerRoom
{
    public Guid PlayerId { get; set; }
    public Guid? WallItemId { get; set; }
    public Guid? FloorItemId { get; set; }

    /// <summary>JSON array of placements: <c>[{"itemId","surface","x","y","rotation"}]</c>, at most 40.</summary>
    public string LayoutJson { get; set; } = "[]";

    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
