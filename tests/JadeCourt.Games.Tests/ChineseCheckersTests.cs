namespace JadeCourt.Games.Tests;

public sealed class ChineseCheckersTests
{
    private readonly IBoardGame game = new ChineseCheckersGame();

    [Theory]
    [InlineData(2, "r0c12,r16c12")]
    [InlineData(3, "r0c12,r12c24,r12c0")]
    [InlineData(4, "r0c12,r4c24,r16c12,r12c0")]
    [InlineData(6, "r0c12,r4c24,r12c24,r16c12,r12c0,r4c0")]
    public void StarHasHalfColumnSpacingAndBalancedStartingCamps(int players, string expectedTips)
    {
        var view = game.View(game.Start("standard", players));
        Assert.Equal((17, 25), (view.Rows, view.Columns));
        Assert.Equal(new[] { 1, 2, 3, 4, 13, 12, 11, 10, 9, 10, 11, 12, 13, 4, 3, 2, 1 },
            view.Cells.GroupBy(cell => cell.Row).Select(row => row.Count()));
        Assert.All(view.Cells, cell => Assert.Equal(cell.Row % 2, cell.Column % 2));
        var tips = expectedTips.Split(',');
        for (var seat = 0; seat < players; seat++)
        {
            Assert.Equal(seat, Assert.Single(view.Cells, cell => cell.Id == tips[seat]).Owner);
            Assert.Equal(10, view.Cells.Count(cell => cell.Owner == seat && cell.Piece == "peg"));
        }
        Assert.Null(Assert.Single(view.Cells, cell => cell.Id == "r8c12").Owner);
        Assert.Equal(121 - 10 * players, view.Cells.Count(cell => cell.Owner is null));
        Assert.Throws<ArgumentException>("players", () => game.Start("standard", 5));
    }

    [Fact]
    public void AnOptionalChainCanCrossButCannotStopInAForeignCamp()
    {
        var start = game.Start("standard", 3);
        var view = game.View(start);
        var state = CheckpointCodec.Load<ChineseCheckersState>("chinese-checkers", start, 8192);
        Relocate("r3c9", "r4c8");
        Relocate("r12c24", "r4c6");
        Relocate("r12c22", "r5c5");
        var checkpoint = Save(state);

        var moves = game.View(checkpoint).Moves.Where(move => move.From == "r4c8").ToArray();
        Assert.DoesNotContain(moves, move => move.To == "r4c4");
        var chain = Assert.Single(moves, move => move.To == "r6c6");
        Assert.Equal(new[] { "r4c4" }, chain.Via);
        Assert.Throws<ArgumentException>(() => game.Apply(checkpoint, "r4c8>r4c4"));

        var after = game.View(game.Apply(checkpoint, chain.Id));
        Assert.Null(Assert.Single(after.Cells, cell => cell.Id == "r4c8").Owner);
        Assert.Equal(0, Assert.Single(after.Cells, cell => cell.Id == "r6c6").Owner);
        Assert.Equal(1, Assert.Single(after.Cells, cell => cell.Id == "r4c6").Owner);
        Assert.Equal(1, Assert.Single(after.Cells, cell => cell.Id == "r5c5").Owner);
        Assert.Equal(chain.Id, after.LastMove?.Id);
        Assert.Equal(chain.Via, after.LastMove?.Via);
        Assert.Equal(1, after.Turn);

        void Relocate(string from, string to)
        {
            var source = Array.FindIndex(view.Cells, cell => cell.Id == from);
            var destination = Array.FindIndex(view.Cells, cell => cell.Id == to);
            state.Board[destination] = state.Board[source];
            state.Board[source] = 0;
        }
    }

    [Fact]
    public void EnteredGoalLocksThePegInAndFillingItEndsTheGame()
    {
        var start = game.Start("standard", 2);
        var opening = game.View(start);
        var board = opening.Cells.Select(cell => cell switch
        {
            { Id: "r8c12" } => (byte)2,
            { Id: "r12c8" } => (byte)1,
            { Row: < 4, Id: not "r0c12" } => (byte)2,
            { Row: >= 13, Id: not "r13c9" } => (byte)1,
            _ => (byte)0
        }).ToArray();
        var checkpoint = Save(new(board, 2, 0, 0, [-1, -1], null));
        var view = game.View(checkpoint);
        var inCamp = view.Moves.Where(move => move.From == "r13c11").ToArray();
        Assert.Equal("r13c9", Assert.Single(inCamp).To);
        Assert.DoesNotContain(inCamp, move => move.To == "r12c10");

        var win = Assert.Single(view.Moves, move => move is { From: "r12c8", To: "r13c9" });
        var final = game.View(game.Apply(checkpoint, win.Id));
        Assert.Equal("finished", final.Status);
        Assert.Equal(new[] { 0 }, final.Winners);
        Assert.Empty(final.Moves);
        Assert.Equal(10, final.Cells.Count(cell => cell is { Row: >= 13, Owner: 0 }));
    }

    private static string Save(ChineseCheckersState state) => CheckpointCodec.Save("chinese-checkers", state, 8192);
}
