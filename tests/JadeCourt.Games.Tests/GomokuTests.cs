namespace JadeCourt.Games.Tests;

public sealed class GomokuTests
{
    private readonly IBoardGame game = new GomokuGame();

    [Fact]
    public void EmptyBoardUsesGoCoordinatesAndNoPassSwapOrOccupiedPointIsLegal()
    {
        var checkpoint = Start();
        var opening = game.View(checkpoint);
        Assert.Equal(225, opening.Moves.Length);
        Assert.Equal(new GameCell("r0c8", 0, 8, null, null), opening.Cells[8]);
        var placement = new GameMove("r0c8", "J15", null, "r0c8");
        Assert.Contains(placement, opening.Moves);
        Assert.Contains(new GameMove("r14c14", "P1", null, "r14c14"), opening.Moves);
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(checkpoint, "pass"));
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(checkpoint, "swap"));

        var saved = game.Apply(checkpoint, placement.Id);
        var next = new GomokuGame().View(saved);
        Assert.Equal(1, next.Turn);
        Assert.Equal(placement, next.LastMove);
        Assert.Equal(new GameCell("r0c8", 0, 8, 0, "stone"), next.Cells[8]);
        Assert.DoesNotContain(next.Moves, move => move.To == placement.To);
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(saved, placement.Id));
    }

    [Theory]
    [InlineData(0, 0, 0, 1, 5, 0)]
    [InlineData(0, 0, 1, 0, 5, 1)]
    [InlineData(0, 14, 1, -1, 5, 1)]
    [InlineData(9, 9, 1, 1, 6, 0)]
    public void CompletingAFiveOrAnOverlineEndsTheGame(
        int row, int column, int dy, int dx, int length, int winner)
    {
        const int gap = 2;
        var checkpoint = Start();
        var played = 0;
        for (var i = 0; i < length; i++)
        {
            if (i == gap)
                continue;
            var filler = At(14, 2 * played++);
            var stone = At(row + i * dy, column + i * dx);
            checkpoint = winner == 0 ? Play(checkpoint, stone, filler) : Play(checkpoint, filler, stone);
        }
        if (winner == 1)
            checkpoint = game.Apply(checkpoint, At(14, 2 * played));
        Assert.Equal("playing", game.View(checkpoint).Status);
        var winningMove = At(row + gap * dy, column + gap * dx);
        var saved = game.Apply(checkpoint, winningMove);
        var final = new GomokuGame().View(saved);
        Assert.Equal("finished", final.Status);
        Assert.Equal(new[] { winner }, final.Winners);
        Assert.Null(final.Turn);
        Assert.Empty(final.Moves);
        Assert.Equal(winningMove, final.LastMove?.To);
        Assert.Null(final.LastMove?.From);
        Assert.Equal(winner, Assert.Single(final.Cells, cell => cell.Id == winningMove).Owner);
        Assert.Throws<InvalidOperationException>(() => game.SelectBotMove(saved));
        Assert.Throws<ArgumentException>("moveId", () => game.Apply(saved, "r13c0"));
    }

    [Fact]
    public void FullBoardWithoutFiveDrawsOnlyAfterTheFinalPlacement()
    {
        var checkpoint = Start();
        var black = new List<string>();
        var white = new List<string>();
        for (var row = 0; row < 15; row++)
            for (var col = 0; col < 15; col++)
                ((row + col / 2) % 2 == 0 ? black : white).Add(At(row, col));
        for (var i = 0; i < white.Count; i++)
            checkpoint = Play(checkpoint, black[i], white[i]);

        var before = game.View(checkpoint);
        Assert.Equal("playing", before.Status);
        Assert.Equal(black[^1], Assert.Single(before.Moves).Id);
        var final = new GomokuGame().View(game.Apply(checkpoint, black[^1]));
        Assert.Equal("finished", final.Status);
        Assert.Empty(final.Winners);
        Assert.Equal("Draw: the board is full.", final.Detail);
        Assert.Null(final.Turn);
        Assert.Empty(final.Moves);
        Assert.All(final.Cells, cell => Assert.Equal("stone", cell.Piece));
    }

    [Fact]
    public void BotBlocksTheOnlyImmediateThreatUnlessItCanWinFirst()
    {
        var threatened = Play(Start(), "r0c0", "r6c5", "r0c1", "r6c6", "r0c2", "r6c7", "r0c3");
        var block = game.SelectBotMove(threatened);
        Assert.Equal("r0c4", block);
        var after = game.View(game.Apply(threatened, block));
        Assert.Equal(new GameCell("r0c4", 0, 4, 1, "stone"), after.Cells[4]);
        Assert.Equal("playing", after.Status);

        var mutualThreat = Play(threatened, "r6c8", "r14c14");
        var botWin = game.SelectBotMove(mutualThreat);
        Assert.Contains(game.View(mutualThreat).Moves, move => move.Id == botWin);
        Assert.Equal(new[] { 1 }, game.View(game.Apply(mutualThreat, botWin)).Winners);
    }

    [Fact]
    public void BotBuildsATwoEndedThreatAndWinsEvenWhenOneEndIsBlocked()
    {
        var checkpoint = Play(Start(), "r7c6", "r0c0", "r7c7", "r0c2", "r7c8", "r0c4");
        var choice = game.SelectBotMove(checkpoint);
        Assert.Contains(choice, new[] { "r7c5", "r7c9" });
        var after = Play(checkpoint, choice, choice == "r7c5" ? "r7c4" : "r7c10");
        Assert.Equal(new[] { 0 }, game.View(game.Apply(after, game.SelectBotMove(after))).Winners);
    }

    [Fact]
    public void BadPlacementHistoriesAndWrongGamesAreRejectedOnEveryReadPath()
    {
        Assert.Throws<ArgumentException>("variant", () => game.Start("standard", 2));
        Assert.Throws<ArgumentException>("players", () => game.Start("freestyle", 3));
        Assert.Throws<InvalidDataException>(() => game.Apply(new XiangqiGame().Start("standard", 2), "r0c0"));
        Assert.Throws<InvalidDataException>(() => game.View(new string(' ', 513)));
        Assert.Throws<InvalidDataException>(() => game.View(Save([0, 0])));
        Assert.Throws<InvalidDataException>(() => game.SelectBotMove(Save([225])));

        var saved = Play(Start(), "r0c0", "r2c1", "r0c1", "r2c3", "r0c2", "r2c5", "r0c3", "r2c7", "r0c4");
        var state = CheckpointCodec.Load<GomokuState>("gomoku", saved, 512);
        Assert.Throws<InvalidDataException>(() => game.View(Save([.. state.Placements, 224])));
    }

    private string Start() => game.Start("freestyle", 2);
    private string Play(string checkpoint, params string[] moves) => moves.Aggregate(checkpoint, game.Apply);
    private static string At(int row, int col) => FormattableString.Invariant($"r{row}c{col}");
    private static string Save(byte[] history) => CheckpointCodec.Save("gomoku", new GomokuState(history), 512);
}
