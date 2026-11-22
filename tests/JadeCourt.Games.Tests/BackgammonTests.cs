using JadeCourt.Games.Backgammon;
using static JadeCourt.Games.Backgammon.BackgammonActionKind;
using static JadeCourt.Games.Backgammon.BackgammonGame;

namespace JadeCourt.Games.Tests;

public sealed class BackgammonTests
{
    [Fact]
    public void OpeningTiesAreRerolledAndTheHigherSeatPlaysThePairBeforeTurnsAlternate()
    {
        var checkpoint = Start(Dice(3, 3, 1, 6));
        var view = View(checkpoint, 1);
        Assert.Equal(new[] { -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2 }, view.Points);
        Assert.Equal(new[] { 0, 0 }, view.Bar);
        Assert.Equal(new[] { 0, 0 }, view.Off);
        Assert.Equal(1, view.Turn);
        Assert.Equal(new[] { 1, 6 }, view.OpeningRoll);
        Assert.Equal(view.OpeningRoll, view.Dice);
        Assert.Equal(view.OpeningRoll, view.Remaining);
        Assert.Null(View(checkpoint, 0).LegalActions);
        Assert.Null(View(checkpoint, 1, playing: false).LegalActions);
        Assert.False(view.LegalActions!.CanRoll);
        Reject(checkpoint, 0, new(Move, new(1, 7, 6)), BackgammonRejection.NotYourTurn);
        Reject(checkpoint, 1, new(Roll));
        Reject(checkpoint, 1, new(Pass));

        checkpoint = Play(checkpoint, 1, 1, 7, 6);
        view = View(checkpoint, 1);
        Assert.Equal(-1, view.Points[0]);
        Assert.Equal(-1, view.Points[6]);
        Assert.Equal(1, view.Turn);
        Assert.Equal(new[] { 1 }, view.Remaining);
        Assert.DoesNotContain(new BackgammonMove(7, 8, 1), view.LegalActions!.Moves);

        checkpoint = Play(checkpoint, 1, 17, 18, 1);
        view = View(checkpoint, 0);
        Assert.Equal(0, view.Turn);
        Assert.Equal(-1, view.Points[17]);
        Assert.Empty(view.Remaining);
        Assert.Empty(view.Dice);
        Assert.Empty(view.LastTurn!.Unused);
        Assert.True(view.LegalActions!.CanRoll);
        Assert.Contains(new BackgammonMove(24, 18, 6), View(Start(Dice(6, 1)), 0).LegalActions!.Moves);
    }

    [Fact]
    public void HitMovesTheBlotToTheBarAndTwoClosedEntriesForceAPass()
    {
        var checkpoint = Fixture(0, [2, 3], [(8, 1), (6, -1), (1, 2), (2, 2), (5, -2), (12, -12)],
            remaining: [2]);
        checkpoint = Play(checkpoint, 0, 8, 6, 2);
        var view = View(checkpoint, 1);
        Assert.Equal(0, view.Points[7]);
        Assert.Equal(1, view.Points[5]);
        Assert.Equal(new[] { 0, 1 }, view.Bar);
        Assert.Equal(1, view.Turn);
        Reject(checkpoint, 1, new(Pass));

        checkpoint = Apply(checkpoint, 1, new(Roll), Dice(1, 2));
        view = View(checkpoint, 1);
        Assert.True(view.LegalActions!.CanPass);
        Assert.Empty(view.LegalActions.Moves);
        Assert.Equal(new[] { 1, 2 }, view.Remaining);
        Reject(checkpoint, 1, new(Move, new(Bar, 1, 1)));
        Reject(checkpoint, 1, new(Move, new(12, 13, 1)));

        var passed = View(Apply(checkpoint, 1, new(Pass), NoDice), 0);
        Assert.Equal(0, passed.Turn);
        Assert.Equal(1, passed.LastTurn!.Seat);
        Assert.Equal(new[] { 1, 2 }, passed.LastTurn.Unused);
        Assert.Equal(view.Points, passed.Points);
        Assert.Equal(view.Bar, passed.Bar);
    }

    [Fact]
    public void ReentryCanHitButNoBoardCheckerMayMoveWhileAnotherIsOnTheBar()
    {
        var checkpoint = Fixture(1, [2, 3], [(2, 2), (3, 1), (12, -2), (23, -1)], bar: [0, 2]);
        Assert.Equal(new BackgammonMove(Bar, 3, 3), Assert.Single(View(checkpoint, 1).LegalActions!.Moves));
        Reject(checkpoint, 1, new(Move, new(12, 15, 3)));
        var after = View(Play(checkpoint, 1, Bar, 3, 3), 0);
        Assert.Equal(new[] { 1, 1 }, after.Bar);
        Assert.Equal(-1, after.Points[2]);
        Assert.Equal(0, after.Turn);
        Assert.Equal(new[] { 2 }, after.LastTurn!.Unused);

        checkpoint = Fixture(0, [4, 6], [(21, -2), (13, 1)], bar: [1, 0]);
        Assert.Equal(new BackgammonMove(Bar, 19, 6), Assert.Single(View(checkpoint, 0).LegalActions!.Moves));
        after = View(Play(checkpoint, 0, Bar, 19, 6), 0);
        Assert.Equal(0, after.Turn);
        Assert.All(after.LegalActions!.Moves, move => Assert.NotEqual(Bar, move.From));
        Assert.Contains(new BackgammonMove(19, 15, 4), after.LegalActions.Moves);
    }

    [Fact]
    public void AnIndividuallyOpenMoveIsNotOfferedIfItPreventsPlayingBothDice()
    {
        var checkpoint = Fixture(0, [2, 3], [(10, 1), (13, 1), (5, -2), (11, -2)]);
        Assert.Equal(new[] { new BackgammonMove(10, 8, 2), new BackgammonMove(13, 10, 3) }, View(checkpoint, 0).LegalActions!.Moves);
        Reject(checkpoint, 0, new(Move, new(10, 7, 3)));
        checkpoint = Play(checkpoint, 0, 10, 8, 2);
        Assert.Equal(new BackgammonMove(13, 10, 3), Assert.Single(View(checkpoint, 0).LegalActions!.Moves));
        var after = View(Play(checkpoint, 0, 13, 10, 3), 1);
        Assert.Equal(1, after.Turn);
        Assert.Empty(after.LastTurn!.Unused);
    }

    [Theory]
    [InlineData(0, 3, 5)]
    [InlineData(-2, 2, 6)]
    public void HigherDieIsRequiredOnlyWhenEitherDieAloneCouldBePlayed(int highBlock, int die, int to)
    {
        var checkpoint = Fixture(0, [2, 3], [(8, 1), (3, -2), (5, highBlock)]);
        Assert.Equal(new BackgammonMove(8, to, die), Assert.Single(View(checkpoint, 0).LegalActions!.Moves));
        Reject(checkpoint, 0, new(Move, new(8, die == 3 ? 6 : 5, 5 - die)));
        var after = View(Play(checkpoint, 0, 8, to, die), 1);
        Assert.Equal(1, after.Points[to - 1]);
        Assert.Equal(new[] { 5 - die }, after.LastTurn!.Unused);
    }

    [Fact]
    public void DoublesPlayFourTimesAndCanEndEarlyOnlyWhenBlocked()
    {
        var checkpoint = Fixture(0, [], [(12, 1), (24, -2)]);
        checkpoint = Apply(checkpoint, 0, new(Roll), Dice(2, 2));
        Assert.Equal(new[] { 2, 2 }, View(checkpoint, 0).Dice);
        Assert.Equal(new[] { 2, 2, 2, 2 }, View(checkpoint, 0).Remaining);
        Reject(checkpoint, 0, new(Roll));
        for (var from = 12; from > 4; from -= 2)
        {
            Assert.Equal(new BackgammonMove(from, from - 2, 2), Assert.Single(View(checkpoint, 0).LegalActions!.Moves));
            checkpoint = Play(checkpoint, 0, from, from - 2, 2);
            Assert.Equal(1, View(checkpoint, 0).Points[from - 3]);
        }
        var finishedTurn = View(checkpoint, 1);
        Assert.Equal(1, finishedTurn.Turn);
        Assert.Empty(finishedTurn.LastTurn!.Unused);

        checkpoint = Fixture(0, [2, 2], [(12, 1), (8, -2)]);
        finishedTurn = View(Play(checkpoint, 0, 12, 10, 2), 1);
        Assert.Equal(1, finishedTurn.Turn);
        Assert.Equal(new[] { 2, 2, 2 }, finishedTurn.LastTurn!.Unused);
    }

    [Fact]
    public void BearingOffNeedsAllCheckersHomeAndOvershootCanOnlyRemoveTheFarthest()
    {
        var checkpoint = Fixture(0, [2, 1], [(7, 1), (1, 1), (24, -2)]);
        Reject(checkpoint, 0, new(Move, new(1, Off, 1)));
        checkpoint = Play(checkpoint, 0, 7, 5, 2);
        Assert.Contains(new BackgammonMove(1, Off, 1), View(checkpoint, 0).LegalActions!.Moves);

        checkpoint = Fixture(0, [6, 3], [(5, 1), (3, 1), (1, 1), (24, -2)]);
        Assert.Equal(new[] { new BackgammonMove(3, Off, 3), new BackgammonMove(5, 2, 3), new BackgammonMove(5, Off, 6) },
            View(checkpoint, 0).LegalActions!.Moves);
        Reject(checkpoint, 0, new(Move, new(3, Off, 6)));
        checkpoint = Play(checkpoint, 0, 5, 2, 3);
        Assert.Equal(new BackgammonMove(3, Off, 6), Assert.Single(View(checkpoint, 0).LegalActions!.Moves));
    }

    [Fact]
    public void BearOffKeepsDistinctDiceForTheSameTargetAndTheFifteenthCheckerWins()
    {
        var checkpoint = Fixture(1, [4, 6], [(22, -2), (6, 1)]);
        Assert.Equal(new[] { new BackgammonMove(22, Off, 6), new BackgammonMove(22, Off, 4) },
            View(checkpoint, 1).LegalActions!.Moves);
        checkpoint = Play(checkpoint, 1, 22, Off, 4);
        var before = View(checkpoint, 1);
        Assert.Equal(14, before.Off[1]);
        Assert.Equal(new[] { 6 }, before.Remaining);
        Assert.Null(before.Winner);

        checkpoint = Play(checkpoint, 1, 22, Off, 6);
        var end = View(checkpoint, 1);
        Assert.Equal(15, end.Off[1]);
        Assert.Equal(1, end.Winner);
        Assert.Equal(0, end.Points[21]);
        Assert.Null(end.Turn);
        Assert.Null(end.LegalActions);
        Reject(checkpoint, 1, new(Roll), BackgammonRejection.Finished);
    }

    [Theory]
    [InlineData("counts")]
    [InlineData("roll")]
    [InlineData("turn")]
    [InlineData("winner")]
    [InlineData("opening")]
    public void CorruptCheckpointsFailClosed(string corruption)
    {
        var state = CheckpointCodec.Load<BackgammonState>("backgammon", Start(Dice(2, 4)), 4_096);
        state = corruption switch
        {
            "counts" => state with { Bar = [1, 0] },
            "roll" => state with { Remaining = [4, 4] },
            "turn" => state with { Turn = 2 },
            "winner" => state with { Winner = 1 },
            "opening" => state with { OpeningRoll = [4, 4] },
            _ => throw new ArgumentException(nameof(corruption))
        };
        var checkpoint = CheckpointCodec.Save("backgammon", state, 4_096);
        Assert.Equal("Invalid Backgammon state.", Assert.Throws<InvalidDataException>(() => View(checkpoint, 0)).Message);
        Assert.Throws<InvalidDataException>(() => Apply(checkpoint, 1, new(Roll), NoDice));
    }

    [Fact]
    public void ADefectiveDiceSourceCannotCreateAnInvalidGameOrLoopForever()
    {
        Assert.Throws<InvalidDataException>(() => Start(Dice(0, 5)));
        Assert.Throws<InvalidDataException>(() => Start(() => 3));
    }

    [Fact]
    public void BotPlaysAMaximalTurnAndOnlyPassesWhenEveryEntryIsBlocked()
    {
        var checkpoint = Fixture(0, [2, 3], [(10, 1), (12, 1), (5, -2), (8, -1), (9, -2)]);
        var after = View(PlayBotTurn(checkpoint, 0, NoDice), 1);
        Assert.Equal(1, after.Turn);
        Assert.Empty(after.LastTurn!.Unused);
        Assert.Equal(1, after.Points[6]);
        Assert.Equal(-1, after.Points[7]);
        Assert.Equal(1, after.Points[9]);
        Assert.Equal(0, after.Points[11]);

        checkpoint = Fixture(1, [], [(2, 2), (3, 2), (12, -2)], bar: [0, 1]);
        after = View(PlayBotTurn(checkpoint, 1, Dice(2, 3)), 0);
        Assert.Equal(0, after.Turn);
        Assert.Equal(1, after.Bar[1]);
        Assert.Equal(new[] { 2, 3 }, after.LastTurn!.Unused);
    }

    [Fact]
    public void InjectedRollsReplayAnEntireGameAgainstTheBotIncludingItsOpening()
    {
        var first = PlayGame();
        var replay = PlayGame();
        Assert.Equal(first, replay);
        var won = View(replay, 0);
        Assert.True(won.Winner is 0 or 1);
        Assert.Equal(15, won.Off[won.Winner!.Value]);
        Assert.Null(won.LegalActions);

        static string PlayGame()
        {
            var random = new Random(143);
            int Die() => random.Next(1, 7);
            var checkpoint = Start(Dice(2, 5));
            checkpoint = PlayBotTurn(checkpoint, 1, NoDice);
            var opening = View(checkpoint, 0);
            Assert.Equal(1, opening.LastTurn!.Seat);
            Assert.Empty(opening.LastTurn.Unused);
            Assert.True(opening.LegalActions!.CanRoll);

            for (var action = 0; action < 400; action++)
            {
                var position = View(checkpoint, 0);
                if (position.Winner.HasValue) return checkpoint;
                Assert.Equal(0, position.Turn);
                var legal = position.LegalActions!;
                checkpoint = Apply(checkpoint, 0, legal.CanRoll ? new(Roll) : legal.CanPass ? new(Pass) : new(Move, legal.Moves[0]), Die);
                checkpoint = PlayBotTurn(checkpoint, 1, Die);
            }
            throw new InvalidOperationException("The deterministic bot game did not finish.");
        }
    }

    private static string Fixture(int seat, int[] dice, (int Point, int Count)[] layout, int[]? bar = null, int[]? remaining = null)
    {
        var points = new int[24];
        foreach (var (point, count) in layout) points[point - 1] = count;
        bar ??= [0, 0];
        int[] off = [15 - points.Sum(count => Math.Max(0, count)) - bar[0], 15 - points.Sum(count => Math.Max(0, -count)) - bar[1]];
        return CheckpointCodec.Save("backgammon", new BackgammonState(points, bar, off, seat, null, dice,
            remaining ?? (dice is [var first, var second] && first == second ? [first, first, first, first] : dice),
            [6, 1], new(1 - seat, [2, 1], [])), 4_096);
    }

    private static string Play(string checkpoint, int seat, int from, int to, int die) =>
        Apply(checkpoint, seat, new(Move, new(from, to, die)), NoDice);

    private static void Reject(string checkpoint, int seat, BackgammonAction action,
        BackgammonRejection reason = BackgammonRejection.IllegalAction) => Assert.Equal(reason,
        Assert.Throws<BackgammonRejectedException>(() => Apply(checkpoint, seat, action, NoDice)).Reason);

    private static Func<int> Dice(params int[] faces) => new Queue<int>(faces).Dequeue;
    private static int NoDice() => throw new InvalidOperationException("This action must not roll dice.");
}
