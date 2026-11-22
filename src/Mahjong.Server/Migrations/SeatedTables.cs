using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mahjong.Server.Migrations;

[DbContext(typeof(MahjongDb))]
[Migration("20261122022000_SeatedTables")]
internal sealed class SeatedTables : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.CreateTable("SeatedTables", table => new
        {
            Id = table.Column<string>(nullable: false),
            Game = table.Column<string>(nullable: false),
            Status = table.Column<string>(nullable: false),
            InviteCode = table.Column<string>(nullable: true),
            Players = table.Column<int>(nullable: false),
            Bots = table.Column<bool>(nullable: false),
            Revision = table.Column<long>(nullable: false),
            ExpiresAtTicks = table.Column<long>(nullable: false),
            Checkpoint = table.Column<string>(nullable: true),
            EndReason = table.Column<string>(nullable: true)
        }, constraints: table => table.PrimaryKey("PK_SeatedTables", t => t.Id));
        migration.CreateTable("SeatedMemberships", table => new
        {
            TableId = table.Column<string>(nullable: false),
            GuestId = table.Column<Guid>(nullable: false),
            Seat = table.Column<int>(nullable: false),
            JoinedAtTicks = table.Column<long>(nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_SeatedMemberships", m => new { m.TableId, m.Seat });
            table.ForeignKey("FK_SeatedMemberships_SeatedTables_TableId", m => m.TableId, "SeatedTables", "Id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_SeatedMemberships_Guests_GuestId", m => m.GuestId, "Guests", "Id",
                onDelete: ReferentialAction.Cascade);
        });
        migration.CreateIndex("IX_SeatedTables_InviteCode", "SeatedTables", "InviteCode", unique: true);
        migration.CreateIndex("IX_SeatedTables_ExpiresAtTicks", "SeatedTables", "ExpiresAtTicks");
        migration.CreateIndex("IX_SeatedMemberships_GuestId_TableId", "SeatedMemberships",
            new[] { "GuestId", "TableId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropTable("SeatedMemberships");
        migration.DropTable("SeatedTables");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SeatedSchema.Build(modelBuilder);
}

internal static class SeatedSchema
{
    public static void Build(ModelBuilder builder)
    {
        BoardDifficultySchema.Build(builder);
        builder.Entity("Mahjong.Server.Seated.SeatedTableRow", table =>
        {
            table.Property<string>("Id").HasColumnType("TEXT");
            table.Property<string>("Game").IsRequired().HasColumnType("TEXT");
            table.Property<string>("Status").IsRequired().HasColumnType("TEXT");
            table.Property<string>("InviteCode").HasColumnType("TEXT");
            table.Property<int>("Players").HasColumnType("INTEGER");
            table.Property<bool>("Bots").HasColumnType("INTEGER");
            table.Property<long>("Revision").HasColumnType("INTEGER");
            table.Property<long>("ExpiresAtTicks").HasColumnType("INTEGER");
            table.Property<string>("Checkpoint").HasColumnType("TEXT");
            table.Property<string>("EndReason").HasColumnType("TEXT");
            table.HasKey("Id");
            table.HasIndex("InviteCode").IsUnique();
            table.HasIndex("ExpiresAtTicks");
            table.ToTable("SeatedTables");
        });
        builder.Entity("Mahjong.Server.Seated.SeatedMembershipRow", member =>
        {
            member.Property<string>("TableId").IsRequired().HasColumnType("TEXT");
            member.Property<Guid>("GuestId").HasColumnType("TEXT");
            member.Property<int>("Seat").HasColumnType("INTEGER");
            member.Property<long>("JoinedAtTicks").HasColumnType("INTEGER");
            member.HasKey("TableId", "Seat");
            member.HasIndex("GuestId", "TableId").IsUnique();
            member.HasOne("Mahjong.Server.Seated.SeatedTableRow", "Table").WithMany("Members")
                .HasForeignKey("TableId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            member.HasOne("Mahjong.Server.GuestRow", "Guest").WithMany()
                .HasForeignKey("GuestId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            member.ToTable("SeatedMemberships");
        });
    }
}
