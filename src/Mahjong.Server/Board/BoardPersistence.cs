using Microsoft.EntityFrameworkCore;

namespace Mahjong.Server.Board;

internal sealed class BoardTableRow
{
    public required string Id { get; init; }
    public required string Game { get; init; }
    public required string Variant { get; init; }
    public string? InviteCode { get; init; }
    public int Players { get; init; }
    public bool Bots { get; init; }
    public string? BotDifficulty { get; init; }
    public long Revision { get; init; }
    public long ExpiresAtTicks { get; init; }
    public string? Checkpoint { get; init; }
    public string? EndReason { get; set; }
    public int? EndedBySeat { get; set; }
    public List<BoardMembershipRow> Members { get; } = [];
}

internal sealed class BoardMembershipRow
{
    public required string TableId { get; init; }
    public Guid GuestId { get; init; }
    public int Seat { get; init; }
    public long JoinedAtTicks { get; init; }
    public BoardTableRow Table { get; init; } = null!;
    public GuestRow Guest { get; set; } = null!;
}

internal static class BoardPersistence
{
    public static IOrderedQueryable<BoardMembershipRow> ResumableMemberships(MahjongDb db, Guid guest, long now) =>
        db.BoardMemberships.AsNoTracking()
            .Where(m => m.GuestId == guest && m.Table.EndReason == null && m.Table.ExpiresAtTicks > now)
            .OrderByDescending(m => m.JoinedAtTicks).ThenByDescending(m => m.TableId);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<BoardTableRow>(table =>
        {
            table.ToTable("BoardTables");
            table.HasIndex(t => t.InviteCode).IsUnique();
            table.HasIndex(t => t.ExpiresAtTicks);
        });
        builder.Entity<BoardMembershipRow>(member =>
        {
            member.ToTable("BoardMemberships");
            member.HasKey(m => new { m.TableId, m.Seat });
            member.HasIndex(m => new { m.GuestId, m.TableId }).IsUnique();
            member.HasOne(m => m.Guest).WithMany().HasForeignKey(m => m.GuestId);
            member.HasOne(m => m.Table).WithMany(t => t.Members).HasForeignKey(m => m.TableId);
        });
    }
}
