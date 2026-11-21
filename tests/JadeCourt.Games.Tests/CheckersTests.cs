namespace JadeCourt.Games.Tests;

public sealed class CheckersTests
{
    private readonly IBoardGame game = new CheckersGame();

    [Fact]
    public void CompleteCaptureChainsAreMandatoryButANonMaximalBranchIsLegal()
    {
        var checkpoint = Position(0, (6, 1, 1), (4, 7, 1), (5, 6, 2),
            (5, 2, 2), (3, 2, 2), (3, 4, 2), (1, 6, 2));
        var view = game.View(checkpoint);
        Assert.Equal(2, view.Moves.Length);
        Assert.All(view.Moves, move => Assert.Equal("r6c1", move.From));
        var shorter = Assert.Single(view.Moves, move => move.To == "r2c1");
        Assert.Equal(new[] { "r4c3" }, shorter.Via);
        Assert.Null(shorter.Promotion);
        var longer = Assert.Single(view.Moves, move => move.To == "r0c7");
        Assert.Equal(new[] { "r4c3", "r2c5" }, longer.Via);
        Assert.Equal("king", longer.Promotion);
        Assert.Throws<ArgumentException>(() => game.Apply(checkpoint, "r6c1>r4c3"));
        Assert.Throws<ArgumentException>(() => game.Apply(checkpoint, "r4c7>r6c5"));

        var after = game.View(game.Apply(checkpoint, shorter.Id));
        Assert.Equal(1, after.Turn);
        Assert.Equal(shorter.Id, after.LastMove?.Id);
        Assert.Equal(new[] { "r1c6", "r3c4", "r5c6" },
            after.Cells.Where(cell => cell.Owner == 1).Select(cell => cell.Id));
        Assert.Equal("man", Assert.Single(after.Cells, cell => cell.Id == "r2c1").Piece);
    }

    [Fact]
    public void CrowningEndsACaptureTurnEvenWhenTheNewKingCouldJumpBackwards()
    {
        var checkpoint = Position(0, (2, 1, 1), (1, 2, 2), (1, 4, 2));
        var capture = Assert.Single(game.View(checkpoint).Moves);
        Assert.Equal(("r2c1", "r0c3", "king"), (capture.From, capture.To, capture.Promotion));
        Assert.Null(capture.Via);
        Assert.Throws<ArgumentException>(() => game.Apply(checkpoint, "r2c1>r0c3>r2c5"));

        var after = game.View(game.Apply(checkpoint, capture.Id));
        Assert.Equal(1, after.Turn);
        Assert.Equal("king", Assert.Single(after.Cells, cell => cell.Owner == 0).Piece);
        Assert.Equal("r1c4", Assert.Single(after.Cells, cell => cell.Owner == 1).Id);
    }

    [Fact]
    public void ALoopingKingHasDistinctLegalChainsWithTheSameOriginAndDestination()
    {
        var checkpoint = Position(0, (4, 3, 3), (3, 2, 2), (1, 2, 2), (1, 4, 2), (3, 4, 2));
        var moves = game.View(checkpoint).Moves;
        Assert.Equal(2, moves.Length);
        Assert.All(moves, move => Assert.Equal(("r4c3", "r4c3"), (move.From, move.To)));
        Assert.Equal(new[] { "r2c1", "r0c3", "r2c5" }, moves[0].Via);
        Assert.Equal(new[] { "r2c5", "r0c3", "r2c1" }, moves[1].Via);
        Assert.NotEqual(moves[0].Id, moves[1].Id);
        Assert.NotEqual(moves[0].Label, moves[1].Label);
        foreach (var move in moves)
        {
            var final = game.View(game.Apply(checkpoint, move.Id));
            Assert.Equal("finished", final.Status);
            Assert.Equal(new[] { 0 }, final.Winners);
            Assert.Equal("r4c3", Assert.Single(final.Cells, cell => cell.Owner == 0).Id);
            Assert.DoesNotContain(final.Cells, cell => cell.Owner == 1);
        }
    }

    [Fact]
    public void APlayerWithPiecesButNoLegalMoveLoses()
    {
        var view = game.View(Position(1, (6, 1, 2), (7, 0, 1), (7, 2, 1)));
        Assert.Equal("finished", view.Status);
        Assert.Equal(new[] { 0 }, view.Winners);
        Assert.Null(view.Turn);
        Assert.Empty(view.Moves);
        Assert.Contains(view.Cells, cell => cell.Owner == 1);
    }

    [Fact]
    public void ThirdOccurrenceOfTheSamePositionAutomaticallyDraws()
    {
        var checkpoint = Position(0, (7, 0, 3), (0, 7, 4));
        string[] cycle = ["r7c0>r6c1", "r0c7>r1c6", "r6c1>r7c0", "r1c6>r0c7"];
        foreach (var move in cycle)
            checkpoint = game.Apply(checkpoint, move);
        Assert.Equal("playing", game.View(checkpoint).Status);

        foreach (var move in cycle)
            checkpoint = game.Apply(checkpoint, move);
        var final = game.View(checkpoint);
        Assert.Equal("finished", final.Status);
        Assert.Contains("threefold", final.Detail);
        Assert.Empty(final.Winners);
    }

    [Fact]
    public void FortyQuietMovesPerSeatDrawEvenBeforeAThirdRepetition()
    {
        string[] red = ["r4c5", "r5c4", "r6c3", "r7c4", "r6c5", "r7c6", "r6c7", "r5c6"];
        string[] white = ["r0c1", "r1c0", "r2c1", "r3c2", "r2c3", "r3c4",
            "r2c5", "r1c6", "r0c5", "r1c4", "r0c3", "r1c2"];
        var checkpoint = Position(0, (4, 5, 3), (0, 1, 4));
        for (var n = 0; n < 40; n++)
        {
            checkpoint = game.Apply(checkpoint, $"{red[n % red.Length]}>{red[(n + 1) % red.Length]}");
            if (n == 39)
                Assert.Equal("playing", game.View(checkpoint).Status);
            checkpoint = game.Apply(checkpoint, $"{white[n % white.Length]}>{white[(n + 1) % white.Length]}");
        }
        var final = game.View(checkpoint);
        Assert.Equal("finished", final.Status);
        Assert.Contains("40 moves per side", final.Detail);
        Assert.Empty(final.Winners);
    }

    private static string Position(int turn, params (int Row, int Column, byte Piece)[] pieces)
    {
        var board = new byte[64];
        foreach (var piece in pieces)
            board[8 * piece.Row + piece.Column] = piece.Piece;
        return CheckpointCodec.Save("checkers",
            new CheckersState(board, turn, [$"{turn}{Convert.ToBase64String(board)}"], null), 16_384);
    }
}
