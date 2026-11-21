namespace JadeCourt.Games.Tests;

public sealed class ReversiTests
{
    private readonly IBoardGame game = new ReversiGame();

    [Fact]
    public void StandardOpeningOutflanksTheCenter()
    {
        var checkpoint = game.Start("standard", 2);
        Assert.Equal(new[] { "r2c3", "r3c2", "r4c5", "r5c4" },
            game.View(checkpoint).Moves.Select(move => move.To));

        var next = game.View(game.Apply(checkpoint, "r3c2"));
        Assert.Equal(4, next.Cells.Count(cell => cell is { Owner: 0, Piece: "disc" }));
        Assert.Equal(1, next.Cells.Count(cell => cell is { Owner: 1, Piece: "disc" }));
        Assert.Equal(new GameMove("r3c2", "C4", null, "r3c2"), next.LastMove);
    }

    [Fact]
    public void OnePlacementFlipsAllEightBracketedRaysButNotOutsideTheBracket()
    {
        var black = Bits(1, 1, 1, 3, 1, 5, 3, 1, 3, 5, 5, 1, 5, 3, 5, 5);
        var white = Bits(2, 2, 2, 3, 2, 4, 3, 2, 3, 4, 4, 2, 4, 3, 4, 4, 3, 6, 3, 7);
        var next = game.View(game.Apply(Checkpoint(black, white, 0), "r3c3"));

        Assert.Equal(17, next.Cells.Count(cell => cell.Owner == 0));
        Assert.Equal(new[] { "r3c6", "r3c7" }, next.Cells.Where(cell => cell.Owner == 1).Select(cell => cell.Id));
    }

    [Fact]
    public void PassIsRequiredOnlyWhenTheOtherSideCanMove()
    {
        var checkpoint = Checkpoint(ulong.MaxValue & ~3UL, 2, 1);
        var pass = Assert.Single(game.View(checkpoint).Moves);
        Assert.Equal(new GameMove("pass", "Pass", null, null), pass);
        Assert.Equal(pass.Id, game.SelectBotMove(checkpoint));
        Assert.Throws<ArgumentException>(() => game.Apply(checkpoint, "r0c0"));

        checkpoint = game.Apply(checkpoint, pass.Id);
        var afterPass = game.View(checkpoint);
        Assert.Equal(0, afterPass.Turn);
        Assert.Equal(pass, afterPass.LastMove);
        Assert.Equal("r0c0", Assert.Single(afterPass.Moves).To);
        var final = game.View(game.Apply(checkpoint, "r0c0"));
        Assert.Equal("finished", final.Status);
        Assert.Equal(new[] { 0 }, final.Winners);
        Assert.Equal(new double[] { 64, 0 }, final.Scores);
    }

    [Fact]
    public void StalemateEndsInADrawEvenWithEmptySquares()
    {
        var view = game.View(Checkpoint(0xffffUL, 0xffffUL << 48, 0));
        Assert.Equal("finished", view.Status);
        Assert.Null(view.Turn);
        Assert.Empty(view.Moves);
        Assert.Empty(view.Winners);
        Assert.Equal(new double[] { 16, 16 }, view.Scores);
        Assert.Equal(32, view.Cells.Count(cell => cell.Owner is null));
    }

    private static string Checkpoint(ulong black, ulong white, int seat) =>
        CheckpointCodec.Save("reversi", new ReversiState(black, white, seat, null), 4096);

    private static ulong Bits(params int[] coordinates)
    {
        ulong bits = 0;
        for (var i = 0; i < coordinates.Length; i += 2)
            bits |= 1UL << (8 * coordinates[i] + coordinates[i + 1]);
        return bits;
    }
}
