using System.Collections.Immutable;
using System.Security.Cryptography;
using JadeCourt.Poker.Persistence;

namespace JadeCourt.Poker;

/// <summary>Authoritative table. Serialize all access to a table instance on the host.</summary>
public sealed class HoldemTable
{
    private sealed class Seat(int stack)
    {
        internal int Stack = stack;
        internal int StreetCommitted;
        internal int HandCommitted;
        internal int? ActedAt;
        internal bool Folded;
        internal ImmutableArray<HoldemCard> Cards = [];
        internal bool DealtIn => Cards.Length != 0;
        internal bool Contending => DealtIn && !Folded;
        internal bool CanAct => Contending && Stack > 0;
    }

    private readonly Seat[] _seats;
    private readonly int _smallBlind;
    private readonly int _bigBlind;
    private readonly List<HoldemCard> _board = new(5);
    private readonly List<HoldemUncalledReturn> _uncalledReturns = [];
    private readonly List<HoldemReplayAction> _actions = [];
    private int[] _startStacks = [];
    private HoldemCard[] _deck = [];
    private long _handNumber;
    private long _startRevision;
    private long _revision;
    private int _deckIndex;
    private int _button;
    private int _smallBlindSeat;
    private int _bigBlindSeat;
    private HoldemStreet _street;
    private int _price;
    private int _fullRaise;
    private int? _actor;
    private int? _matchWinner;
    private HoldemHandResult? _result;

    private HoldemTable(IReadOnlyList<int> stacks, int smallBlind, int bigBlind, int button,
        long handNumber, long startRevision, HoldemCard[] deck)
    {
        ArgumentNullException.ThrowIfNull(stacks);
        if (stacks.Count is < 2 or > 6)
            throw new ArgumentException("A table needs two to six fixed seats.", nameof(stacks));
        if (smallBlind <= 0 || bigBlind < smallBlind)
            throw new ArgumentException("Blinds must satisfy 0 < small blind <= big blind.");
        _seats = stacks.Select(stack => new Seat(stack)).ToArray();
        if (_seats.Any(s => s.Stack < 0) || _seats.Count(s => s.Stack > 0) < 2
            || _seats.Sum(s => (long)s.Stack) > int.MaxValue)
            throw new ArgumentException("At least two funded seats and a table total within Int32 are required.",
                nameof(stacks));
        if (button < 0 || button >= _seats.Length || _seats[button].Stack == 0)
            throw new ArgumentOutOfRangeException(nameof(button), "The button must start at a funded seat.");

        _smallBlind = smallBlind;
        _bigBlind = bigBlind;
        BeginHand(button, handNumber, startRevision, deck);
    }

    public static HoldemTable Create(IReadOnlyList<int> startingStacks, int smallBlind, int bigBlind,
        int firstButtonSeat = 0) => new(startingStacks, smallBlind, bigBlind, firstButtonSeat, 1, 1, ShuffledDeck());

    internal static HoldemTable CreateWithDeck(IReadOnlyList<int> startingStacks, int smallBlind, int bigBlind,
        int firstButtonSeat, IReadOnlyList<HoldemCard> deck)
        => new(startingStacks, smallBlind, bigBlind, firstButtonSeat, 1, 1, CopyDeck(deck));

    public HoldemView View(int? viewerSeat = null)
    {
        if (viewerSeat is int index && (index < 0 || index >= _seats.Length))
            throw new ArgumentOutOfRangeException(nameof(viewerSeat));

        return new(_revision, _handNumber, viewerSeat, _button, _smallBlindSeat, _bigBlindSeat,
            _smallBlind, _bigBlind, _street, _actor, _actor.HasValue ? EffectivePrice() : 0, PotChips,
            [.. _seats.Select((seat, i) => new HoldemSeatView(i, seat.Stack, seat.StreetCommitted,
                seat.HandCommitted, seat.DealtIn, seat.Folded, _result is null && seat.Contending && seat.Stack == 0))],
            [.. _board],
            viewerSeat is int viewer ? _seats[viewer].Cards : [],
            viewerSeat is int actingViewer && _actor == actingViewer ? LegalActionsFor(actingViewer) : null,
            [.. _uncalledReturns], _result, _matchWinner);
    }

    public void Act(int seat, long expectedRevision, HoldemAction action)
    {
        RequireRevision(expectedRevision);
        if (_actor != seat)
            throw new HoldemRejectedException(HoldemRejection.NotYourTurn);

        var legal = LegalActionsFor(seat);
        var valid = action.Kind switch
        {
            HoldemActionKind.Fold => legal.CanFold && action.To == 0,
            HoldemActionKind.Check => legal.CanCheck && action.To == 0,
            HoldemActionKind.Call => legal.CallAmount.HasValue && action.To == 0,
            HoldemActionKind.Bet => legal.Bet?.Contains(action.To) == true,
            HoldemActionKind.Raise => legal.Raise?.Contains(action.To) == true,
            _ => false
        };
        if (!valid)
            throw new HoldemRejectedException(HoldemRejection.IllegalAction);

        var revision = checked(_revision + 1);
        var player = _seats[seat];
        switch (action.Kind)
        {
            case HoldemActionKind.Fold:
                player.Folded = true;
                break;
            case HoldemActionKind.Call:
                Commit(player, legal.CallAmount!.Value);
                break;
            case HoldemActionKind.Bet or HoldemActionKind.Raise:
                Commit(player, action.To - player.StreetCommitted);
                var increase = action.To - _price;
                if (increase >= _fullRaise)
                    _fullRaise = increase;
                _price = action.To;
                break;
        }
        player.ActedAt = _price;
        _revision = revision;
        _actions.Add(new(seat, action.Kind, action.To));
        ContinueAfter(seat);
    }

    public void StartNextHand(long expectedRevision) => NextHand(expectedRevision, null);

    internal void StartNextHandWithDeck(long expectedRevision, IReadOnlyList<HoldemCard> deck)
        => NextHand(expectedRevision, deck);

    private void NextHand(long expectedRevision, IReadOnlyList<HoldemCard>? deck)
    {
        RequireRevision(expectedRevision);
        if (_result is null)
            throw new HoldemRejectedException(HoldemRejection.HandInProgress);
        if (_matchWinner.HasValue)
            throw new HoldemRejectedException(HoldemRejection.MatchComplete);

        // Heads-up, place the button opposite the next big blind rather than just advancing it.
        var after = _seats.Count(s => s.Stack > 0) == 2
            ? NextSeatAfter(_bigBlindSeat, s => s.Stack > 0)!.Value
            : _button;
        var button = NextSeatAfter(after, s => s.Stack > 0)!.Value;
        BeginHand(button, checked(_handNumber + 1), checked(_revision + 1),
            deck is null ? ShuffledDeck() : CopyDeck(deck));
    }

    private void BeginHand(int button, long handNumber, long startRevision, HoldemCard[] deck)
    {
        _startStacks = [.. _seats.Select(s => s.Stack)];
        _deck = deck;
        _deckIndex = 0;
        _handNumber = handNumber;
        _revision = _startRevision = startRevision;
        _button = button;
        _board.Clear();
        _uncalledReturns.Clear();
        _actions.Clear();
        _result = null;
        _matchWinner = null;
        _street = HoldemStreet.Preflop;
        _price = _fullRaise = _bigBlind;

        foreach (var seat in _seats)
        {
            seat.StreetCommitted = seat.HandCommitted = 0;
            seat.ActedAt = null;
            seat.Folded = false;
            seat.Cards = [];
        }

        var dealingOrder = ClockwiseSeats().Where(i => _seats[i].Stack > 0).ToArray();
        var firstCards = dealingOrder.Select(_ => Draw()).ToArray();
        for (var i = 0; i < dealingOrder.Length; i++)
            _seats[dealingOrder[i]].Cards = [firstCards[i], Draw()];

        _smallBlindSeat = dealingOrder.Length == 2 ? button : dealingOrder[0];
        _bigBlindSeat = dealingOrder.Length == 2 ? dealingOrder[0] : dealingOrder[1];
        Commit(_seats[_smallBlindSeat], Math.Min(_smallBlind, _seats[_smallBlindSeat].Stack));
        Commit(_seats[_bigBlindSeat], Math.Min(_bigBlind, _seats[_bigBlindSeat].Stack));
        ContinueAfter(_bigBlindSeat);
    }

    private HoldemLegalActions LegalActionsFor(int seat)
    {
        var player = _seats[seat];
        var price = EffectivePrice();
        var owes = price - player.StreetCommitted;
        HoldemWagerRange? range = null;
        var maxTo = player.StreetCommitted + player.Stack;
        // Reopening is measured from this player's last action, not the immediately preceding wager.
        if (maxTo > price && _seats.Count(s => s.CanAct) > 1
            && (player.ActedAt is null || price - player.ActedAt.Value >= _fullRaise))
        {
            var minimum = (long)price + _fullRaise;
            range = new((int)Math.Min(minimum, maxTo), maxTo);
        }
        return new(owes > 0, owes == 0, owes > 0 ? Math.Min(owes, player.Stack) : null,
            price == 0 ? range : null, price > 0 ? range : null);
    }

    private void ContinueAfter(int after)
    {
        while (true)
        {
            var contenders = _seats.Count(s => s.Contending);
            if (contenders == 1)
            {
                var winner = Array.FindIndex(_seats, s => s.Contending);
                ReturnUncalled();
                Settle(new(HoldemEndReason.EveryoneElseFolded,
                    [new(PotChips, [winner], [new(winner, PotChips)])], []));
                return;
            }

            var canAct = _seats.Count(s => s.CanAct);
            var price = canAct > 1 ? _price : HighestLiveCommitment;
            _actor = NextSeatAfter(after, s => s.CanAct
                && (s.StreetCommitted < price || (canAct > 1 && s.ActedAt is null)));
            if (_actor.HasValue)
                return;

            ReturnUncalled();
            if (_street == HoldemStreet.River)
            {
                SettleShowdown();
                return;
            }

            foreach (var seat in _seats)
            {
                seat.StreetCommitted = 0;
                seat.ActedAt = null;
            }
            _price = 0;
            _fullRaise = _bigBlind;
            _street++;
            _ = Draw(); // Burn before each community street.
            var cardsToDeal = _street == HoldemStreet.Flop ? 3 : 1;
            for (var i = 0; i < cardsToDeal; i++)
                _board.Add(Draw());
            after = _button;
        }
    }

    private void ReturnUncalled()
    {
        var ordered = _seats.OrderByDescending(s => s.StreetCommitted).ToArray();
        var high = ordered[0];
        var excess = high.StreetCommitted - ordered[1].StreetCommitted;
        if (excess == 0)
            return;

        high.Stack += excess;
        high.StreetCommitted -= excess;
        high.HandCommitted -= excess;
        _uncalledReturns.Add(new(_street, Array.IndexOf(_seats, high), excess));
    }

    private void SettleShowdown()
    {
        HoldemCard[] board = [.. _board];
        ImmutableArray<HoldemShownHand> shown = [.. ClockwiseSeats().Where(i => _seats[i].Contending)
            .Select(i => new HoldemShownHand(i, _seats[i].Cards,
                HoldemHandEvaluator.Evaluate([.. board, .. _seats[i].Cards])))];
        var pots = ImmutableArray.CreateBuilder<HoldemPot>();
        var floor = 0;
        // Only live caps create side pots. Folded chips between two caps are dead money in the same pot.
        foreach (var cap in shown.Select(h => _seats[h.Seat].HandCommitted).Distinct().Order())
        {
            var chips = _seats.Sum(s => Math.Max(0, Math.Min(s.HandCommitted, cap) - floor));
            if (chips == 0)
                continue;
            var eligible = shown.Where(h => _seats[h.Seat].HandCommitted >= cap).ToArray();
            var best = eligible.Max(h => h.Rank);
            var winners = eligible.Where(h => h.Rank == best).ToArray();
            var share = Math.DivRem(chips, winners.Length, out var oddChips);
            pots.Add(new(chips, [.. eligible.Select(h => h.Seat)],
                [.. winners.Select((h, i) => new HoldemAward(h.Seat, share + (i < oddChips ? 1 : 0)))]));
            floor = cap;
        }
        Settle(new(HoldemEndReason.Showdown, pots.ToImmutable(), shown));
    }

    private void Settle(HoldemHandResult result)
    {
        if (result.Pots.Sum(p => p.Awards.Sum(a => a.Chips)) != PotChips)
            throw new InvalidOperationException("Pot settlement does not account for every committed chip.");

        foreach (var pot in result.Pots)
        foreach (var award in pot.Awards)
            _seats[award.Seat].Stack += award.Chips;

        foreach (var seat in _seats)
            seat.HandCommitted = seat.StreetCommitted = 0;
        _actor = null;
        _result = result;
        if (_seats.Count(s => s.Stack > 0) == 1)
            _matchWinner = Array.FindIndex(_seats, s => s.Stack > 0);
    }

    private int PotChips => _seats.Sum(s => s.HandCommitted);
    private int HighestLiveCommitment => _seats.Where(s => s.Contending).Max(s => s.StreetCommitted);
    private int EffectivePrice() => _seats.Count(s => s.CanAct) > 1 ? _price : HighestLiveCommitment;

    private HoldemCard Draw() => _deck[_deckIndex++];

    private void RequireRevision(long expectedRevision)
    {
        if (expectedRevision != _revision)
            throw new HoldemRejectedException(HoldemRejection.StaleRevision);
    }

    private static void Commit(Seat seat, int chips)
    {
        seat.Stack -= chips;
        seat.StreetCommitted += chips;
        seat.HandCommitted += chips;
    }

    private IEnumerable<int> ClockwiseSeats()
    {
        for (var offset = 1; offset <= _seats.Length; offset++)
            yield return (_button + offset) % _seats.Length;
    }

    private int? NextSeatAfter(int after, Func<Seat, bool> condition)
    {
        for (var offset = 1; offset <= _seats.Length; offset++)
        {
            var seat = (after + offset) % _seats.Length;
            if (condition(_seats[seat]))
                return seat;
        }
        return null;
    }

    private static HoldemCard[] ShuffledDeck()
    {
        var deck = Enumerable.Range(0, 52).Select(HoldemCard.FromIndex).ToArray();
        for (var i = deck.Length - 1; i > 0; i--)
        {
            var swap = RandomNumberGenerator.GetInt32(i + 1);
            (deck[i], deck[swap]) = (deck[swap], deck[i]);
        }
        return deck;
    }

    private static HoldemCard[] CopyDeck(IReadOnlyList<HoldemCard> deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        if (deck.Count != 52)
            throw new ArgumentException("A deck must include 52 distinct cards.", nameof(deck));
        var copy = deck.ToArray();
        ulong seen = 0;
        foreach (var card in copy)
        {
            var bit = 1UL << card.Index;
            if ((seen & bit) != 0)
                throw new ArgumentException("A deck cannot contain duplicate cards.", nameof(deck));
            seen |= bit;
        }
        return copy;
    }

    internal HoldemReplay CaptureReplay() => new(_handNumber, _startRevision, _revision,
        _smallBlind, _bigBlind, _button, [.. _startStacks], [.. _deck.Select(c => (byte)c.Index)], [.. _actions]);

    internal static HoldemTable Replay(HoldemReplay replay)
    {
        if (replay.HandNumber < 1 || replay.StartRevision < replay.HandNumber
            || (replay.HandNumber == 1 && replay.StartRevision != 1)
            || replay.Revision < replay.StartRevision
            || replay.Revision - replay.StartRevision != replay.Actions.LongLength)
            throw new InvalidDataException("Invalid checkpoint revision or hand number.");

        var table = new HoldemTable(replay.Stacks, replay.SmallBlind, replay.BigBlind, replay.ButtonSeat,
            replay.HandNumber, replay.StartRevision, CopyDeck([.. replay.Deck.Select(c => HoldemCard.FromIndex(c))]));
        foreach (var step in replay.Actions)
        {
            if (step is null)
                throw new InvalidDataException("Missing action in Hold'em replay.");
            table.Act(step.Seat, table._revision, new(step.Kind, step.To));
        }
        return table;
    }
}
