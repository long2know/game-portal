using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mahjong.Server.Migrations;

[DbContext(typeof(MahjongDb))]
[Migration("20261122022500_SoloSaves")]
internal sealed class SoloSaves : Migration
{
    protected override void Up(MigrationBuilder migration) => migration.CreateTable("SoloSaves", table => new
    {
        GuestId = table.Column<Guid>(nullable: false),
        Game = table.Column<string>(nullable: false),
        Variant = table.Column<string>(nullable: false),
        Checkpoint = table.Column<string>(nullable: false),
        Revision = table.Column<long>(nullable: false)
    }, constraints: table =>
    {
        table.PrimaryKey("PK_SoloSaves", t => new { t.GuestId, t.Game });
        table.ForeignKey("FK_SoloSaves_Guests_GuestId", t => t.GuestId, "Guests", "Id", onDelete: ReferentialAction.Cascade);
    });

    protected override void Down(MigrationBuilder migration) => migration.DropTable("SoloSaves");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SoloSchema.Build(modelBuilder);
}

internal static class SoloSchema
{
    public static void Build(ModelBuilder builder)
    {
        SeatedSchema.Build(builder);
        builder.Entity("Mahjong.Server.Solo.SoloSaveRow", save =>
        {
            save.Property<Guid>("GuestId").HasColumnType("TEXT");
            save.Property<string>("Game").HasColumnType("TEXT");
            save.Property<string>("Variant").IsRequired().HasColumnType("TEXT");
            save.Property<string>("Checkpoint").IsRequired().HasColumnType("TEXT");
            save.Property<long>("Revision").HasColumnType("INTEGER");
            save.HasKey("GuestId", "Game");
            save.HasOne("Mahjong.Server.GuestRow", null).WithMany()
                .HasForeignKey("GuestId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            save.ToTable("SoloSaves");
        });
    }
}
