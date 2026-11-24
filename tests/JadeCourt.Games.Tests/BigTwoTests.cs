using System.Numerics;
using JadeCourt.Games.BigTwo;
using static JadeCourt.Games.BigTwo.BigTwoActionKind;
using static JadeCourt.Games.BigTwo.BigTwoGame;

namespace JadeCourt.Games.Tests;

public sealed class BigTwoTests
{
    [Fact]
    public void SeededDealGivesEachSeatThirteenUniqueCardsAndOnlyTheDiamondHolderCanOpen()
    {
        var saved = Start(419);
        Assert.Equal(saved, Start(419));
        Assert.NotEqual(saved, Start(814));
        var views = Enumerable.Range(0, SeatCount).Select(seat => View(saved, seat)).ToArray();
        var cards = views.SelectMany(view => view.OwnCards).ToArray();
        Assert.Equal(Enumerable.Range(0, 52), cards.Select(card => card.Id).Order());
        Assert.Equal(from rank in Enumerable.Range(2, 13) from suit in Enumerable.Range(0, 4) select (rank, suit),
            cards.Select(card => (card.Rank, card.Suit)).Order());
        var first = Array.FindIndex(views, view => view.OwnCards.Any(card => card is { Rank: 3, Suit: 1 }));

        for (var seat = 0; seat < views.Length; seat++)
        {
            var view = views[seat];
            Assert.Equal(first, view.TurnSeat);
            Assert.Null(view.WinnerSeat);
            Assert.Null(view.LastPlay);
            Assert.False(view.CanPass);
            Assert.Equal(Enumerable.Range(0, 4).Select(index => new BigTwoSeat(index, 13)), view.Seats);
            Assert.Equal(13, view.OwnCards.Length);
            Assert.Equal(view.OwnCards.OrderBy(card => card.Rank == 2 ? 15 : card.Rank)
                .ThenBy(card => card.Suit == 1 ? -1 : card.Suit), view.OwnCards);
            if (seat != first) Assert.Empty(view.LegalMasks);
        }
        Assert.Contains(1, views[first].LegalMasks);
        Assert.All(views[first].LegalMasks, mask => Assert.Contains(Id(3, 1), Cards(views[first], mask)));
        Reject(saved, (first + 1) % 4, new(Pass), BigTwoRejection.NotYourTurn);
        Reject(saved, first, new(Pass));
        Reject(saved, first, new(Play, [views[first].OwnCards[1].Id]));
        Reject(saved, first, new(Play, [views[(first + 1) % 4].OwnCards[0].Id]));
    }

    [Fact]
    public void OpeningDiamondMayBeInACombinationButTheLeadCannotOmitIt()
    {
        int[] pair = [Id(3, 1), Id(3, 0)];
        var higherPair = new[] { Id(4, 1), Id(4, 0) };
        var deck = pair.Concat(higherPair).Concat(Enumerable.Range(0, 52).Except(pair.Concat(higherPair)));
        var saved = Fixture(0, deck.Chunk(13).ToArray());
        var view = View(saved, 0);
        Assert.Contains(view.LegalMasks, mask => Cards(view, mask).SequenceEqual(pair));
        Assert.DoesNotContain(view.LegalMasks, mask => Cards(view, mask).SequenceEqual(higherPair));
        Reject(saved, 0, new(Play, higherPair));
        Reject(saved, 0, new(Play, [pair[0], pair[0]]));
        var after = View(Apply(saved, 0, new(Play, pair.Reverse().ToArray())), 1);
        var play = Assert.IsType<BigTwoPlay>(after.LastPlay);
        Assert.Equal("pair", play.Kind);
        Assert.Equal(pair, play.Cards.Select(card => card.Id));
        Assert.Equal(new[] { 11, 13, 13, 13 }, after.Seats.Select(seat => seat.CardsRemaining));
        Assert.True(after.CanPass);
        Assert.All(after.LegalMasks, mask => Assert.Equal(2, BitOperations.PopCount((uint)mask)));
    }

    [Fact]
    public void SinglesPairsAndTriplesRespectSuitOrderAndNeverBeatADifferentSize()
    {
        var sameRank = new[] { 1, 0, 2, 3 }.Select(suit => Value(Id(7, suit))).ToArray();
        for (var index = 1; index < sameRank.Length; index++) Stronger(sameRank[index], sameRank[index - 1]);
        Stronger(Value(Id(2, 1)), Value(Id(14, 3)));
        Stronger(Value(Id(4, 1)), Value(Id(3, 3)));
        var pair = Value(Id(7, 2), Id(7, 3));
        Stronger(pair, Value(Id(7, 0), Id(7, 1)));
        Stronger(Value(Id(8, 0), Id(8, 1)), pair);
        var triple = Value(Id(7, 1), Id(7, 0), Id(7, 3));
        Stronger(triple, Value(Id(7, 1), Id(7, 0), Id(7, 2)));
        Stronger(Value(Id(2, 1), Id(2, 0), Id(2, 2)), triple);
        Assert.False(triple.Beats(pair));
        Assert.False(pair.Beats(sameRank[0]));
        Assert.Null(Evaluate(Id(3, 1), Id(4, 1)));
        Assert.Null(Evaluate(Id(3, 1), Id(3, 0), Id(4, 1)));
        Assert.Null(Evaluate(Id(3, 1), Id(3, 0), Id(3, 2), Id(3, 3)));
    }

    [Fact]
    public void FiveCardCategoriesAndTheirTieBreaksFollowTheHouseRules()
    {
        var straight = Value(Id(10, 0), Id(11, 1), Id(12, 2), Id(13, 3), Id(14, 1));
        var flush = Value(Id(3, 1), Id(4, 1), Id(7, 1), Id(9, 1), Id(11, 1));
        var house = Value(Id(3, 0), Id(3, 1), Id(3, 2), Id(2, 1), Id(2, 2));
        var four = Value(Id(3, 0), Id(3, 1), Id(3, 2), Id(3, 3), Id(2, 3));
        var straightFlush = Value(Id(3, 1), Id(4, 1), Id(5, 1), Id(6, 1), Id(7, 1));
        Assert.Equal(new[] { "straight", "flush", "full-house", "four-of-a-kind", "straight-flush" },
            new[] { straight, flush, house, four, straightFlush }.Select(value => value.Name));
        Stronger(flush, straight);
        Stronger(house, flush);
        Stronger(four, house);
        Stronger(straightFlush, four);

        Stronger(Value(Id(10, 1), Id(11, 0), Id(12, 2), Id(13, 1), Id(14, 0)), straight);
        Stronger(straight, Value(Id(9, 0), Id(10, 1), Id(11, 2), Id(12, 0), Id(13, 3)));
        Stronger(Value(Id(3, 1), Id(4, 1), Id(7, 1), Id(9, 1), Id(12, 1)),
            Value(Id(3, 3), Id(4, 3), Id(7, 3), Id(9, 3), Id(11, 3)));
        Stronger(Value(Id(3, 0), Id(4, 0), Id(7, 0), Id(9, 0), Id(11, 0)), flush);
        Stronger(Value(Id(3, 1), Id(3, 2), Id(4, 0), Id(4, 1), Id(4, 2)), house);
        Stronger(Value(Id(3, 1), Id(4, 0), Id(4, 1), Id(4, 2), Id(4, 3)), four);
        Stronger(Value(Id(3, 0), Id(4, 0), Id(5, 0), Id(6, 0), Id(7, 0)), straightFlush);
        Assert.False(straightFlush.Beats(straightFlush));
    }

    [Fact]
    public void StraightsCannotWrapUseATwoOrRepeatRanks()
    {
        Assert.Null(Evaluate(Id(2, 0), Id(3, 1), Id(4, 2), Id(5, 3), Id(6, 0)));
        Assert.Null(Evaluate(Id(11, 0), Id(12, 1), Id(13, 2), Id(14, 3), Id(2, 0)));
        Assert.Null(Evaluate(Id(14, 0), Id(2, 1), Id(3, 2), Id(4, 3), Id(5, 0)));
        Assert.Null(Evaluate(Id(3, 0), Id(4, 1), Id(5, 2), Id(5, 3), Id(7, 0)));
        Assert.Equal("straight", Value(Id(3, 0), Id(4, 1), Id(5, 2), Id(6, 3), Id(7, 0)).Name);
        Assert.Equal("flush", Value(Id(11, 3), Id(12, 3), Id(13, 3), Id(14, 3), Id(2, 3)).Name);
    }

    [Fact]
    public void FollowingMasksAndReducerBothRequireAFiveCardPlayThatBeatsTheTarget()
    {
        int[] flush = [Id(3, 1), Id(5, 1), Id(7, 1), Id(9, 1), Id(11, 1)];
        int[] house = [Id(6, 0), Id(6, 2), Id(8, 0), Id(8, 2), Id(8, 3)];
        var highCard = Id(2, 3);
        var saved = Fixture(1, [[Id(4, 1)], [.. house, highCard], [Id(14, 0)], [Id(13, 0)]], new(0, flush));
        var view = View(saved, 1);
        Assert.Equal("flush", view.LastPlay?.Kind);
        Assert.Equal(house, Cards(view, Assert.Single(view.LegalMasks)));
        Reject(saved, 1, new(Play, [highCard]));
        Reject(saved, 1, new(Play, flush));
        var next = View(Apply(saved, 1, new(Play, house)), 2);
        Assert.Equal(2, next.TurnSeat);
        Assert.Equal("full-house", next.LastPlay?.Kind);
        Assert.Equal(house, next.LastPlay!.Cards.Select(card => card.Id));
        Assert.Empty(next.LegalMasks);
        Assert.True(next.CanPass);
        Assert.Empty(View(saved, 1, playing: false).LegalMasks);
        Assert.False(View(saved, 1, playing: false).CanPass);
    }

    [Fact]
    public void PriorPassersMayPlayAgainAndThreeConsecutivePassesGiveTheLeaderAFreeLead()
    {
        var saved = Fixture(1, [[Id(4, 0), Id(14, 3)], [Id(4, 1), Id(9, 2)], [Id(8, 1), Id(8, 2)], [Id(5, 3), Id(6, 1)]],
            new(0, [Id(3, 1)]));
        Assert.NotEmpty(View(saved, 1).LegalMasks);
        saved = Apply(saved, 1, new(Pass));
        Assert.Equal(0, View(saved, 2).LastPlay?.Seat);
        saved = Apply(saved, 2, new(Play, [Id(8, 2)]));
        saved = Apply(saved, 3, new(Pass));
        saved = Apply(saved, 0, new(Pass));
        Assert.Contains(Id(9, 2), Cards(View(saved, 1), Assert.Single(View(saved, 1).LegalMasks)));
        saved = Apply(saved, 1, new(Play, [Id(9, 2)]));
        saved = Apply(saved, 2, new(Pass));
        Assert.Equal(1, View(saved, 3).LastPlay?.Seat);
        saved = Apply(saved, 3, new(Pass));
        Assert.Equal(1, View(saved, 0).LastPlay?.Seat);
        saved = Apply(saved, 0, new(Pass));
        var reset = View(saved, 1);
        Assert.Equal(1, reset.TurnSeat);
        Assert.Null(reset.LastPlay);
        Assert.False(reset.CanPass);
        Assert.Equal(new[] { Id(4, 1) }, Cards(reset, Assert.Single(reset.LegalMasks)));
        Reject(saved, 1, new(Pass));
        var finished = Apply(saved, 1, new(Play, [Id(4, 1)]));
        var end = View(finished, 1);
        Assert.Equal(1, end.WinnerSeat);
        Assert.Null(end.TurnSeat);
        Assert.Equal(1, end.LastPlay?.Seat);
        Assert.Empty(end.OwnCards);
        Assert.Empty(end.LegalMasks);
        Assert.False(end.CanPass);
        Assert.Equal(new[] { 2, 0, 1, 2 }, end.Seats.Select(seat => seat.CardsRemaining));
        Reject(finished, 1, new(Pass), BigTwoRejection.Finished);
    }

    [Fact]
    public void BotRepliesOnlyUseTheActingHandAndStopBeforeTheHumanPlays()
    {
        var hands = new[] { new[] { Id(7, 0) }, [Id(6, 1), Id(9, 0)], [Id(5, 2)], [Id(4, 2), Id(8, 2)] };
        var saved = Fixture(1, hands, new(0, [Id(3, 1)]));
        var projection = View(saved, 1);
        var action = SelectBotAction(projection);
        Assert.Equal(Play, action.Kind);
        Assert.Equal(new[] { Id(6, 1) }, action.Cards);

        hands[0] = [Id(2, 3)];
        hands[2] = [Id(3, 0)];
        hands[3] = [Id(7, 3), Id(8, 3)];
        var changed = Fixture(1, hands, new(0, [Id(3, 1)]));
        Assert.Equal(action.Cards, SelectBotAction(View(changed, 1)).Cards);
        var after = View(PlayBots(saved, 0), 0);
        Assert.Equal(0, after.TurnSeat);
        Assert.Equal(3, after.LastPlay?.Seat);
        Assert.Equal(Id(8, 2), Assert.Single(after.LastPlay!.Cards).Id);
        Assert.Equal(new[] { 1, 1, 1, 1 }, after.Seats.Select(seat => seat.CardsRemaining));
        Assert.True(after.CanPass);
        Assert.Empty(after.LegalMasks);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(143)]
    public void ASeededBotMatchRunsFromOpeningToFirstOut(int seed)
    {
        var saved = PlayBots(Start(seed), 0);
        for (var move = 0; move < 52; move++)
        {
            var view = View(saved, 0);
            if (view.WinnerSeat is int winner)
            {
                Assert.Null(view.TurnSeat);
                Assert.Empty(View(saved, winner).OwnCards);
                Assert.Equal(winner, view.LastPlay?.Seat);
                return;
            }
            Assert.Equal(0, view.TurnSeat);
            var action = view.LegalMasks.Length == 0 ? new BigTwoAction(Pass) : new(Play, Cards(view, view.LegalMasks[0]));
            saved = PlayBots(Apply(saved, 0, action), 0);
        }
        Assert.Fail("The hand did not finish within one human action per card in the deck.");
    }

    [Fact]
    public void InvalidCheckpointsCannotProjectOrActWithADuplicateCard()
    {
        var saved = Start(321);
        var deal = CheckpointCodec.Load<BigTwoState>("big-two", saved, 4_096);
        var hands = (int[][])deal.Hands.Clone();
        hands[0] = hands[1];
        var corrupted = CheckpointCodec.Save("big-two", deal with { Hands = hands }, 4_096);
        Assert.Throws<InvalidDataException>(() => View(corrupted, 0));
        Assert.Throws<InvalidDataException>(() => Apply(corrupted, 0, new(Pass)));
    }

    private static string Fixture(int turn, int[][] hands, BigTwoTrick? last = null) =>
        CheckpointCodec.Save("big-two", new BigTwoState(hands.Select(Sorted).ToArray(), turn, null,
            last is null ? null : last with { Cards = Sorted(last.Cards) }), 4_096);

    private static int[] Sorted(int[] ids) => ids.OrderBy(BigTwoCombinations.Strength).ToArray();
    private static int[] Cards(BigTwoView view, int mask) =>
        view.OwnCards.Where((_, index) => (mask & (1 << index)) != 0).Select(card => card.Id).ToArray();
    private static int Id(int rank, int suit) => (rank - 2) * 4 + suit;
    private static BigTwoValue? Evaluate(params int[] ids) => BigTwoCombinations.Evaluate(Sorted(ids));
    private static BigTwoValue Value(params int[] ids) => Assert.IsType<BigTwoValue>(Evaluate(ids));

    private static void Stronger(BigTwoValue high, BigTwoValue low)
    {
        Assert.True(high.Beats(low), $"{high} must beat {low}.");
        Assert.False(low.Beats(high));
    }

    private static void Reject(string saved, int seat, BigTwoAction action, BigTwoRejection rejection = BigTwoRejection.IllegalAction) =>
        Assert.Equal(rejection, Assert.Throws<BigTwoRejectedException>(() => Apply(saved, seat, action)).Reason);
}
