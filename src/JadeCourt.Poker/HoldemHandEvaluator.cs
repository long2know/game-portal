using System.Collections.Immutable;

namespace JadeCourt.Poker;

public enum HoldemHandCategory
{
    HighCard, OnePair, TwoPair, ThreeOfAKind, Straight, Flush, FullHouse, FourOfAKind, StraightFlush
}

public readonly record struct HoldemHandRank : IComparable<HoldemHandRank>
{
    private readonly int _strength;

    internal HoldemHandRank(HoldemHandCategory category, int first, int second = 0, int third = 0,
        int fourth = 0, int fifth = 0)
    {
        // The category followed by fixed-width rank slots sorts in hand-strength order.
        _strength = ((int)category << 20) | (first << 16) | (second << 12) | (third << 8) | (fourth << 4) | fifth;
    }

    public HoldemHandCategory Category => (HoldemHandCategory)(_strength >> 20);

    public ImmutableArray<HoldemRank> Tiebreakers
    {
        get
        {
            var ranks = ImmutableArray.CreateBuilder<HoldemRank>();
            for (var shift = 16; shift >= 0 && ((_strength >> shift) & 15) != 0; shift -= 4)
                ranks.Add((HoldemRank)((_strength >> shift) & 15));
            return ranks.ToImmutable();
        }
    }

    public int CompareTo(HoldemHandRank other) => _strength.CompareTo(other._strength);
}

public static class HoldemHandEvaluator
{
    /// <summary>Finds the strongest five-card hand from five to seven distinct standard cards.</summary>
    public static HoldemHandRank Evaluate(ReadOnlySpan<HoldemCard> cards)
    {
        if (cards.Length is < 5 or > 7)
            throw new ArgumentException("A poker hand needs five to seven cards.", nameof(cards));

        ulong seen = 0;
        foreach (var card in cards)
        {
            var bit = 1UL << card.Index;
            if ((seen & bit) != 0)
                throw new ArgumentException("A poker hand cannot repeat a card.", nameof(cards));
            seen |= bit;
        }

        HoldemHandRank best = default;
        Span<HoldemCard> five = stackalloc HoldemCard[5];
        for (var a = 0; a < cards.Length - 4; a++)
        for (var b = a + 1; b < cards.Length - 3; b++)
        for (var c = b + 1; c < cards.Length - 2; c++)
        for (var d = c + 1; d < cards.Length - 1; d++)
        for (var e = d + 1; e < cards.Length; e++)
        {
            five[0] = cards[a];
            five[1] = cards[b];
            five[2] = cards[c];
            five[3] = cards[d];
            five[4] = cards[e];
            var candidate = EvaluateFive(five);
            if (candidate.CompareTo(best) > 0)
                best = candidate;
        }
        return best;
    }

    private static HoldemHandRank EvaluateFive(ReadOnlySpan<HoldemCard> cards)
    {
        Span<int> counts = stackalloc int[15];
        counts.Clear();
        var ranks = 0;
        var flush = true;
        foreach (var card in cards)
        {
            var rank = (int)card.Rank;
            counts[rank]++;
            ranks |= 1 << rank;
            flush &= card.Suit == cards[0].Suit;
        }

        if (counts[14] != 0)
            ranks |= 1 << 1;
        var straight = 0;
        for (var high = 14; high >= 5; high--)
        {
            var mask = 31 << (high - 4);
            if ((ranks & mask) == mask)
            {
                straight = high;
                break;
            }
        }

        Span<int> singles = stackalloc int[5];
        var singleCount = 0;
        var pair = 0;
        var lowerPair = 0;
        var trips = 0;
        var quads = 0;
        for (var rank = 14; rank >= 2; rank--)
        {
            switch (counts[rank])
            {
                case 4: quads = rank; break;
                case 3: trips = rank; break;
                case 2 when pair == 0: pair = rank; break;
                case 2: lowerPair = rank; break;
                case 1: singles[singleCount++] = rank; break;
            }
        }

        if (flush && straight > 0)
            return new(HoldemHandCategory.StraightFlush, straight);
        if (quads > 0)
            return new(HoldemHandCategory.FourOfAKind, quads, singles[0]);
        if (trips > 0 && pair > 0)
            return new(HoldemHandCategory.FullHouse, trips, pair);
        if (flush)
            return new(HoldemHandCategory.Flush, singles[0], singles[1], singles[2], singles[3], singles[4]);
        if (straight > 0)
            return new(HoldemHandCategory.Straight, straight);
        if (trips > 0)
            return new(HoldemHandCategory.ThreeOfAKind, trips, singles[0], singles[1]);
        if (lowerPair > 0)
            return new(HoldemHandCategory.TwoPair, pair, lowerPair, singles[0]);
        if (pair > 0)
            return new(HoldemHandCategory.OnePair, pair, singles[0], singles[1], singles[2]);
        return new(HoldemHandCategory.HighCard, singles[0], singles[1], singles[2], singles[3], singles[4]);
    }
}
