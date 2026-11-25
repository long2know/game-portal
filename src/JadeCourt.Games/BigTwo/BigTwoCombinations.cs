namespace JadeCourt.Games.BigTwo;

internal enum BigTwoKind { Single, Pair, Triple, Straight, Flush, FullHouse, FourOfAKind, StraightFlush }

internal readonly record struct BigTwoValue(int Count, BigTwoKind Kind, int Strength)
{
    public bool Beats(BigTwoValue other) =>
        Count == other.Count && (Kind > other.Kind || Kind == other.Kind && Strength > other.Strength);

    public string Name => Kind switch
    {
        BigTwoKind.Single => "single",
        BigTwoKind.Pair => "pair",
        BigTwoKind.Triple => "triple",
        BigTwoKind.Straight => "straight",
        BigTwoKind.Flush => "flush",
        BigTwoKind.FullHouse => "full-house",
        BigTwoKind.FourOfAKind => "four-of-a-kind",
        BigTwoKind.StraightFlush => "straight-flush",
        _ => throw new InvalidDataException("Unknown Big Two combination.")
    };
}

internal static class BigTwoCombinations
{
    public static BigTwoCard Card(int id) => new(id, Rank(id), Suit(id));

    private static int Rank(int id) => id / 4 + 2;
    private static int Suit(int id) => id % 4;
    private static int RankStrength(int rank) => rank == 2 ? 12 : rank - 3;
    public static int Strength(int id)
    {
        var suit = Suit(id);
        return RankStrength(Rank(id)) * 4 + (suit < 2 ? 1 - suit : suit);
    }

    // The engine supplies distinct cards sorted by strength.
    public static BigTwoValue? Evaluate(ReadOnlySpan<int> cards)
    {
        if (cards.Length is not (1 or 2 or 3 or 5)) return null;
        var low = Rank(cards[0]);
        var high = Rank(cards[^1]);
        var top = Strength(cards[^1]);
        if (cards.Length == 1) return new(1, BigTwoKind.Single, top);
        if (cards.Length is 2 or 3)
            return low == high ? new(cards.Length, cards.Length == 2 ? BigTwoKind.Pair : BigTwoKind.Triple, top) : null;

        var suit = Suit(cards[0]);
        var flush = true;
        var straight = low >= 3 && high - low == 4;
        for (var index = 1; index < cards.Length; index++)
        {
            flush &= Suit(cards[index]) == suit;
            straight &= Rank(cards[index]) == low + index;
        }
        if (straight && flush) return new(5, BigTwoKind.StraightFlush, top);

        var second = Rank(cards[1]);
        var middle = Rank(cards[2]);
        var fourth = Rank(cards[3]);
        if (low == fourth || second == high) return new(5, BigTwoKind.FourOfAKind, RankStrength(middle));
        if ((low == middle && fourth == high) || (low == second && middle == high))
            return new(5, BigTwoKind.FullHouse, RankStrength(middle));
        if (flush)
        {
            var key = 0;
            for (var index = cards.Length - 1; index >= 0; index--) key = (key << 6) | Strength(cards[index]);
            return new(5, BigTwoKind.Flush, key);
        }
        return straight ? new(5, BigTwoKind.Straight, top) : null;
    }
}
