using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Seated;

internal sealed class SeatedTableRow
{
    public required string Id { get; init; }
    public required string Game { get; init; }
    public required string Status { get; init; }
    public string? InviteCode { get; init; }
    public int Players { get; init; }
    public bool Bots { get; init; }
    public long Revision { get; init; }
    public long ExpiresAtTicks { get; init; }
    public string? Checkpoint { get; init; }
    public string? EndReason { get; init; }
    public List<SeatedMembershipRow> Members { get; } = [];
}

internal sealed class SeatedMembershipRow
{
    public required string TableId { get; init; }
    public Guid GuestId { get; init; }
    public int Seat { get; init; }
    public long JoinedAtTicks { get; init; }
    public SeatedTableRow Table { get; init; } = null!;
    public GuestRow Guest { get; set; } = null!;
}

internal static class SeatedPersistence
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<SeatedTableRow>(table =>
        {
            table.ToTable("SeatedTables");
            table.HasIndex(t => t.InviteCode).IsUnique();
            table.HasIndex(t => t.ExpiresAtTicks);
        });
        builder.Entity<SeatedMembershipRow>(member =>
        {
            member.ToTable("SeatedMemberships");
            member.HasKey(m => new { m.TableId, m.Seat });
            member.HasIndex(m => new { m.GuestId, m.TableId }).IsUnique();
            member.HasOne(m => m.Guest).WithMany().HasForeignKey(m => m.GuestId);
            member.HasOne(m => m.Table).WithMany(t => t.Members).HasForeignKey(m => m.TableId);
        });
    }
}
