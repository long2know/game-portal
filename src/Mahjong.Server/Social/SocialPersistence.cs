using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Social;

internal sealed class SocialInviteRow
{
    public required string Id { get; init; }
    public Guid SenderId { get; init; }
    public Guid RecipientId { get; init; }
    public required string Game { get; init; }
    public required string TableId { get; init; }
    public long WaitingVersion { get; init; }
    public bool Accepted { get; set; }
    public long ExpiresAtTicks { get; set; }
}

internal sealed class SocialBlockRow
{
    public Guid BlockerId { get; init; }
    public Guid BlockedId { get; init; }
}

internal static class SocialPersistence
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<GuestRow>().Property(g => g.SocialAvailable).HasDefaultValue(false);
        builder.Entity<SocialInviteRow>(invite =>
        {
            invite.ToTable("SocialInvites");
            invite.HasIndex(i => i.ExpiresAtTicks);
            invite.HasIndex(i => new { i.SenderId, i.Accepted, i.ExpiresAtTicks });
            invite.HasIndex(i => new { i.RecipientId, i.Accepted, i.ExpiresAtTicks });
            invite.HasOne<GuestRow>().WithMany().HasForeignKey(i => i.SenderId);
            invite.HasOne<GuestRow>().WithMany().HasForeignKey(i => i.RecipientId);
        });
        builder.Entity<SocialBlockRow>(block =>
        {
            block.ToTable("SocialBlocks");
            block.HasKey(b => new { b.BlockerId, b.BlockedId });
            block.HasOne<GuestRow>().WithMany().HasForeignKey(b => b.BlockerId);
            block.HasOne<GuestRow>().WithMany().HasForeignKey(b => b.BlockedId);
        });
    }
}
