using System.Collections.Immutable;
using System.Security.Cryptography;
using static JadeCourt.Games.Hanafuda.HanafudaCards;

namespace JadeCourt.Games.Hanafuda;

public sealed record KoiKoiPlay(int CardId, int[] TargetIds);
public sealed record KoiKoiLegalActions(KoiKoiPlay[] Plays, int[] DrawTargets, bool CanScore, bool CanContinue, bool CanNextRound);
public sealed record KoiKoiSeat(int Seat, int HandCount, KoiKoiCard[] Captured, int Score, KoiKoiYaku[] Yaku, bool Continued);
public sealed record KoiKoiResult(int? WinnerSeat, int Points, int BasePoints, bool SevenPointBonus, bool OpponentContinued,
    int NextDealerSeat);
public sealed record KoiKoiView(string Phase, int? TurnSeat, int DealerSeat, int Round, int TotalRounds, KoiKoiCard[] Field,
    int StockCount, KoiKoiCard? DrawnCard, KoiKoiCard[] OwnCards, KoiKoiSeat[] Seats, KoiKoiResult? Result,
    int? MatchWinnerSeat, KoiKoiLegalActions LegalActions);

public enum KoiKoiActionKind { Play = 1, TakeDraw, Score, Continue }
public readonly record struct KoiKoiAction(KoiKoiActionKind Kind, int? CardId = null, int? TargetId = null);
public enum KoiKoiRejection { IllegalAction, NotYourTurn, RoundFinished, RoundInProgress, Finished }
public sealed class KoiKoiRejectedException(KoiKoiRejection reason) : Exception(reason.ToString())
{
    public KoiKoiRejection Reason { get; } = reason;
}

internal enum KoiKoiPhase { Hand = 1, Draw, Decision, RoundEnd, Finished }
internal sealed record KoiKoiPlayer(ImmutableArray<int> Hand, ImmutableArray<int> Captured, int Score, int ContinuedAt);
internal sealed record KoiKoiState(int Round, int DealerSeat, int? TurnSeat, KoiKoiPhase Phase,
    ImmutableArray<KoiKoiPlayer> Players, ImmutableArray<int> Field, ImmutableArray<int> Stock, int? DrawnCard,
    KoiKoiResult? Result, ImmutableArray<ImmutableArray<int>> FutureDeals);

public static class KoiKoiGame
{
    public const int SeatCount = 2;
    public const int DeckSize = 48;
    public const int TotalRounds = 6;
    private const int OpeningCount = 8;
    private const int StockCount = 24;
    private const int MaxDealAttempts = 32;
    private const int MaxBotSteps = 3; // A play, a possible drawn-card choice, then a possible scoring decision.
    private const string Game = "koi-koi";
    private const int CheckpointLimit = 4_096;

    public static string Start(int? seed = null, CancellationToken stop = default)
    {
        var random = seed is int value ? new Random(value) : null;
        var dealer = random?.Next(SeatCount) ?? RandomNumberGenerator.GetInt32(SeatCount);
        // Keep the actual private deal orders so next-round transitions replay exactly without any new entropy.
        var deals = Enumerable.Range(0, TotalRounds).Select(_ => Shuffle(random, stop)).ToImmutableArray();
        return Save(Deal(1, dealer, 0, 0, deals));
    }

    public static KoiKoiView View(string checkpoint, int viewer, bool playing = true)
    {
        RequireSeat(viewer);
        return Project(Load(checkpoint), viewer, playing);
    }

    public static string Apply(string checkpoint, int seat, KoiKoiAction action)
    {
        RequireSeat(seat);
        return Save(Apply(Load(checkpoint), seat, action));
    }

    public static string NextRound(string checkpoint, int seat)
    {
        RequireSeat(seat);
        var state = Load(checkpoint);
        if (state.Phase == KoiKoiPhase.Finished) throw new KoiKoiRejectedException(KoiKoiRejection.Finished);
        if (state.Result is not { } result) throw new KoiKoiRejectedException(KoiKoiRejection.RoundInProgress);
        return Save(Deal(state.Round + 1, result.NextDealerSeat, state.Players[0].Score, state.Players[1].Score,
            state.FutureDeals));
    }

    public static string PlayBotTurn(string checkpoint, int botSeat, CancellationToken stop = default)
    {
        RequireSeat(botSeat);
        var state = Load(checkpoint);
        if (state.TurnSeat != botSeat) return checkpoint;
        for (var step = 0; step < MaxBotSteps && state.TurnSeat == botSeat; step++)
        {
            stop.ThrowIfCancellationRequested();
            state = Apply(state, botSeat, KoiKoiBot.Choose(Project(state, botSeat, true)));
        }
        if (state.TurnSeat == botSeat) throw new InvalidDataException("Koi-Koi bot did not finish its turn.");
        return Save(state);
    }

    private static ImmutableArray<int> Shuffle(Random? random, CancellationToken stop)
    {
        var deck = Enumerable.Range(0, DeckSize).ToArray();
        for (var attempt = 0; attempt < MaxDealAttempts; attempt++)
        {
            stop.ThrowIfCancellationRequested();
            if (random is null) RandomNumberGenerator.Shuffle(deck.AsSpan());
            else random.Shuffle(deck);
            if (!WholeMonth(deck.AsSpan(0, OpeningCount))) return [.. deck];
        }
        throw new InvalidDataException("Koi-Koi could not deal a field without a complete month.");
    }

    private static KoiKoiState Deal(int round, int dealer, int firstScore, int secondScore, ImmutableArray<ImmutableArray<int>> deals)
    {
        var deck = deals[0];
        return new(round, dealer, dealer, KoiKoiPhase.Hand,
            [new(deck[OpeningCount..(2 * OpeningCount)].Sort(), [], firstScore, 0),
             new(deck[(2 * OpeningCount)..(3 * OpeningCount)].Sort(), [], secondScore, 0)],
            deck[..OpeningCount], deck[(3 * OpeningCount)..], null, null, deals.RemoveAt(0));
    }

    private static KoiKoiState Apply(KoiKoiState state, int seat, KoiKoiAction action)
    {
        if (state.Phase == KoiKoiPhase.Finished) throw new KoiKoiRejectedException(KoiKoiRejection.Finished);
        if (state.Phase == KoiKoiPhase.RoundEnd) throw new KoiKoiRejectedException(KoiKoiRejection.RoundFinished);
        if (state.TurnSeat != seat) throw new KoiKoiRejectedException(KoiKoiRejection.NotYourTurn);

        var player = state.Players[seat];
        switch (state.Phase, action)
        {
            case (KoiKoiPhase.Hand, { Kind: KoiKoiActionKind.Play, CardId: int id }) when player.Hand.Contains(id):
                state = WithPlayer(state, seat, player with { Hand = player.Hand.Remove(id) });
                state = ResolveCard(state, seat, id, action.TargetId);
                var drawn = state.Stock[0];
                state = state with { Stock = state.Stock.RemoveAt(0) };
                return Matches(state, drawn).Length == 2
                    ? state with { Phase = KoiKoiPhase.Draw, DrawnCard = drawn }
                    : CompleteTurn(ResolveCard(state, seat, drawn, null), seat);
            case (KoiKoiPhase.Draw, { Kind: KoiKoiActionKind.TakeDraw, CardId: null, TargetId: int target }):
                return CompleteTurn(ResolveCard(state, seat, state.DrawnCard!.Value, target) with { DrawnCard = null }, seat);
            case (KoiKoiPhase.Decision, { Kind: KoiKoiActionKind.Score, CardId: null, TargetId: null }):
                return EndRound(state, seat);
            case (KoiKoiPhase.Decision, { Kind: KoiKoiActionKind.Continue, CardId: null, TargetId: null })
                when CanContinue(state, seat):
                return PassTurn(WithPlayer(state, seat, player with { ContinuedAt = Subtotal(player) }), seat);
            default:
                throw new KoiKoiRejectedException(KoiKoiRejection.IllegalAction);
        }
    }

    private static KoiKoiState ResolveCard(KoiKoiState state, int seat, int id, int? target)
    {
        var matches = Matches(state, id);
        if (matches.Length == 2)
        {
            if (target is not int choice || !matches.Contains(choice))
                throw new KoiKoiRejectedException(KoiKoiRejection.IllegalAction);
            matches = [choice];
        }
        else if (target.HasValue) throw new KoiKoiRejectedException(KoiKoiRejection.IllegalAction);

        if (matches.Length == 0) return state with { Field = state.Field.Add(id) };
        var player = state.Players[seat];
        return WithPlayer(state, seat, player with { Captured = [.. player.Captured, id, .. matches] })
            with { Field = state.Field.RemoveRange(matches) };
    }

    private static KoiKoiState CompleteTurn(KoiKoiState state, int seat)
    {
        var player = state.Players[seat];
        return Subtotal(player) > player.ContinuedAt
            ? state with { Phase = KoiKoiPhase.Decision }
            : PassTurn(state, seat);
    }

    private static KoiKoiState PassTurn(KoiKoiState state, int seat) =>
        state.Players[Other(seat)].Hand.IsEmpty
            ? EndRound(state, null)
            : state with { Phase = KoiKoiPhase.Hand, TurnSeat = Other(seat) };

    private static bool CanContinue(KoiKoiState state, int seat) =>
        !state.Players[seat].Hand.IsEmpty && state.Players.All(player => player.ContinuedAt == 0);

    private static KoiKoiState EndRound(KoiKoiState state, int? winner)
    {
        var result = Settlement(state, winner);
        if (winner is int seat)
        {
            var player = state.Players[seat];
            state = WithPlayer(state, seat, player with { Score = player.Score + result.Points });
        }
        return state with { TurnSeat = null, Phase = state.Round == TotalRounds ? KoiKoiPhase.Finished : KoiKoiPhase.RoundEnd,
            Result = result };
    }

    private static KoiKoiResult Settlement(KoiKoiState state, int? winner)
    {
        if (winner is not int seat) return new(null, 0, 0, false, false, Other(state.DealerSeat));
        var points = Subtotal(state.Players[seat]);
        var bonus = points >= 7;
        var opponentContinued = state.Players[Other(seat)].ContinuedAt > 0;
        return new(seat, points * (bonus ? 2 : 1) * (opponentContinued ? 2 : 1), points, bonus, opponentContinued, seat);
    }

    private static KoiKoiView Project(KoiKoiState state, int viewer, bool playing)
    {
        var turn = playing ? state.TurnSeat : null;
        var active = turn == viewer;
        var decision = active && state.Phase == KoiKoiPhase.Decision;
        var hand = state.Players[viewer].Hand;
        var drawTargets = active && state.DrawnCard is int id ? Matches(state, id) : [];
        var options = new KoiKoiLegalActions(
            active && state.Phase == KoiKoiPhase.Hand ? hand.Select(card => new KoiKoiPlay(card, Matches(state, card))).ToArray() : [],
            drawTargets, decision, decision && CanContinue(state, viewer), playing && state.Phase == KoiKoiPhase.RoundEnd);
        var seats = state.Players.Select((player, seat) =>
            new KoiKoiSeat(seat, player.Hand.Length, player.Captured.Order().Select(Card).ToArray(), player.Score,
                Yaku(player.Captured), player.ContinuedAt > 0)).ToArray();
        var scoreDifference = seats[0].Score.CompareTo(seats[1].Score);
        int? matchWinner = state.Phase == KoiKoiPhase.Finished && scoreDifference != 0 ? (scoreDifference > 0 ? 0 : 1) : null;
        var phase = state.Phase switch
        {
            KoiKoiPhase.Hand => "hand", KoiKoiPhase.Draw => "draw", KoiKoiPhase.Decision => "decision",
            KoiKoiPhase.RoundEnd => "roundEnd", KoiKoiPhase.Finished => "finished",
            _ => throw new InvalidDataException("Invalid Koi-Koi phase.")
        };
        return new(phase, turn, state.DealerSeat, state.Round, TotalRounds, state.Field.Select(Card).ToArray(), state.Stock.Length,
            state.DrawnCard is int drawn ? Card(drawn) : null, hand.Select(Card).ToArray(), seats, state.Result, matchWinner, options);
    }

    private static KoiKoiState WithPlayer(KoiKoiState state, int seat, KoiKoiPlayer player) =>
        state with { Players = state.Players.SetItem(seat, player) };

    private static int[] Matches(KoiKoiState state, int id) => state.Field.Where(card => Month(card) == Month(id)).ToArray();
    private static int Subtotal(KoiKoiPlayer player) => Yaku(player.Captured).Sum(yaku => yaku.Points);
    private static int Other(int seat) => 1 - seat;
    private static bool IsSeat(int seat) => seat is >= 0 and < SeatCount;
    private static bool IsCard(int card) => card is >= 0 and < DeckSize;

    private static void RequireSeat(int seat)
    {
        if (!IsSeat(seat)) throw new ArgumentOutOfRangeException(nameof(seat));
    }

    private static string Save(KoiKoiState state) => CheckpointCodec.Save(Game, state, CheckpointLimit);

    private static KoiKoiState Load(string checkpoint)
    {
        var state = CheckpointCodec.Load<KoiKoiState>(Game, checkpoint, CheckpointLimit);
        return IsValid(state) ? state : throw new InvalidDataException("Invalid Koi-Koi state.");
    }

    private static bool IsValid(KoiKoiState state)
    {
        if (state.Round is < 1 or > TotalRounds || !IsSeat(state.DealerSeat)
            || state.Players.IsDefault || state.Players.Length != SeatCount || state.Field.IsDefault || state.Stock.IsDefault
            || state.FutureDeals.IsDefault || state.FutureDeals.Length != TotalRounds - state.Round
            || state.Players.Any(p => p is null || p.Hand.IsDefault || p.Hand.Length > OpeningCount || p.Captured.IsDefault
                || p.Captured.Length % 2 != 0 || p.ContinuedAt < 0 || p.Score < 0)
            || state.Players.Count(p => p.ContinuedAt != 0) > 1)
            return false;

        var inPlay = state.Players.SelectMany(p => p.Hand.Concat(p.Captured)).Concat(state.Field).Concat(state.Stock);
        if (state.DrawnCard is int pending) inPlay = inPlay.Append(pending);
        if (!FullDeck(inPlay) || WholeMonth(state.Field.AsSpan())
            || state.FutureDeals.Any(deal => deal.IsDefault || !FullDeck(deal) || WholeMonth(deal.AsSpan()[..OpeningCount]))
            || state.Players.Any(p => p.ContinuedAt > Subtotal(p)))
            return false;

        var dealerHand = state.Players[state.DealerSeat].Hand.Length;
        var otherHand = state.Players[Other(state.DealerSeat)].Hand.Length;
        if (otherHand - dealerHand is < 0 or > 1 || state.Stock.Length != StockCount - 2 * OpeningCount + dealerHand + otherHand)
            return false;

        var next = dealerHand == otherHand ? state.DealerSeat : Other(state.DealerSeat);
        var last = Other(next);
        if (state.Result is { } result)
        {
            if (state.TurnSeat is not null || state.DrawnCard is not null
                || state.Phase != (state.Round < TotalRounds ? KoiKoiPhase.RoundEnd : KoiKoiPhase.Finished))
                return false;
            return (result.WinnerSeat is int winner
                    ? winner == last && result.BasePoints > state.Players[last].ContinuedAt && state.Players[last].Score >= result.Points
                    : dealerHand + otherHand == 0)
                && result == Settlement(state, result.WinnerSeat);
        }

        return state.Phase switch
        {
            KoiKoiPhase.Hand => state.DrawnCard is null && state.TurnSeat == next && !state.Players[next].Hand.IsEmpty,
            KoiKoiPhase.Draw => state.DrawnCard is int drawn && state.TurnSeat == last && Matches(state, drawn).Length == 2,
            KoiKoiPhase.Decision => state.DrawnCard is null && state.TurnSeat == last && Subtotal(state.Players[last]) > state.Players[last].ContinuedAt,
            _ => false
        };
    }

    private static bool FullDeck(IEnumerable<int> ids)
    {
        var count = 0;
        var seen = 0UL;
        foreach (var id in ids)
        {
            if (!IsCard(id) || (seen & (1UL << id)) != 0) return false;
            seen |= 1UL << id;
            count++;
        }
        return count == DeckSize;
    }

    private static bool WholeMonth(ReadOnlySpan<int> cards)
    {
        Span<int> monthCounts = stackalloc int[12];
        monthCounts.Clear();
        foreach (var card in cards)
            if (++monthCounts[Month(card) - 1] == 4) return true;
        return false;
    }
}
