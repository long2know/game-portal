using System.Collections.Immutable;

namespace JadeCourt.Poker;

public enum HoldemSuit { Clubs, Diamonds, Hearts, Spades }

public enum HoldemRank
{
    Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten, Jack, Queen, King, Ace
}

public readonly record struct HoldemCard(HoldemRank Rank, HoldemSuit Suit)
{
    internal int Index => Rank is >= HoldemRank.Two and <= HoldemRank.Ace
        && Suit is >= HoldemSuit.Clubs and <= HoldemSuit.Spades
            ? (int)Suit * 13 + (int)Rank - 2
            : throw new ArgumentException("Invalid poker card.");

    internal static HoldemCard FromIndex(int index) => index is >= 0 and < 52
        ? new((HoldemRank)(index % 13 + 2), (HoldemSuit)(index / 13))
        : throw new ArgumentException("Invalid poker card index.");
}

public enum HoldemStreet { Preflop, Flop, Turn, River }

public enum HoldemActionKind { Fold = 1, Check, Call, Bet, Raise }

/// <param name="To">The new total commitment on this street for a bet or raise; zero otherwise.</param>
/// <param name="Kind">The requested action.</param>
public readonly record struct HoldemAction(HoldemActionKind Kind, int To = 0);

public readonly record struct HoldemWagerRange(int MinTo, int MaxTo)
{
    public bool Contains(int to) => to >= MinTo && to <= MaxTo;
}

public sealed record HoldemLegalActions(
    bool CanFold, bool CanCheck, int? CallAmount, HoldemWagerRange? Bet, HoldemWagerRange? Raise);

public enum HoldemRejection { StaleRevision, NotYourTurn, IllegalAction, HandInProgress, MatchComplete }

public sealed class HoldemRejectedException : InvalidOperationException
{
    internal HoldemRejectedException(HoldemRejection reason) : base($"Hold'em command rejected: {reason}.")
        => Reason = reason;

    public HoldemRejection Reason { get; }
}

public sealed record HoldemSeatView(
    int Seat, int Stack, int StreetCommitted, int HandCommitted, bool DealtIn, bool Folded, bool IsAllIn);

public sealed record HoldemShownHand(int Seat, ImmutableArray<HoldemCard> Cards, HoldemHandRank Rank);

public readonly record struct HoldemAward(int Seat, int Chips);

public sealed record HoldemPot(int Chips, ImmutableArray<int> EligibleSeats, ImmutableArray<HoldemAward> Awards);

public readonly record struct HoldemUncalledReturn(HoldemStreet Street, int Seat, int Chips);

public enum HoldemEndReason { EveryoneElseFolded, Showdown }

public sealed record HoldemHandResult(
    HoldemEndReason Reason, ImmutableArray<HoldemPot> Pots, ImmutableArray<HoldemShownHand> ShowdownHands);

/// <summary>Public table information plus the authorized viewer's cards and actions, if any.</summary>
public sealed record HoldemView(
    long Revision,
    long HandNumber,
    int? ViewerSeat,
    int ButtonSeat,
    int SmallBlindSeat,
    int BigBlindSeat,
    int SmallBlind,
    int BigBlind,
    HoldemStreet Street,
    int? ActingSeat,
    int CurrentBet,
    int PotChips,
    ImmutableArray<HoldemSeatView> Seats,
    ImmutableArray<HoldemCard> Board,
    ImmutableArray<HoldemCard> OwnCards,
    HoldemLegalActions? LegalActions,
    ImmutableArray<HoldemUncalledReturn> UncalledReturns,
    HoldemHandResult? Result,
    int? MatchWinnerSeat);
