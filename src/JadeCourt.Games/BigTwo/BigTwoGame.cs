using System.Numerics;
using System.Security.Cryptography;
using static JadeCourt.Games.BigTwo.BigTwoCombinations;

namespace JadeCourt.Games.BigTwo;

public readonly record struct BigTwoCard(int Id, int Rank, int Suit);
public readonly record struct BigTwoSeat(int Seat, int CardsRemaining);
public sealed record BigTwoPlay(int Seat, BigTwoCard[] Cards, string Kind);
public sealed record BigTwoView(int? TurnSeat, int? WinnerSeat, BigTwoSeat[] Seats, BigTwoCard[] OwnCards,
    BigTwoPlay? LastPlay, int[] LegalMasks, bool CanPass);

public enum BigTwoActionKind { Play = 1, Pass }
public readonly record struct BigTwoAction(BigTwoActionKind Kind, int[]? Cards = null);
public enum BigTwoRejection { NotYourTurn, IllegalAction, Finished }
public sealed class BigTwoRejectedException(BigTwoRejection reason) : Exception(reason.ToString())
{
    public BigTwoRejection Reason { get; } = reason;
}

internal sealed record BigTwoTrick(int Seat, int[] Cards);
internal sealed record BigTwoState(int[][] Hands, int? TurnSeat, int? WinnerSeat, BigTwoTrick? LastPlay);

public static class BigTwoGame
{
    public const int SeatCount = 4;
    public const int DeckSize = 52;
    private const int CardsPerHand = DeckSize / SeatCount;
    private const int OpeningCard = 5; // Three of diamonds.
    private const string Game = "big-two";
    private const int CheckpointLimit = 4_096;

    public static string Start(int? seed = null)
    {
        var deck = Enumerable.Range(0, DeckSize).ToArray();
        if (seed is int value) new Random(value).Shuffle(deck);
        else RandomNumberGenerator.Shuffle(deck.AsSpan());

        var hands = new int[SeatCount][];
        for (var seat = 0; seat < hands.Length; seat++)
        {
            var start = seat * CardsPerHand;
            hands[seat] = deck[start..(start + CardsPerHand)];
            Array.Sort(hands[seat], static (left, right) => Strength(left).CompareTo(Strength(right)));
        }
        return Save(new(hands, Array.FindIndex(hands, hand => hand[0] == OpeningCard), null, null));
    }

    public static BigTwoView View(string checkpoint, int viewer, bool playing = true) => Project(Load(checkpoint), viewer, playing);

    public static string Apply(string checkpoint, int seat, BigTwoAction action) => Save(Apply(Load(checkpoint), seat, action));

    public static string PlayBots(string checkpoint, int humanSeat, CancellationToken stop = default)
    {
        if (!IsSeat(humanSeat)) throw new ArgumentOutOfRangeException(nameof(humanSeat));
        var state = Load(checkpoint);
        for (var count = 0; count < SeatCount - 1 && state.TurnSeat is int seat && seat != humanSeat; count++)
        {
            stop.ThrowIfCancellationRequested();
            try { state = Apply(state, seat, SelectBotAction(Project(state, seat, true))); }
            catch (BigTwoRejectedException exception)
            {
                throw new InvalidDataException("The Big Two bot rejected its own action.", exception);
            }
        }
        if (state.TurnSeat is int next && next != humanSeat)
            throw new InvalidDataException("Big Two bot turns did not return to the human seat.");
        return Save(state);
    }

    internal static BigTwoAction SelectBotAction(BigTwoView view)
    {
        var best = 0;
        var count = 0;
        foreach (var mask in view.LegalMasks)
        {
            var size = BitOperations.PopCount((uint)mask);
            if (size <= count) continue;
            best = mask;
            count = size;
        }
        if (best != 0)
            return new(BigTwoActionKind.Play, view.OwnCards.Where((_, index) => (best & (1 << index)) != 0)
                .Select(card => card.Id).ToArray());
        if (view.CanPass) return new(BigTwoActionKind.Pass);
        throw new InvalidDataException("The Big Two bot has no legal action.");
    }

    private static BigTwoState Apply(BigTwoState state, int seat, BigTwoAction action)
    {
        if (state.WinnerSeat.HasValue) throw new BigTwoRejectedException(BigTwoRejection.Finished);
        if (state.TurnSeat != seat) throw new BigTwoRejectedException(BigTwoRejection.NotYourTurn);

        var next = (seat + 1) % SeatCount;
        // Returning to the last player without a new play means all three other seats passed.
        if (action is { Kind: BigTwoActionKind.Pass, Cards: null } && state.LastPlay is { } last)
            return state with { TurnSeat = next, LastPlay = last.Seat == next ? null : last };
        if (action.Kind != BigTwoActionKind.Play || action.Cards is not { Length: 1 or 2 or 3 or 5 } ids)
            throw new BigTwoRejectedException(BigTwoRejection.IllegalAction);

        var hand = state.Hands[seat];
        var mask = 0;
        foreach (var id in ids)
        {
            var index = Array.IndexOf(hand, id);
            if (index < 0 || (mask & (1 << index)) != 0) throw new BigTwoRejectedException(BigTwoRejection.IllegalAction);
            mask |= 1 << index;
        }
        var played = hand.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
        if (!CanPlay(played, IsOpening(state), Target(state))) throw new BigTwoRejectedException(BigTwoRejection.IllegalAction);

        var hands = (int[][])state.Hands.Clone();
        hands[seat] = hand.Where((_, index) => (mask & (1 << index)) == 0).ToArray();
        var won = hands[seat].Length == 0;
        return new(hands, won ? null : next, won ? seat : null, new(seat, played));
    }

    private static BigTwoView Project(BigTwoState state, int viewer, bool playing)
    {
        if (!IsSeat(viewer)) throw new ArgumentOutOfRangeException(nameof(viewer));
        var turn = playing ? state.TurnSeat : null;
        var target = Target(state);
        var last = state.LastPlay is { } play && target is { } value
            ? new BigTwoPlay(play.Seat, play.Cards.Select(Card).ToArray(), value.Name) : null;
        var hand = state.Hands[viewer];
        var active = turn == viewer;
        return new(turn, state.WinnerSeat, state.Hands.Select((cards, seat) => new BigTwoSeat(seat, cards.Length)).ToArray(),
            hand.Select(Card).ToArray(), last, active ? LegalMasks(hand, IsOpening(state), target) : [], active && last is not null);
    }

    private static int[] LegalMasks(int[] hand, bool opening, BigTwoValue? target)
    {
        var masks = new List<int>();
        Span<int> cards = stackalloc int[5];
        for (var mask = 1; mask < (1 << hand.Length); mask++)
        {
            if (opening && (mask & 1) == 0) continue; // Three of diamonds is the first card in the sorted opening hand.
            var count = BitOperations.PopCount((uint)mask);
            if (target is { } prior ? count != prior.Count : count is not (1 or 2 or 3 or 5)) continue;
            var index = 0;
            for (var remaining = (uint)mask; remaining != 0; remaining &= remaining - 1)
                cards[index++] = hand[BitOperations.TrailingZeroCount(remaining)];
            if (CanPlay(cards[..count], opening, target)) masks.Add(mask);
        }
        return masks.ToArray();
    }

    private static bool CanPlay(ReadOnlySpan<int> cards, bool opening, BigTwoValue? target) =>
        (!opening || cards[0] == OpeningCard) && Evaluate(cards) is { } play && (target is null || play.Beats(target.Value));

    private static BigTwoValue? Target(BigTwoState state) => state.LastPlay is { } play ? Evaluate(play.Cards) : null;
    private static bool IsOpening(BigTwoState state) => state.Hands.All(hand => hand.Length == CardsPerHand);
    private static bool IsSeat(int seat) => seat is >= 0 and < SeatCount;
    private static string Save(BigTwoState state) => CheckpointCodec.Save(Game, state, CheckpointLimit);

    private static BigTwoState Load(string checkpoint)
    {
        var state = CheckpointCodec.Load<BigTwoState>(Game, checkpoint, CheckpointLimit);
        return IsValid(state) ? state : throw new InvalidDataException("Invalid Big Two state.");
    }

    private static bool IsValid(BigTwoState state)
    {
        var hands = state.Hands;
        if (hands is not { Length: SeatCount } || hands.Any(hand => !ValidCards(hand, CardsPerHand))) return false;
        if (state.LastPlay is { } play && (!IsSeat(play.Seat) || !ValidCards(play.Cards, 5)
            || hands[play.Seat].Length + play.Cards.Length > CardsPerHand || Target(state) is null))
            return false;
        var inHands = hands.SelectMany(hand => hand).ToArray();
        var recordedCards = inHands.Concat(state.LastPlay?.Cards ?? []).ToArray();
        if (recordedCards.Distinct().Count() != recordedCards.Length) return false;

        if (IsOpening(state))
            return state.LastPlay is null && state.WinnerSeat is null && state.TurnSeat is int first
                && IsSeat(first) && hands[first][0] == OpeningCard;
        if (inHands.Contains(OpeningCard)) return false;
        if (state.WinnerSeat is int winner)
            return IsSeat(winner) && state.TurnSeat is null && state.LastPlay?.Seat == winner
                && hands.Select((hand, seat) => (hand.Length == 0) == (seat == winner)).All(valid => valid);
        return state.TurnSeat is int turn && IsSeat(turn) && hands.All(hand => hand.Length != 0)
            && (state.LastPlay is { } lead ? lead.Seat != turn : hands[turn].Length < CardsPerHand);
    }

    private static bool ValidCards(int[]? cards, int max)
    {
        if (cards is null || cards.Length > max) return false;
        var prior = -1;
        foreach (var card in cards)
        {
            if (card is < 0 or >= DeckSize || Strength(card) <= prior) return false;
            prior = Strength(card);
        }
        return true;
    }
}
