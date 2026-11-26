using JadeCourt.Games.Hanafuda;
using static JadeCourt.Games.Hanafuda.KoiKoiActionKind;
using static JadeCourt.Games.Hanafuda.KoiKoiGame;

namespace JadeCourt.Games.Tests;

public sealed class KoiKoiTests
{
    [Fact]
    public void DealConservesAllCardsAndRedealsTheWholeMonthBeforePlay()
    {
        var badFieldSeed = Enumerable.Range(0, 4_096).First(seed =>
            FirstShuffle(seed)[..8].GroupBy(id => id / 4).Any(month => month.Count() == 4));
        var saved = Start(badFieldSeed);
        Assert.Equal(Start(badFieldSeed), saved);
        Assert.NotEqual(Start(badFieldSeed + 1), saved);

        var first = View(saved, 0);
        var second = View(saved, 1);
        Assert.Equal("hand", first.Phase);
        Assert.Equal(first.DealerSeat, first.TurnSeat);
        Assert.Equal(1, first.Round);
        Assert.Equal(6, first.TotalRounds);
        Assert.Equal(8, first.OwnCards.Length);
        Assert.Equal(8, second.OwnCards.Length);
        Assert.Equal(8, first.Field.Length);
        Assert.Equal(24, first.StockCount);
        Assert.All(first.Field.GroupBy(card => card.Month), month => Assert.InRange(month.Count(), 1, 3));
        Assert.All(first.Seats, seat =>
        {
            Assert.Equal(0, seat.Score);
            Assert.False(seat.Continued);
            Assert.Empty(seat.Yaku);
        });

        var deck = AssertConserved(saved);
        Assert.Equal(48, deck.Select(card => card.Name).Distinct().Count());
        Assert.All(deck, card => Assert.Equal(card.Id / 4 + 1, card.Month));
        string[] plants = ["Pine", "Plum", "Cherry", "Wisteria", "Iris", "Peony", "Bush Clover", "Pampas",
            "Chrysanthemum", "Maple", "Willow", "Paulownia"];
        Assert.Equal(plants, deck.Select(card => card.Name.Split(" — ")[0]).Distinct());
        Assert.Equal(new[] { ("animal", 9), ("bright", 5), ("plain", 24), ("ribbon", 10) },
            deck.GroupBy(card => card.Kind).Select(kind => (kind.Key, kind.Count())).Order());
        Assert.Equal("Willow — Lightning", deck[43].Name);
        Assert.Equal("Paulownia — Phoenix", deck[44].Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADealtFullMonthOrFourPairsDoesNotAwardInstantPoints(bool fourPairs)
    {
        bool OpeningSet(IEnumerable<int> cards) => fourPairs
            ? cards.GroupBy(id => id / 4).Count(month => month.Count() == 2) == 4
            : cards.GroupBy(id => id / 4).Any(month => month.Count() == 4);
        var seed = Enumerable.Range(0, 20_000).First(value =>
        {
            var deck = FirstShuffle(value);
            return deck[..8].GroupBy(id => id / 4).All(month => month.Count() < 4)
                && (OpeningSet(deck[8..16]) || OpeningSet(deck[16..24]));
        });
        var saved = Start(seed);
        Assert.Contains(new[] { View(saved, 0), View(saved, 1) }, view => OpeningSet(view.OwnCards.Select(card => card.Id)));
        var opening = View(saved, 0);
        Assert.Equal("hand", opening.Phase);
        Assert.Null(opening.Result);
        Assert.All(opening.Seats, seat => Assert.Equal(0, seat.Score));
    }

    [Theory]
    [InlineData(16, null, new int[] { })]
    [InlineData(4, null, new[] { 5 })]
    [InlineData(8, 10, new[] { 9, 10 })]
    [InlineData(12, null, new[] { 13, 14, 15 })]
    public void PlaysMustChooseOnlyWhenExactlyTwoFieldCardsMatch(int card, int? chosen, int[] targets)
    {
        int[] field = [5, 9, 10, 13, 14, 15, 20, 21];
        var saved = Fixture([4, 8, 12, 16, 24, 28, 36, 40], [0, 2, 6, 11, 17, 25, 29, 37], field, [33]);
        var open = View(saved, 0);
        Assert.Equal(targets, Assert.Single(open.LegalActions.Plays, play => play.CardId == card).TargetIds);
        Reject(saved, 0, new(Play, card, 21));
        if (chosen.HasValue) Reject(saved, 0, new(Play, card));
        else if (targets.Length != 0) Reject(saved, 0, new(Play, card, targets[0]));

        var after = Apply(saved, 0, new(Play, card, chosen));
        var view = View(after, 0);
        int[] collected = chosen is int target ? [target] : targets;
        Assert.Equal(collected.Length == 0 ? [] : collected.Append(card).Order().ToArray(), Ids(view.Seats[0].Captured));
        Assert.Equal(field.Except(collected).Concat(collected.Length == 0 ? [card, 33] : [33]), Ids(view.Field));
        Assert.Equal(23, view.StockCount);
        Assert.Null(view.DrawnCard);
        Assert.Equal(1, view.TurnSeat);
        Assert.Empty(view.LegalActions.Plays);
        AssertConserved(after);
    }

    [Fact]
    public void AnAmbiguousDrawIsPublicButOnlyItsOwnerCanResolveItBeforeDeciding()
    {
        var saved = Fixture([8, 0, 4, 12, 16, 24, 36], [1, 5, 13, 17, 28, 38, 44],
            [9, 20, 22, 25, 27, 37, 40, 42, 6, 47], [21], [32, 34]);
        var pending = Apply(saved, 0, new(Play, 8));
        var own = View(pending, 0);
        var peer = View(pending, 1);
        Assert.Equal("draw", own.Phase);
        Assert.Equal(0, own.TurnSeat);
        Assert.Equal(21, own.DrawnCard?.Id);
        Assert.Equal(own.DrawnCard, peer.DrawnCard);
        Assert.Equal(21, own.StockCount);
        Assert.DoesNotContain(own.Field, card => card.Id == 21);
        Assert.Equal(new[] { 20, 22 }, own.LegalActions.DrawTargets);
        Assert.Empty(peer.LegalActions.DrawTargets);
        Assert.Empty(own.LegalActions.Plays);
        Assert.False(own.LegalActions.CanScore);
        Assert.False(own.LegalActions.CanContinue);
        Assert.Equal(new KoiKoiYaku("Flower Viewing", 5), Assert.Single(own.Seats[0].Yaku));
        AssertConserved(pending);
        Reject(pending, 0, new(Score));
        Reject(pending, 0, new(TakeDraw, TargetId: 25));
        Reject(pending, 1, new(TakeDraw, TargetId: 20), KoiKoiRejection.NotYourTurn);

        var decision = Apply(pending, 0, new(TakeDraw, TargetId: 20));
        var view = View(decision, 0);
        Assert.Equal("decision", view.Phase);
        Assert.Null(view.DrawnCard);
        Assert.True(view.LegalActions.CanScore);
        Assert.True(view.LegalActions.CanContinue);
        Assert.Equal(new[] { 8, 9, 20, 21, 32, 34 }, Ids(view.Seats[0].Captured));
        Assert.Contains(view.Field, card => card.Id == 22);
        Assert.Equal(own.StockCount, view.StockCount);
        AssertConserved(decision);
    }

    [Fact]
    public void OnlyTheBankingWinnerGetsPointsAndBothMultipliersCanApply()
    {
        var saved = Fixture([8, 12, 16, 20, 24], [28, 4, 13, 17, 36],
            [9, 29, 5, 6, 25, 33, 38, 46, 2, 18, 21, 27], [43, 31], [32, 34], [0, 1, 40, 41, 44, 45]);
        var decision = Apply(saved, 0, new(Play, 8));
        Assert.True(View(decision, 0).LegalActions.CanContinue);
        var bank = View(Apply(decision, 0, new(Score)), 0);
        Assert.Equal(new KoiKoiResult(0, 5, 5, false, false, 0), bank.Result);
        Assert.Equal(new[] { 5, 0 }, bank.Seats.Select(seat => seat.Score));

        var continued = Apply(decision, 0, new(Continue));
        var peerTurn = View(continued, 1);
        Assert.Equal(1, peerTurn.TurnSeat);
        Assert.True(peerTurn.Seats[0].Continued);
        Assert.Equal(0, peerTurn.Seats[0].Score);
        Assert.Null(peerTurn.Result);
        var peerDecision = Apply(continued, 1, new(Play, 28));
        var peer = View(peerDecision, 1);
        Assert.Equal(new KoiKoiYaku("Rainy Four Brights", 7), Assert.Single(peer.Seats[1].Yaku));
        Assert.True(peer.LegalActions.CanScore);
        Assert.False(peer.LegalActions.CanContinue);
        Reject(peerDecision, 1, new(Continue));

        var settled = Apply(peerDecision, 1, new(Score));
        var end = View(settled, 0);
        Assert.Equal("roundEnd", end.Phase);
        Assert.Equal(new KoiKoiResult(1, 28, 7, true, true, 1), end.Result);
        Assert.Equal(new[] { 0, 28 }, end.Seats.Select(seat => seat.Score));
        Assert.Null(end.TurnSeat);
        Assert.True(end.LegalActions.CanNextRound);
        Assert.True(View(settled, 1).LegalActions.CanNextRound);
        Reject(settled, 1, new(Score), KoiKoiRejection.RoundFinished);
        Assert.Equal(KoiKoiRejection.RoundInProgress,
            Assert.Throws<KoiKoiRejectedException>(() => NextRound(continued, 1)).Reason);

        var next = NextRound(settled, 0);
        Assert.Equal(next, NextRound(settled, 1));
        var deal = View(next, 0);
        Assert.Equal(2, deal.Round);
        Assert.Equal(1, deal.DealerSeat);
        Assert.Equal(1, deal.TurnSeat);
        Assert.Equal(new[] { 0, 28 }, deal.Seats.Select(seat => seat.Score));
        Assert.All(deal.Seats, seat =>
        {
            Assert.False(seat.Continued);
            Assert.Empty(seat.Captured);
        });
        AssertConserved(next);
    }

    [Fact]
    public void AnUnchangedSubtotalCannotBankEvenOnExhaustionAndADrawRotatesTheDealer()
    {
        var saved = Fixture([12], [16], [25, 37, 2, 47], [14, 18],
            [4, 6, 8, 9, 13, 15, 17, 19, 32, 34, 36, 38, 42, 43, 44, 45],
            [0, 3, 10, 11, 21, 22, 24, 26, 28, 29, 30, 31, 33, 35, 40, 41], continued0: 5);
        var passed = Apply(saved, 0, new(Play, 12));
        var last = View(passed, 1);
        Assert.Equal("hand", last.Phase);
        Assert.Equal(new KoiKoiYaku("Flower Viewing", 5), Assert.Single(last.Seats[0].Yaku));
        Assert.Equal(1, last.TurnSeat);

        var roundEnd = Apply(passed, 1, new(Play, 16));
        var end = View(roundEnd, 0);
        Assert.Equal("roundEnd", end.Phase);
        Assert.Equal(new KoiKoiResult(null, 0, 0, false, false, 1), end.Result);
        Assert.True(end.Seats[0].Continued);
        Assert.Equal(8, end.StockCount);
        Assert.All(end.Seats, seat => Assert.Equal(0, seat.HandCount));
        Assert.Equal(1, View(NextRound(roundEnd, 0), 0).DealerSeat);
    }

    [Fact]
    public void NewYakuOnPersonalLastTurnCanBeBankedButNotRisked()
    {
        var saved = Fixture([28], [16], [29, 25, 37, 2], [14],
            [5, 7, 12, 13, 17, 18, 20, 22, 32, 34, 40, 43, 44, 45, 46, 47],
            [0, 3, 8, 9, 10, 11, 21, 23, 26, 27, 33, 35, 36, 39, 41, 42]);
        var decision = Apply(saved, 0, new(Play, 28));
        var last = View(decision, 0);
        Assert.Equal("decision", last.Phase);
        Assert.Equal(new[] { 0, 1 }, last.Seats.Select(seat => seat.HandCount));
        Assert.True(last.LegalActions.CanScore);
        Assert.False(last.LegalActions.CanContinue);
        Reject(decision, 0, new(Continue));
        Assert.Equal(new KoiKoiResult(0, 5, 5, false, false, 0), View(Apply(decision, 0, new(Score)), 0).Result);
    }

    [Fact]
    public void AnEarlierCallerCanBankARealIncreaseOnTheLastTurnWithoutItsOwnCallMultiplier()
    {
        var saved = Fixture([], [28], [29, 25, 37, 2], [14],
            [13, 15, 16, 18, 20, 21, 22, 23, 24, 27, 33, 35, 40, 41, 44, 45, 46, 47],
            [4, 5, 6, 7, 8, 9, 17, 19, 30, 31, 32, 34, 36, 38, 42, 43], continued1: 5, turn: 1);
        var decision = Apply(saved, 1, new(Play, 28));
        var last = View(decision, 1);
        Assert.Equal(new[] { new KoiKoiYaku("Flower Viewing", 5), new KoiKoiYaku("Moon Viewing", 5) }, last.Seats[1].Yaku);
        Assert.True(last.LegalActions.CanScore);
        Assert.False(last.LegalActions.CanContinue);
        Assert.Equal(new KoiKoiResult(1, 20, 10, true, false, 1), View(Apply(decision, 1, new(Score)), 1).Result);
    }

    [Fact]
    public void AScorelessSixthRoundEndsTheTiedMatchWithoutANextRound()
    {
        var saved = Fixture([12], [16], [25, 37], [14, 18],
            [5, 7, 13, 15, 21, 22, 28, 31, 33, 35, 36, 39, 41, 43, 44, 45],
            [0, 1, 2, 3, 4, 6, 8, 9, 10, 11, 17, 19, 20, 23, 24, 26, 40, 42], round: 6);
        saved = Apply(Apply(saved, 0, new(Play, 12)), 1, new(Play, 16));
        var end = View(saved, 1);
        Assert.Equal("finished", end.Phase);
        Assert.Null(end.MatchWinnerSeat);
        Assert.Null(end.TurnSeat);
        Assert.False(end.LegalActions.CanNextRound);
        Assert.Equal(new KoiKoiResult(null, 0, 0, false, false, 1), end.Result);
        Assert.Equal(KoiKoiRejection.Finished, Assert.Throws<KoiKoiRejectedException>(() => NextRound(saved, 0)).Reason);
    }

    [Fact]
    public void ScoringUsesOnlyTheHighestBrightsAndNeverDuplicatesAnimalOrRibbonBonuses()
    {
        Exact([0, 8, 40]);
        Exact([0, 8, 28], new KoiKoiYaku("Three Brights", 5));
        Exact([0, 8, 28, 44], new KoiKoiYaku("Four Brights", 8));
        Exact([0, 8, 40, 44], new KoiKoiYaku("Rainy Four Brights", 7));
        Exact([0, 8, 28, 40, 44], new KoiKoiYaku("Five Brights", 10));
        Exact([20, 24, 36, 4, 12], new KoiKoiYaku("Boar, Deer, Butterfly", 7));
        Exact([4, 12, 16, 29]);
        Exact([4, 12, 16, 29, 41], new KoiKoiYaku("Animals", 1));
        Exact([4, 12, 16, 29, 41, 32], new KoiKoiYaku("Animals", 2));
        Exact([1, 5, 13, 17, 25], new KoiKoiYaku("Ribbons", 1));
        Exact([1, 5, 9, 13, 17], new KoiKoiYaku("Red Poetry Ribbons", 7));
        Exact([21, 33, 37, 42, 13, 17], new KoiKoiYaku("Blue Ribbons", 8));
        Exact([1, 5, 9, 21, 33, 37, 13, 42], new KoiKoiYaku("Red and Blue Ribbons", 12));
        int[] ninePlains = [2, 3, 6, 7, 10, 11, 14, 15, 18];
        Exact(ninePlains);
        Exact([.. ninePlains, 32], new KoiKoiYaku("Plain Cards", 1));
        Exact([.. ninePlains, 19, 8, 28, 32], new("Plain Cards", 2), new("Flower Viewing", 5), new("Moon Viewing", 5));

        static void Exact(int[] captures, params KoiKoiYaku[] expected) => Assert.Equal(expected, HanafudaCards.Yaku(captures));
    }

    [Fact]
    public void SeededLegalBotMatchesReplayThroughAllSixRoundsWithoutLeakingHiddenCards()
    {
        var openedBy = new HashSet<int>();
        for (var seed = 0; seed < 20; seed++)
        {
            var saved = Start(seed);
            openedBy.Add(View(saved, 0).DealerSeat);
            for (var round = 1; round <= 6; round++)
            {
                for (var step = 0; step < 48 && View(saved, 0).TurnSeat is int active; step++)
                {
                    var before = View(saved, active);
                    Assert.Equal(round, before.Round);
                    var other = View(saved, 1 - active);
                    Assert.Empty(other.LegalActions.Plays);
                    Assert.Empty(other.LegalActions.DrawTargets);
                    Assert.False(other.LegalActions.CanScore);
                    Assert.False(other.LegalActions.CanContinue);
                    Assert.Equal(before.Field, other.Field);
                    Assert.Equal(before.DrawnCard, other.DrawnCard);
                    AssertConserved(saved);

                    var next = active == 1 ? PlayBotTurn(saved, 1) : Apply(saved, 0, KoiKoiBot.Choose(before));
                    if (active == 1)
                    {
                        Assert.Equal(next, PlayBotTurn(saved, 1));
                        var after = View(next, 1);
                        Assert.NotEqual(1, after.TurnSeat);
                        Assert.Equal(before.OwnCards.Length - (before.Phase == "hand" ? 1 : 0), after.OwnCards.Length);
                    }
                    saved = next;
                }
                var end = View(saved, 0);
                Assert.Equal(round == 6 ? "finished" : "roundEnd", end.Phase);
                Assert.Equal(round != 6, end.LegalActions.CanNextRound);
                AssertConserved(saved);
                if (round < 6)
                {
                    Assert.Equal(NextRound(saved, 1), NextRound(saved, 0));
                    saved = NextRound(saved, 0);
                }
                else
                {
                    var expected = end.Seats[0].Score.CompareTo(end.Seats[1].Score);
                    Assert.Equal(expected == 0 ? (int?)null : expected > 0 ? 0 : 1, end.MatchWinnerSeat);
                }
            }
        }
        Assert.Equal(new[] { 0, 1 }, openedBy.Order());
    }

    [Fact]
    public void CheckpointRejectsWrongVersionAndDuplicatedPrivateCards()
    {
        var saved = Start(4);
        var wrongVersion = saved.Replace("\"Version\":1,", "\"Version\":2,", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => View(wrongVersion, 0));
        var state = Read(saved);
        var corrupt = CheckpointCodec.Save("koi-koi", state with { Field = state.Field.SetItem(0, state.Stock[0]) }, 4_096);
        Assert.Throws<InvalidDataException>(() => View(corrupt, 0));
    }

    private static int[] FirstShuffle(int seed)
    {
        var random = new Random(seed);
        random.Next(2);
        var deck = Enumerable.Range(0, 48).ToArray();
        random.Shuffle(deck);
        return deck;
    }

    private static string Fixture(int[] hand0, int[] hand1, int[] field, int[] top, int[]? captured0 = null, int[]? captured1 = null,
        int continued0 = 0, int continued1 = 0, int round = 1, int turn = 0)
    {
        captured0 ??= [];
        captured1 ??= [];
        var placed = hand0.Concat(hand1).Concat(field).Concat(top).Concat(captured0).Concat(captured1);
        var state = new KoiKoiState(round, 0, turn, KoiKoiPhase.Hand,
            [new([.. hand0.Order()], [.. captured0], 0, continued0), new([.. hand1.Order()], [.. captured1], 0, continued1)],
            [.. field], [.. top, .. Enumerable.Range(0, 48).Except(placed)], null, null,
            Read(Start(32)).FutureDeals[(round - 1)..]);
        return CheckpointCodec.Save("koi-koi", state, 4_096);
    }

    private static KoiKoiCard[] AssertConserved(string checkpoint)
    {
        var first = View(checkpoint, 0);
        var second = View(checkpoint, 1);
        var state = Read(checkpoint);
        var pending = first.DrawnCard is KoiKoiCard drawn ? new[] { drawn } : [];
        var publiclyKnown = first.Field.Concat(first.Seats.SelectMany(seat => seat.Captured)).Concat(pending);
        Assert.Equal(first.StockCount, state.Stock.Length);
        Assert.DoesNotContain(second.OwnCards, card => publiclyKnown.Contains(card) || first.OwnCards.Contains(card));
        var deck = publiclyKnown.Concat(first.OwnCards).Concat(second.OwnCards).Concat(state.Stock.Select(HanafudaCards.Card))
            .OrderBy(card => card.Id).ToArray();
        Assert.Equal(Enumerable.Range(0, 48), Ids(deck));
        return deck;
    }

    private static int[] Ids(KoiKoiCard[] cards) => cards.Select(card => card.Id).ToArray();
    private static KoiKoiState Read(string checkpoint) => CheckpointCodec.Load<KoiKoiState>("koi-koi", checkpoint, 4_096);

    private static void Reject(string checkpoint, int seat, KoiKoiAction action, KoiKoiRejection reason = KoiKoiRejection.IllegalAction) =>
        Assert.Equal(reason, Assert.Throws<KoiKoiRejectedException>(() => Apply(checkpoint, seat, action)).Reason);
}
