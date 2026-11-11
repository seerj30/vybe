using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vybe.Data.Entities;

namespace Vybe.Data.Configuration;

internal sealed class CatalogItemConfiguration : IEntityTypeConfiguration<CatalogItem>
{
    public void Configure(EntityTypeBuilder<CatalogItem> b)
    {
        b.ToTable("catalog_items", t =>
        {
            t.EnumCheck<CatalogItem, ItemSlot>("slot");
            t.EnumCheck<CatalogItem, Rarity>("rarity");
            t.Check("price", "price >= 0");
            t.Check("min_level", "min_level BETWEEN 1 AND 50");
            t.Check("catalog_version", "catalog_version >= 1");
            t.Check("compatible_body_presets", "cardinality(compatible_body_presets) >= 1");
            t.Check("availability_window",
                "available_from IS NULL OR available_until IS NULL OR available_until > available_from");
        });
        b.Property(x => x.Slot).HasTextEnum();
        b.Property(x => x.Rarity).HasTextEnum();
        b.Property(x => x.MinLevel).HasDefaultValue(1).HasSentinel(-1);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.Sku).IsUnique();
    }
}

internal sealed class PlayerInventoryItemConfiguration : IEntityTypeConfiguration<PlayerInventoryItem>
{
    public void Configure(EntityTypeBuilder<PlayerInventoryItem> b)
    {
        b.ToTable("player_inventory", t =>
        {
            t.EnumCheck<PlayerInventoryItem, InventorySource>("source");
            t.Check("purchase_has_transaction", "(source = 'purchase') = (source_transaction_id IS NOT NULL)");
        });
        b.HasKey(x => new { x.PlayerId, x.ItemId });
        b.Property(x => x.Source).HasTextEnum();
        b.Property(x => x.AcquiredAt).DefaultNow();
        b.HasIndex(x => x.ItemId);
        b.HasIndex(x => x.SourceTransactionId).IsUnique().HasFilter("source_transaction_id IS NOT NULL");
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
        b.HasOne<CatalogItem>().WithMany().HasForeignKey(x => x.ItemId);
        b.HasOne<WalletTransaction>().WithMany().HasForeignKey(x => x.SourceTransactionId);
    }
}

internal sealed class SavedOutfitConfiguration : IEntityTypeConfiguration<SavedOutfit>
{
    public void Configure(EntityTypeBuilder<SavedOutfit> b)
    {
        b.ToTable("saved_outfits", t =>
        {
            t.Check("slot", "slot BETWEEN 1 AND 3");
            t.Check("name_not_empty", "length(name) >= 1");
            t.Check("equipped_json_shape", ConfigurationHelpers.EquippedJsonShape);
        });
        b.Property(x => x.Name).HasMaxLength(40);
        b.Property(x => x.EquippedJson).Jsonb();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.Property(x => x.UpdatedAt).DefaultNow();
        b.HasIndex(x => new { x.PlayerId, x.Slot }).IsUnique();
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> b)
    {
        b.ToTable("wallets", t =>
        {
            t.Check("balance", "balance >= 0");
            t.Check("revision", "revision >= 0");
        });
        b.HasKey(x => x.PlayerId);
        b.Property(x => x.Balance).HasDefaultValue(0L);
        b.Property(x => x.Revision).IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasDefaultValue(0L);
        b.Property(x => x.UpdatedAt).DefaultNow();
        b.HasOne<Player>().WithOne().HasForeignKey<Wallet>(x => x.PlayerId);
    }
}

internal sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> b)
    {
        b.ToTable("wallet_transactions", t =>
        {
            t.EnumCheck<WalletTransaction, LedgerReason>("reason");
            t.Check("delta", "delta <> 0");
            t.Check("balance_after", "balance_after >= 0");
            t.Check("ledger_key_not_empty", "length(ledger_key) >= 1");
            t.Check("reference_required", "(reason = 'level_reward') = (reference_id IS NULL)");
            // Spec §4 ledger key table: the key is derived from the reason and the reference.
            t.Check("ledger_key_shape",
                "CASE reason " +
                "WHEN 'game_reward' THEN ledger_key = 'game:' || reference_id::text " +
                "WHEN 'quest_reward' THEN ledger_key = 'quest:' || reference_id::text " +
                "WHEN 'challenge_reward' THEN ledger_key = 'challenge:' || reference_id::text " +
                "WHEN 'purchase' THEN ledger_key = 'purchase:' || reference_id::text " +
                "WHEN 'level_reward' THEN ledger_key ~ '^level:([2-9]|[1-4][0-9]|50)$' " +
                "WHEN 'admin_adjustment' THEN ledger_key ~ '^admin:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' " +
                "ELSE false END");
        });
        b.Property(x => x.Reason).HasTextEnum();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => new { x.PlayerId, x.LedgerKey }).IsUnique();
        b.HasIndex(x => new { x.PlayerId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.Reason, x.ReferenceId });
        // Invariant 9: at most one winner bonus per challenge, across both players.
        b.HasIndex(x => x.ReferenceId).IsUnique().HasFilter("reason = 'challenge_reward'")
            .HasDatabaseName("ux_wallet_transactions_one_challenge_reward");
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

internal sealed class PlayerRoomConfiguration : IEntityTypeConfiguration<PlayerRoom>
{
    public void Configure(EntityTypeBuilder<PlayerRoom> b)
    {
        b.ToTable("player_rooms", t =>
        {
            t.Check("revision", "revision >= 0");
            // The full placement rules need the manifest footprints; the API checks them (docs/design/p1-my-room.md).
            t.Check("layout_json_shape", "jsonb_typeof(layout_json) = 'array' AND jsonb_array_length(layout_json) <= 40");
        });
        b.HasKey(x => x.PlayerId);
        b.Property(x => x.LayoutJson).Jsonb().HasDefaultValueSql("'[]'::jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasDefaultValue(0L);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.Property(x => x.UpdatedAt).DefaultNow();
        b.HasIndex(x => x.WallItemId);
        b.HasIndex(x => x.FloorItemId);
        b.HasOne<Player>().WithOne().HasForeignKey<PlayerRoom>(x => x.PlayerId);
        b.HasOne<CatalogItem>().WithMany().HasForeignKey(x => x.WallItemId);
        b.HasOne<CatalogItem>().WithMany().HasForeignKey(x => x.FloorItemId);
    }
}
