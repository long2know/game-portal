using System.Numerics;

namespace JadeCourt.Poker;

/// <summary>A deterministic, bounded policy using only the acting seat's view.</summary>
public static class HoldemBot
{
    public static HoldemAction Decide(HoldemView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.ViewerSeat is not int seat || view.ActingSeat != seat
            || view.LegalActions is not { } legal || view.OwnCards.Length != 2)
            throw new ArgumentException("The bot needs the acting seat's view.", nameof(view));

        var strength = view.Street == HoldemStreet.Preflop ? StartingStrength(view) : BoardStrength(view);
        var call = legal.CallAmount.GetValueOrDefault();
        var potAfterCall = (long)view.PotChips + call;
        if (strength >= 3 && (legal.Bet ?? legal.Raise) is { } range)
        {
            var target = view.CurrentBet + Math.Max(view.BigBlind, potAfterCall / 2);
            if (strength == 4 || range.MinTo <= target)
            {
                var to = (int)Math.Clamp(target, range.MinTo, range.MaxTo);
                return new(legal.Bet.HasValue ? HoldemActionKind.Bet : HoldemActionKind.Raise, to);
            }
        }

        if (legal.CanCheck)
            return new(HoldemActionKind.Check);
        if (legal.CallAmount.HasValue && (strength >= 3
            || strength == 2 && 3L * call <= potAfterCall
            || strength == 1 && call <= view.BigBlind && 6L * call <= potAfterCall))
            return new(HoldemActionKind.Call);
        return legal.CanFold ? new(HoldemActionKind.Fold)
            : throw new ArgumentException("The view has no legal fallback action.", nameof(view));
    }

    private static int StartingStrength(HoldemView view)
    {
        var first = view.OwnCards[0];
        var second = view.OwnCards[1];
        var high = Math.Max((int)first.Rank, (int)second.Rank);
        var low = Math.Min((int)first.Rank, (int)second.Rank);
        var pair = high == low;
        var suited = first.Suit == second.Suit;
        if (pair && high >= 10 || high == 14 && low >= 12)
            return 4;
        if (pair && high >= 6 || high >= 13 && low >= 10 || suited && high == 14)
            return 3;
        if (pair || high == 14 || suited && (high >= 10 || high - low <= 2))
            return 2;
        return suited || high >= 12 ? 1 : 0;
    }

    private static int BoardStrength(HoldemView view)
    {
        HoldemCard[] cards = [.. view.OwnCards, .. view.Board];
        var best = HoldemHandEvaluator.Evaluate(cards);
        if (view.Board.Length == 5 && best == HoldemHandEvaluator.Evaluate(view.Board.AsSpan()))
            return 1;
        if (best.Category >= HoldemHandCategory.Straight)
            return 4;
        if (best.Category >= HoldemHandCategory.TwoPair)
            return 3;
        if (best.Category == HoldemHandCategory.OnePair)
        {
            var pair = best.Tiebreakers[0];
            if (view.OwnCards.Any(c => c.Rank == pair))
                return pair >= view.Board.Max(c => c.Rank) ? 3 : 2;
        }
        if (view.Board.Length < 5 && HasDraw(view, cards))
            return 2;
        return best.Category == HoldemHandCategory.OnePair ? 1 : 0;
    }

    private static bool HasDraw(HoldemView view, HoldemCard[] cards)
    {
        if (view.OwnCards.Any(hole => cards.Count(c => c.Suit == hole.Suit) == 4))
            return true;
        static int RankBits(HoldemCard card)
            => (1 << (int)card.Rank) | (card.Rank == HoldemRank.Ace ? 1 << 1 : 0);

        var ranks = cards.Aggregate(0, (bits, card) => bits | RankBits(card));
        var privateRanks = RankBits(view.OwnCards[0]) | RankBits(view.OwnCards[1]);
        for (var high = 5; high <= 14; high++)
        {
            var mask = 31 << (high - 4);
            if ((privateRanks & mask) != 0 && BitOperations.PopCount((uint)(ranks & mask)) == 4)
                return true;
        }
        return false;
    }
}
