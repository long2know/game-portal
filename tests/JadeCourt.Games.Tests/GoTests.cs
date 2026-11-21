namespace JadeCourt.Games.Tests;

public sealed class GoTests
{
    private readonly IBoardGame game = new GoGame();

    [Fact]
    public void PlayingTheLastLibertyRemovesAnEntireConnectedGroup()
    {
        var checkpoint = Play(game.Start("9x9", 2), "r1c0", "r0c0", "r1c1", "r0c1");
        Assert.Equal(2, game.View(checkpoint).Cells.Count(cell => cell.Owner == 1));
        var next = game.View(game.Apply(checkpoint, "r0c2"));
        Assert.DoesNotContain(next.Cells, cell => cell.Owner == 1);
        Assert.Equal(new[] { "r0c2", "r1c0", "r1c1" }, next.Cells.Where(cell => cell.Owner == 0).Select(cell => cell.Id));
        Assert.Equal(1, next.Turn);
    }

    [Fact]
    public void SelfCaptureIsIllegalButCapturingFirstCanCreateTheRequiredLiberty()
    {
        var checkpoint = Play(game.Start("9x9", 2), "r0c1", "r8c8", "r1c0");
        Assert.DoesNotContain(game.View(checkpoint).Moves, move => move.To == "r0c0");
        Assert.Throws<ArgumentException>(() => game.Apply(checkpoint, "r0c0"));

        var ko = Play(game.Start("9x9", 2), "r1c0", "r0c2", "r0c1", "r2c2", "r2c1", "r1c3", "pass", "r1c1");
        var captured = game.Apply(ko, "r1c2");
        var view = game.View(captured);
        Assert.Null(Assert.Single(view.Cells, cell => cell.Id == "r1c1").Owner);
        Assert.Equal(0, Assert.Single(view.Cells, cell => cell.Id == "r1c2").Owner);
        Assert.DoesNotContain(view.Moves, move => move.To == "r1c1");
        Assert.Throws<ArgumentException>(() => game.Apply(captured, "r1c1"));

        var changedElsewhere = Play(captured, "pass", "r8c8", "r1c1");
        Assert.Null(Assert.Single(game.View(changedElsewhere).Cells, cell => cell.Id == "r1c2").Owner);
    }

    [Fact]
    public void PositionalSuperkoRejectsAnOlderBoardEvenWithADifferentNextPlayer()
    {
        var black = new List<string>();
        var white = new List<string>();
        AddKo(0, 0, black, white);
        AddKo(0, 5, white, black);
        AddKo(6, 0, black, white);

        var checkpoint = game.Start("9x9", 2);
        for (var i = 0; i < white.Count; i++)
            checkpoint = Play(checkpoint, i < black.Count ? black[i] : "pass", white[i]);
        var start = game.View(checkpoint);
        Assert.Equal(black.Count + white.Count, start.Cells.Count(cell => cell.Owner is not null));
        Assert.Equal(0, start.Turn);

        // Cycle three independent kos. One intervening pass means the final board would have the other turn.
        checkpoint = Play(checkpoint, "r1c2", "r1c7", "r7c2", "r1c1", "pass", "r7c1");
        var beforeRecapture = new GoGame().View(checkpoint);
        Assert.Equal(0, beforeRecapture.Turn);
        Assert.Equal(1, Assert.Single(beforeRecapture.Cells, cell => cell.Id == "r1c7").Owner);
        Assert.DoesNotContain(beforeRecapture.Moves, move => move.To == "r1c6");
        Assert.Throws<ArgumentException>(() => game.Apply(checkpoint, "r1c6"));
        Assert.Contains(beforeRecapture.Moves, move => move.To == "r8c8");

        static void AddKo(int row, int column, List<string> captor, List<string> victim)
        {
            captor.AddRange([At(0, 1), At(1, 0), At(2, 1)]);
            victim.AddRange([At(0, 2), At(1, 3), At(2, 2), At(1, 1)]);
            string At(int y, int x) => $"r{row + y}c{column + x}";
        }
    }

    [Fact]
    public void TwoConsecutivePassesScoreStonesExclusiveAreaAndWhiteKomi()
    {
        var checkpoint = Play(game.Start("9x9", 2), "r1c1", "r7c7");
        foreach (var stone in new[] { "r1c2", "r1c3", "r2c1", "r2c3", "r3c1", "r3c2", "r3c3" })
            checkpoint = Play(checkpoint, stone, "pass");

        var before = game.View(checkpoint);
        Assert.Equal("playing", before.Status);
        Assert.Equal(0, before.Turn);
        Assert.Equal(new GameMove("pass", "Pass", null, null), before.LastMove);
        Assert.Null(before.Scores);
        Assert.Contains("7.5 komi", before.Detail);

        var final = game.View(game.Apply(checkpoint, "pass"));
        Assert.Equal("finished", final.Status);
        Assert.Null(final.Turn);
        Assert.Empty(final.Moves);
        Assert.Equal(new[] { 0 }, final.Winners);
        Assert.Equal(new[] { 9d, 8.5d }, final.Scores);
        Assert.Null(Assert.Single(final.Cells, cell => cell.Id == "r2c2").Owner);
    }

    private string Play(string checkpoint, params string[] moves) => moves.Aggregate(checkpoint, game.Apply);
}
