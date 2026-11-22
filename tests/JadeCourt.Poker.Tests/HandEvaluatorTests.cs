using static JadeCourt.Poker.HoldemHandCategory;
using static JadeCourt.Poker.Tests.TableFixture;

namespace JadeCourt.Poker.Tests;

public sealed class HandEvaluatorTests
{
    [Theory]
    [InlineData("As Kd 9c 7h 5s 3d 2c", HighCard, "AK975")]
    [InlineData("Ac Ad Kh Js 9d 3s 2d", OnePair, "AKJ9")]
    [InlineData("Ac Ad Kh Ks Qh Qs 9c", TwoPair, "AKQ")]
    [InlineData("Qc Qd Qh As Ks 2d 3h", ThreeOfAKind, "QAK")]
    [InlineData("As 2d 3h 4c 5s Kc 8d", Straight, "5")]
    [InlineData("Ah Jh 9h 7h 5h 3h Ks", Flush, "AJ975")]
    [InlineData("Ac Ad Ah Kc Kd Kh 2s", FullHouse, "AK")]
    [InlineData("9c 9d 9h 9s Ac Kd 2h", FourOfAKind, "9A")]
    [InlineData("Ah 2h 3h 4h 5h 9s Kd", StraightFlush, "5")]
    public void EvaluatesBestFiveAndOrderedTiebreakers(string cards, HoldemHandCategory category, string ranks)
    {
        var hand = Cards(cards);
        var rank = HoldemHandEvaluator.Evaluate(hand);
        Assert.Equal(category, rank.Category);
        Assert.Equal(ranks, string.Concat(rank.Tiebreakers.Select(r => "23456789TJQKA"[(int)r - 2])));
        Array.Reverse(hand);
        Assert.Equal(rank, HoldemHandEvaluator.Evaluate(hand));
    }

    [Fact]
    public void ComparesLastKickersAndWheelButDoesNotRankSuits()
    {
        static HoldemHandRank Rank(string cards) => HoldemHandEvaluator.Evaluate(Cards(cards));
        Assert.True(Rank("Ac Ad Kd Js 9c 2h 3d").CompareTo(Rank("As Ah Ks Jc 8c 2s 3c")) > 0);
        Assert.True(Rank("As 2d 3h 4c 5s 6d").CompareTo(Rank("Ah 2c 3s 4d 5h")) > 0);
        Assert.True(Rank("2h 4h 6h 8h Th").CompareTo(Rank("Tc Jd Qh Ks As")) > 0);
        Assert.Equal(Rank("Tc Jd Qh Ks As 2d 3d"), Rank("Tc Jd Qh Ks As 4h 5s"));
    }

    [Fact]
    public void RejectsInvalidCardsAndDuplicateOrIncompleteHands()
    {
        Assert.Throws<ArgumentException>(() => HoldemHandEvaluator.Evaluate(Cards("Ac Ac 9c 8d 7s")));
        Assert.Throws<ArgumentException>(() => HoldemHandEvaluator.Evaluate(Cards("Ac 9c 8d 7s")));
        Assert.Throws<ArgumentException>(() => HoldemHandEvaluator.Evaluate([.. Cards("Ac 9c 8d 7s"), default]));
    }
}
