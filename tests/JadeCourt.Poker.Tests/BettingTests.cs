using static JadeCourt.Poker.HoldemActionKind;
using static JadeCourt.Poker.Tests.TableFixture;

namespace JadeCourt.Poker.Tests;

public sealed class BettingTests
{
    [Fact]
    public void BigBlindKeepsOptionAndFullRaisesSetTheNextMinimum()
    {
        var table = New([100, 100, 100]);
        var start = table.View(0);
        Assert.Equal((0, 1, 2), (start.ButtonSeat, start.SmallBlindSeat, start.BigBlindSeat));
        Assert.Equal([0, 5, 10], start.Seats.Select(s => s.StreetCommitted));
        Assert.Equal(new(true, false, 10, null, new(20, 100)), start.LegalActions);
        Reject(HoldemRejection.NotYourTurn, () => table.Act(1, start.Revision, new(Call)));
        Reject(HoldemRejection.StaleRevision, () => table.Act(0, start.Revision - 1, new(Call)));
        Reject(HoldemRejection.HandInProgress, () => table.StartNextHand(start.Revision));
        foreach (var invalid in new HoldemAction[] { default, new(Check), new(Fold, 1), new(Call, 10),
            new(Bet, 20), new(Raise, 19), new(Raise, 101) })
            Reject(HoldemRejection.IllegalAction, () => table.Act(0, start.Revision, invalid));
        Conserves(table.View(), 300);
        Assert.Equal(start.Revision, table.View().Revision);

        Play(table, 0, Call);
        Assert.Equal(5, table.View(1).LegalActions!.CallAmount);
        Play(table, 1, Call);
        var option = table.View(2);
        Assert.Equal(new(false, true, null, null, new(20, 100)), option.LegalActions);
        Reject(HoldemRejection.IllegalAction, () => table.Act(2, option.Revision, new(Call)));
        Play(table, 2, Check);

        var flop = table.View(1);
        Assert.Equal(HoldemStreet.Flop, flop.Street);
        Assert.Equal(StandardDeck().Skip(7).Take(3), flop.Board);
        Assert.Equal(new(false, true, null, new(10, 90), null), flop.LegalActions);
        Reject(HoldemRejection.IllegalAction, () => table.Act(1, flop.Revision, new(Bet, 9)));
        Play(table, 1, Bet, 15);
        Assert.Equal(new HoldemWagerRange(30, 90), table.View(2).LegalActions!.Raise);
        Play(table, 2, Raise, 37);
        Assert.Equal(new HoldemWagerRange(59, 90), table.View(0).LegalActions!.Raise);
        Play(table, 0, Call);
        Play(table, 1, Call);

        var turn = table.View(1);
        Assert.Equal(HoldemStreet.Turn, turn.Street);
        Assert.Equal(1, turn.ActingSeat);
        Assert.Equal(StandardDeck()[11], turn.Board[^1]);
        Assert.Equal(new HoldemWagerRange(10, 53), turn.LegalActions!.Bet);
        Assert.Equal(0, turn.CurrentBet);
        Assert.Equal(141, turn.PotChips);
        Assert.All(turn.Seats, s => Assert.Equal(0, s.StreetCommitted));
        Conserves(turn, 300);
        CheckDown(table);
        Assert.Equal(StandardDeck()[13], table.View().Board[^1]);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void ShortAllInsReopenCumulativelyFromEachPlayersOwnLastAction(int last, bool reopens)
    {
        var table = New([200, last, 200, 200, 39, 200]);
        Play(table, 3, Raise, 30);
        Assert.Equal(new HoldemWagerRange(39, 39), table.View(4).LegalActions!.Raise);
        Play(table, 4, Raise, 39);
        Play(table, 5, Call);
        Play(table, 0, Call);
        Assert.Equal(new HoldemWagerRange(last, last), table.View(1).LegalActions!.Raise);
        Play(table, 1, Raise, last);
        Assert.Equal(new HoldemWagerRange(last + 20, 200), table.View(2).LegalActions!.Raise);
        Play(table, 2, Call);

        var earlyRaiser = table.View(3);
        Assert.Equal(last - 30, earlyRaiser.LegalActions!.CallAmount);
        if (reopens)
        {
            Assert.Equal(new HoldemWagerRange(last + 20, 200), earlyRaiser.LegalActions.Raise);
            var branch = Restore(Checkpoint(table));
            Play(branch, 3, Raise, last + 20);
            Assert.Equal(last + 20, branch.View().CurrentBet);
        }
        else
        {
            Assert.Null(earlyRaiser.LegalActions.Raise);
            Reject(HoldemRejection.IllegalAction, () => table.Act(3, earlyRaiser.Revision, new(Raise, last + 20)));
        }
        Play(table, 3, Call);

        var lateCaller = table.View(5);
        Assert.Equal(last - 39, lateCaller.LegalActions!.CallAmount);
        Assert.Null(lateCaller.LegalActions.Raise);
        Reject(HoldemRejection.IllegalAction, () => table.Act(5, lateCaller.Revision, new(Raise, 200)));
        Play(table, 5, Call);
        Play(table, 0, Call);
        Assert.Equal(HoldemStreet.Flop, table.View().Street);
        Conserves(table.View(), 4 * 200 + last + 39);
    }

    [Fact]
    public void ShortOpeningAllInCanBeRaisedByAnUnactedPlayerButNotThePriorChecker()
    {
        var table = New([100, 100, 15, 100]);
        Play(table, 3, Call);
        Play(table, 0, Call);
        Play(table, 1, Call);
        Play(table, 2, Check);

        Play(table, 1, Check);
        Assert.Equal(new HoldemWagerRange(5, 5), table.View(2).LegalActions!.Bet);
        Play(table, 2, Bet, 5);
        Assert.Equal(new HoldemWagerRange(15, 90), table.View(3).LegalActions!.Raise);
        Play(table, 3, Call);
        Play(table, 0, Call);
        Assert.Equal(new(true, false, 5, null, null), table.View(1).LegalActions);
        Play(table, 1, Call);
        Assert.Equal(HoldemStreet.Turn, table.View().Street);
        Assert.Equal(60, table.View().PotChips);
    }

    [Fact]
    public void ShortBigBlindUsesNominalBringInOnlyWhileAnotherStackCanRespond()
    {
        var multiway = New([100, 100, 2]);
        Assert.Equal(10, multiway.View().CurrentBet);
        Assert.Equal(10, multiway.View(0).LegalActions!.CallAmount);
        Play(multiway, 0, Call);
        Play(multiway, 1, Call);
        Assert.Equal(HoldemStreet.Flop, multiway.View().Street);
        Assert.Equal(22, multiway.View().PotChips);

        var headsUp = New([100, 8]);
        Assert.Equal(8, headsUp.View().CurrentBet);
        Assert.Equal(new(true, false, 3, null, null), headsUp.View(0).LegalActions);
        Play(headsUp, 0, Call);
        var done = headsUp.View();
        Assert.Equal(5, done.Board.Length);
        Assert.Equal(16, Assert.Single(done.Result!.Pots).Chips);
        Conserves(done, 108);
    }

    [Fact]
    public void TransitionToHeadsUpDoesNotGiveTheSurvivingBigBlindAnotherBigBlind()
    {
        var deck = Rig(0, "2c 3d 7h 9s Jc", "4d 5d", "As Ad", "Kd Qd");
        var table = HoldemTable.CreateWithDeck([5, 100, 100], 5, 10, 0, deck);
        Play(table, 0, Call);
        Assert.True(table.View().Seats[0].IsAllIn);
        Play(table, 1, Call);
        Play(table, 2, Check);
        CheckDown(table);
        var previous = table.View();
        Assert.Equal([0, 115, 90], previous.Seats.Select(s => s.Stack));
        Assert.Null(previous.MatchWinnerSeat);

        var headsUp = Rig(2, "3c 5d 7h 9s Jc", "", "Qs Qd", "Ah Ac");
        table.StartNextHandWithDeck(previous.Revision, headsUp);
        var next = table.View(2);
        Assert.Equal(previous.Revision + 1, next.Revision);
        Assert.Equal(2, next.HandNumber);
        Assert.Equal((2, 2, 1), (next.ButtonSeat, next.SmallBlindSeat, next.BigBlindSeat));
        Assert.Equal(2, next.ActingSeat);
        Assert.Null(next.Result);
        Assert.Empty(next.Board);
        Assert.False(next.Seats[0].DealtIn);
        Assert.Equal(Cards("Qs Qd"), table.View(1).OwnCards.AsEnumerable());
        Assert.Equal(Cards("Ah Ac"), next.OwnCards.AsEnumerable());
        Play(table, 2, Call);
        Play(table, 1, Check);
        Assert.Equal(1, table.View().ActingSeat);
        CheckDown(table);
        table.StartNextHand(table.View().Revision);
        var third = table.View();
        Assert.Equal((1, 1, 2), (third.ButtonSeat, third.SmallBlindSeat, third.BigBlindSeat));
        Conserves(third, 205);
    }
}
