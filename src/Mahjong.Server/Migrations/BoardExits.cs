using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mahjong.Server.Migrations;

[DbContext(typeof(MahjongDb))]
[Migration("20261122021500_BoardExits")]
internal sealed class BoardExits : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.AddColumn<string>("EndReason", "BoardTables", nullable: true);
        migration.AddColumn<int>("EndedBySeat", "BoardTables", nullable: true);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropColumn("EndedBySeat", "BoardTables");
        migration.DropColumn("EndReason", "BoardTables");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BoardExitSchema.Build(modelBuilder);
}

internal static class BoardExitSchema
{
    public static void Build(ModelBuilder builder)
    {
        BoardSchema.Build(builder);
        var table = builder.Entity("Mahjong.Server.Board.BoardTableRow");
        table.Property<string>("EndReason").HasColumnType("TEXT");
        table.Property<int?>("EndedBySeat").HasColumnType("INTEGER");
    }
}
