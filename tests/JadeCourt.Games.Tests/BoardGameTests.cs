using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace JadeCourt.Games.Tests;

public sealed class BoardGameTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("reversi", "standard", 2, 8, 8, 64)]
    [InlineData("checkers", "english", 2, 8, 8, 32)]
    [InlineData("chinese-checkers", "standard", 6, 17, 25, 121)]
    [InlineData("go", "9x9", 2, 9, 9, 81)]
    [InlineData("go", "19x19", 2, 19, 19, 361)]
    [InlineData("xiangqi", "standard", 2, 10, 9, 90)]
    [InlineData("gomoku", "freestyle", 2, 15, 15, 225)]
    public void SavedOpeningReplaysWithAFreshEngine(
        string id, string variant, int players, int rows, int columns, int holes)
    {
        var game = BoardGames.Find(id)!;
        var checkpoint = game.Start(variant, players);
        Assert.Equal(checkpoint, game.Start(variant, players));
        var opening = game.View(checkpoint);
        Assert.Equal((rows, columns, holes), (opening.Rows, opening.Columns, opening.Cells.Length));
        Assert.Equal(0, opening.Turn);
        Assert.Equal("playing", opening.Status);
        Assert.Empty(opening.Winners);
        Assert.Null(opening.LastMove);
        Assert.All(opening.Cells.Where(cell => cell.Owner is null), cell => Assert.Null(cell.Piece));
        Assert.Equal(opening.Moves.Length, opening.Moves.Select(move => move.Id).Distinct().Count());

        var idChosen = game.SelectBotMove(checkpoint);
        var moveChosen = Assert.Single(opening.Moves, option => option.Id == idChosen);
        Assert.NotNull(moveChosen.To);
        var saved = game.Apply(checkpoint, idChosen);

        var fresh = (IBoardGame)Activator.CreateInstance(game.GetType(), nonPublic: true)!;
        Assert.Equal(idChosen, fresh.SelectBotMove(checkpoint));
        Assert.Equal(saved, fresh.Apply(checkpoint, idChosen));
        Assert.Equal(game.SelectBotMove(saved), fresh.SelectBotMove(saved));
        var view = fresh.View(saved);
        Assert.Equal(moveChosen.Id, view.LastMove!.Id);
        Assert.Equal(1, view.Turn);
        Assert.Equal(0, Assert.Single(view.Cells, cell => cell.Id == moveChosen.To).Owner);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(view, JsonSerializerOptions.Web));
        Assert.Equal(1, json.RootElement.GetProperty("turn").GetInt32());
        Assert.Equal(idChosen, json.RootElement.GetProperty("lastMove").GetProperty("id").GetString());
    }

    [Fact]
    public void InvalidCommandsAndCorruptCheckpointsAreNotSuccessfulFallbacks()
    {
        var reversi = BoardGames.Find("reversi")!;
        var go = BoardGames.Find("go")!;
        Assert.Throws<ArgumentException>("variant", () => reversi.Start("english", 2));
        Assert.Throws<ArgumentException>("players", () => reversi.Start("standard", 3));
        var checkpoint = reversi.Start("standard", 2);
        Assert.Throws<ArgumentException>("moveId", () => reversi.Apply(checkpoint, "r0c0"));
        Assert.Throws<ArgumentException>("moveId", () => reversi.Apply(checkpoint, "pass"));
        Assert.Throws<InvalidDataException>(() => reversi.View(checkpoint[..^2]));
        Assert.Throws<InvalidDataException>(() => reversi.Apply(go.Start("9x9", 2), "r2c3"));
        Assert.Throws<InvalidDataException>(() => reversi.SelectBotMove(
            CheckpointCodec.Save("reversi", new ReversiState(15, 15, 1, null), 4096)));
        Assert.Throws<InvalidDataException>(() => go.View(new string(' ', 560_001)));
    }

    [Theory]
    [InlineData("reversi", "standard", 2, 64)]
    [InlineData("checkers", "english", 2, 300)]
    [InlineData("chinese-checkers", "standard", 2, 300)]
    [InlineData("chinese-checkers", "standard", 3, 600)]
    [InlineData("chinese-checkers", "standard", 4, 600)]
    [InlineData("chinese-checkers", "standard", 6, 600)]
    [InlineData("go", "9x9", 2, 220)]
    [InlineData("go", "19x19", 2, 750)]
    [InlineData("xiangqi", "standard", 2, 500)]
    [InlineData("gomoku", "freestyle", 2, 225)]
    public void SampledBotGamesReachATerminalPosition(string id, string variant, int players, int maxTurns)
    {
        var game = BoardGames.Find(id)!;
        var checkpoint = game.Start(variant, players);
        var slowest = TimeSpan.Zero;
        var plies = 0;
        var timed = id is "xiangqi" or "gomoku";
        for (var turn = 0; turn < maxTurns; turn++)
        {
            var view = game.View(checkpoint);
            if (view.Status == "finished")
                break;
            Assert.Equal(turn % players, view.Turn);
            var start = timed ? Stopwatch.GetTimestamp() : 0;
            var bot = game.SelectBotMove(checkpoint);
            if (timed)
            {
                var elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed > slowest)
                    slowest = elapsed;
            }
            Assert.Contains(view.Moves, move => move.Id == bot);
            checkpoint = game.Apply(checkpoint, bot);
            plies++;
        }
        var final = game.View(checkpoint);
        Assert.Equal("finished", final.Status);
        Assert.Null(final.Turn);
        Assert.Empty(final.Moves);
        Assert.NotNull(final.LastMove);
        Assert.Throws<InvalidOperationException>(() => game.SelectBotMove(checkpoint));
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(checkpoint, final.LastMove.Id));
        if (timed)
        {
            output.WriteLine($"{id}: {plies} legal bot turns; slowest selection {slowest.TotalMilliseconds:F1} ms.");
            Assert.True(slowest < TimeSpan.FromSeconds(2), $"A bot turn took {slowest}.");
        }
    }
}
