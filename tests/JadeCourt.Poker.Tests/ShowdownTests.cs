using static JadeCourt.Poker.HoldemActionKind;
using static JadeCourt.Poker.Tests.TableFixture;

namespace JadeCourt.Poker.Tests;

public sealed class ShowdownTests
{
    [Fact]
    public void SettlesEachSidePotAndReturnsUnmatchedChipsBeforeSplitting()
    {
        var deck = Rig(0, "As Ad 7c 4h 2s", "Ks Jh", "Jd Td", "Kc Qc", "Kd Qd", "Ah 7d");
        var table = HoldemTable.CreateWithDeck([81, 101, 51, 21, 200], 5, 10, 0, deck);
        Play(table, 3, Raise, 21);
        Play(table, 4, Call);
        Play(table, 0, Raise, 81);
        Play(table, 1, Raise, 101);
        Assert.Equal(41, table.View(2).LegalActions!.CallAmount);
        Play(table, 2, Call);
        Assert.Equal(new(true, false, 80, null, null), table.View(4).LegalActions);
        Play(table, 4, Fold);

        var final = table.View();
        Assert.Null(final.ActingSeat);
        Assert.Equal(Cards("As Ad 7c 4h 2s"), final.Board.AsEnumerable());
        Assert.Equal(new(HoldemStreet.Preflop, 1, 20), Assert.Single(final.UncalledReturns));
        var result = Assert.IsType<HoldemHandResult>(final.Result);
        Assert.Equal(HoldemEndReason.Showdown, result.Reason);
        Assert.Equal([105, 90, 60], result.Pots.Select(p => p.Chips));
        Assert.Equal([1, 2, 3, 0], result.Pots[0].EligibleSeats.AsEnumerable());
        Assert.Equal([1, 2, 0], result.Pots[1].EligibleSeats.AsEnumerable());
        Assert.Equal([1, 0], result.Pots[2].EligibleSeats.AsEnumerable());
        Assert.Equal([new HoldemAward(2, 53), new(3, 52)], result.Pots[0].Awards.AsEnumerable());
        Assert.Equal(new HoldemAward(2, 90), Assert.Single(result.Pots[1].Awards));
        Assert.Equal(new HoldemAward(0, 60), Assert.Single(result.Pots[2].Awards));
        Assert.Equal([60, 20, 143, 52, 179], final.Seats.Select(s => s.Stack));
        Assert.Equal(0, final.PotChips);
        Conserves(final, 454);

        Assert.Equal([1, 2, 3, 0], result.ShowdownHands.Select(h => h.Seat));
        Assert.Equal(result.ShowdownHands[1].Rank, result.ShowdownHands[2].Rank);
        Assert.Equal(Cards("Kc Qc"), result.ShowdownHands[1].Cards.AsEnumerable());
        Assert.Empty(final.OwnCards);
        Assert.Equal(Cards("Ah 7d"), table.View(4).OwnCards.AsEnumerable());
        Assert.DoesNotContain(table.View(4).Result!.ShowdownHands, shown => shown.Seat == 4);
    }

    [Fact]
    public void FoldedContributionLevelsAreDeadMoneyNotSeparatelyRoundedPots()
    {
        var deck = Rig(0, "As Ks Qs Js Ts", "2h 4c", "4d 6c", "5d 7c", "8s 9h", "3h 6d");
        var table = HoldemTable.CreateWithDeck([50, 50, 50, 50, 50], 1, 2, 0, deck);
        Play(table, 3, Raise, 5);
        Play(table, 4, Raise, 10);
        Play(table, 0, Call);
        Play(table, 1, Fold);
        Play(table, 2, Fold);
        Play(table, 3, Fold);
        CheckDown(table);

        var done = table.View();
        var pot = Assert.Single(done.Result!.Pots);
        Assert.Equal(28, pot.Chips);
        Assert.Equal([4, 0], pot.EligibleSeats.AsEnumerable());
        Assert.Equal([new HoldemAward(4, 14), new(0, 14)], pot.Awards.AsEnumerable());
        Assert.Equal([54, 49, 48, 45, 54], done.Seats.Select(s => s.Stack));
        Conserves(done, 250);
    }

    [Fact]
    public void FoldVictoryNeverRunsOutOrRevealsAndAllowsAnotherHand()
    {
        var table = New([100, 100]);
        var start = table.View();
        Play(table, 0, Fold);
        var done = table.View();
        Assert.Empty(done.Board);
        Assert.Empty(done.OwnCards);
        Assert.Null(done.LegalActions);
        Assert.Null(done.ActingSeat);
        Assert.Equal(HoldemEndReason.EveryoneElseFolded, done.Result!.Reason);
        Assert.Empty(done.Result.ShowdownHands);
        Assert.Empty(table.View(0).Result!.ShowdownHands);
        Assert.Empty(table.View(1).Result!.ShowdownHands);
        Assert.Equal(start.Revision + 1, done.Revision);
        Assert.Equal([95, 105], done.Seats.Select(s => s.Stack));
        Assert.Equal(new(HoldemStreet.Preflop, 1, 5), Assert.Single(done.UncalledReturns));
        Assert.Equal(new HoldemAward(1, 10), Assert.Single(Assert.Single(done.Result.Pots).Awards));
        Reject(HoldemRejection.NotYourTurn, () => table.Act(1, done.Revision, new(Check)));
        Reject(HoldemRejection.StaleRevision, () => table.StartNextHand(start.Revision));

        table.StartNextHand(done.Revision);
        var next = table.View(1);
        Assert.Equal(done.Revision + 1, next.Revision);
        Assert.Equal(2, next.HandNumber);
        Assert.Equal((1, 1, 0), (next.ButtonSeat, next.SmallBlindSeat, next.BigBlindSeat));
        Assert.Empty(next.UncalledReturns);
        Assert.Null(next.Result);
        Conserves(next, 200);
    }

    [Fact]
    public void BlindsAloneCanSettleAndFinishTheMatchWithoutAPhantomTurn()
    {
        var deck = Rig(0, "2c 3d 7h 9s Kc", "As Ad", "Qd Jd");
        var table = HoldemTable.CreateWithDeck([100, 2], 5, 10, 0, deck);
        var view = table.View();
        Assert.Equal(1, view.Revision);
        Assert.Null(view.ActingSeat);
        Assert.Equal(0, view.MatchWinnerSeat);
        Assert.Equal(5, view.Board.Length);
        Assert.Equal([102, 0], view.Seats.Select(s => s.Stack));
        Assert.Equal(new(HoldemStreet.Preflop, 0, 3), Assert.Single(view.UncalledReturns));
        Assert.Equal(new HoldemAward(0, 4), Assert.Single(Assert.Single(view.Result!.Pots).Awards));
        Reject(HoldemRejection.MatchComplete, () => table.StartNextHand(view.Revision));
        Conserves(view, 102);
    }

    [Fact]
    public void ProductionDealAndRunoutUseDistinctCardsForSixSeats()
    {
        var table = HoldemTable.Create([1, 1, 1, 1, 1, 1], 1, 1);
        foreach (var seat in new[] { 3, 4, 5, 0 })
            Play(table, seat, Call);

        var done = table.View();
        Assert.Equal(6, done.Result!.ShowdownHands.Length);
        Assert.Equal(5, done.Board.Length);
        var dealt = done.Result.ShowdownHands.SelectMany(h => h.Cards).Concat(done.Board).ToArray();
        Assert.Equal(17, dealt.Distinct().Count());
        Assert.All(dealt, c => Assert.Contains(c, StandardDeck()));
        Assert.Equal(6, Assert.Single(done.Result.Pots).Chips);
        Conserves(done, 6);
    }
}
