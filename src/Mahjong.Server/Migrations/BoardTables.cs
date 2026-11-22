using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mahjong.Server.Migrations;

[DbContext(typeof(MahjongDb))]
[Migration("20261121223100_BoardTables")]
internal sealed class BoardTables : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.CreateTable("BoardTables", table => new
        {
            Id = table.Column<string>(nullable: false),
            Game = table.Column<string>(nullable: false),
            Variant = table.Column<string>(nullable: false),
            InviteCode = table.Column<string>(nullable: true),
            Players = table.Column<int>(nullable: false),
            Bots = table.Column<bool>(nullable: false),
            Revision = table.Column<long>(nullable: false),
            ExpiresAtTicks = table.Column<long>(nullable: false),
            Checkpoint = table.Column<string>(nullable: true)
        }, constraints: table => table.PrimaryKey("PK_BoardTables", t => t.Id));

        migration.CreateTable("BoardMemberships", table => new
        {
            TableId = table.Column<string>(nullable: false),
            GuestId = table.Column<Guid>(nullable: false),
            Seat = table.Column<int>(nullable: false),
            JoinedAtTicks = table.Column<long>(nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BoardMemberships", m => new { m.TableId, m.Seat });
            table.ForeignKey("FK_BoardMemberships_BoardTables_TableId", m => m.TableId, "BoardTables", "Id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_BoardMemberships_Guests_GuestId", m => m.GuestId, "Guests", "Id",
                onDelete: ReferentialAction.Cascade);
        });

        migration.CreateIndex("IX_BoardTables_InviteCode", "BoardTables", "InviteCode", unique: true);
        migration.CreateIndex("IX_BoardTables_ExpiresAtTicks", "BoardTables", "ExpiresAtTicks");
        migration.CreateIndex("IX_BoardMemberships_GuestId_TableId", "BoardMemberships",
            new[] { "GuestId", "TableId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropTable("BoardMemberships");
        migration.DropTable("BoardTables");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BoardSchema.Build(modelBuilder);
}

[DbContext(typeof(MahjongDb))]
internal sealed class MahjongDbModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => SocialSchema.Build(modelBuilder);
}

internal static class BoardSchema
{
    public static void Build(ModelBuilder builder)
    {
        InitialSchema.Build(builder);
        builder.Entity("Mahjong.Server.Board.BoardTableRow", table =>
        {
            table.Property<string>("Id").HasColumnType("TEXT");
            table.Property<string>("Game").IsRequired().HasColumnType("TEXT");
            table.Property<string>("Variant").IsRequired().HasColumnType("TEXT");
            table.Property<string>("InviteCode").HasColumnType("TEXT");
            table.Property<int>("Players").HasColumnType("INTEGER");
            table.Property<bool>("Bots").HasColumnType("INTEGER");
            table.Property<long>("Revision").HasColumnType("INTEGER");
            table.Property<long>("ExpiresAtTicks").HasColumnType("INTEGER");
            table.Property<string>("Checkpoint").HasColumnType("TEXT");
            table.HasKey("Id");
            table.HasIndex("InviteCode").IsUnique();
            table.HasIndex("ExpiresAtTicks");
            table.ToTable("BoardTables");
        });
        builder.Entity("Mahjong.Server.Board.BoardMembershipRow", member =>
        {
            member.Property<string>("TableId").IsRequired().HasColumnType("TEXT");
            member.Property<Guid>("GuestId").HasColumnType("TEXT");
            member.Property<int>("Seat").HasColumnType("INTEGER");
            member.Property<long>("JoinedAtTicks").HasColumnType("INTEGER");
            member.HasKey("TableId", "Seat");
            member.HasIndex("GuestId", "TableId").IsUnique();
            member.HasOne("Mahjong.Server.Board.BoardTableRow", "Table").WithMany("Members")
                .HasForeignKey("TableId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            member.HasOne("Mahjong.Server.GuestRow", "Guest").WithMany()
                .HasForeignKey("GuestId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            member.ToTable("BoardMemberships");
        });
    }
}
