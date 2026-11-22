using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mahjong.Server.Migrations;

[DbContext(typeof(MahjongDb))]
[Migration("20261122021700_BoardDifficulty")]
internal sealed class BoardDifficulty : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.AddColumn<string>("BotDifficulty", "BoardTables", nullable: true);

    protected override void Down(MigrationBuilder migration) =>
        migration.DropColumn("BotDifficulty", "BoardTables");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BoardDifficultySchema.Build(modelBuilder);
}

internal static class BoardDifficultySchema
{
    public static void Build(ModelBuilder builder)
    {
        BoardExitSchema.Build(builder);
        builder.Entity("Mahjong.Server.Board.BoardTableRow").Property<string>("BotDifficulty").HasColumnType("TEXT");
    }
}
