using System.Collections.Immutable;
using System.Security.Cryptography;
using static JadeCourt.Games.Harbors.HarborsBoard;

namespace JadeCourt.Games.Harbors;

public sealed record HarborsTile(int Id, int X, int Y, int? Resource, int? Number, int[] NodeIds);
public readonly record struct HarborsNode(int Id, int X, int Y, int? Seat, int Level);
public readonly record struct HarborsRoute(int Id, int A, int B, int? Seat);
public readonly record struct HarborsSeat(int Seat, int Points, int ResourceCount);
public readonly record struct HarborsTrade(int Give, int Take, int Cost);
public sealed record HarborsLegalActions(bool CanRoll, bool CanEndTurn, int[] Piers, int[] Routes, int[] Upgrades,
    HarborsTrade[] Trades);
public sealed record HarborsPosition(string Phase, int? TurnSeat, int Turn, int? WinnerSeat, int[]? LastRoll,
    HarborsTile[] Tiles, HarborsNode[] Nodes, HarborsRoute[] Routes, HarborsSeat[] Seats, int[] OwnResources,
    HarborsLegalActions LegalActions);

public enum HarborsActionKind { BuildPier = 1, BuildRoute, Upgrade, Trade, Roll, EndTurn }
public readonly record struct HarborsAction(HarborsActionKind Kind, int? Target = null, int? Give = null, int? Take = null);
public enum HarborsRejection { IllegalAction, NotYourTurn, Finished }
public sealed class HarborsRejectedException(HarborsRejection reason) : Exception(reason.ToString())
{
    public HarborsRejection Reason { get; } = reason;
}

internal enum HarborsPhase { PlacePier = 1, PlaceRoute, Roll, Build, Finished }

// Sites store 0 for empty, 1–3 for piers and 4–6 for warehouses; routes store 0 or the one-based seat.
internal sealed record HarborsState(HarborsPhase Phase, int Turn, bool Traded, ImmutableArray<int> OpeningPiers,
    ImmutableArray<int> OpeningRoutes, ImmutableArray<int> Sites, ImmutableArray<int> Routes, ImmutableArray<int> Resources,
    ImmutableArray<int> DiceTape);

public static class HarborsGame
{
    public const int SeatCount = 3;
    internal const int ResourceKinds = 4;
    internal const int ResourceLimit = 12;
    internal const int MaxTurns = 120;
    internal const int TargetPoints = 8;
    internal static ReadOnlySpan<int> PierCost => [1, 1, 1, 0];
    internal static ReadOnlySpan<int> RouteCost => [1, 1, 0, 0];
    internal static ReadOnlySpan<int> UpgradeCost => [0, 0, 1, 2];

    private static ReadOnlySpan<int> SetupOrder => [0, 1, 2, 2, 1, 0];
    // At most two bot turns: 48 resources / 2 per build, plus roll, trade and end. Include every setup action.
    private const int MaxBotSteps = 2 * 2 * SeatCount + (SeatCount - 1) * (ResourceKinds * ResourceLimit / 2 + 3);
    private const string Game = "jade-harbors";
    private const int CheckpointLimit = 4_096;

    public static string Start(int? seed = null, CancellationToken stop = default)
    {
        var random = seed is int value ? new Random(value) : null;
        var dice = ImmutableArray.CreateBuilder<int>(2 * MaxTurns);
        for (var turn = 0; turn < MaxTurns; turn++)
        {
            stop.ThrowIfCancellationRequested();
            dice.Add(random?.Next(1, 7) ?? RandomNumberGenerator.GetInt32(1, 7));
            dice.Add(random?.Next(1, 7) ?? RandomNumberGenerator.GetInt32(1, 7));
        }
        return Save(new(HarborsPhase.PlacePier, 0, false, [], [], [.. new int[Sites.Length]],
            [.. new int[Routes.Length]], [.. new int[SeatCount * ResourceKinds]], dice.MoveToImmutable()));
    }

    public static HarborsPosition View(string checkpoint, int viewer, bool playing = true)
    {
        RequireSeat(viewer);
        return Project(Load(checkpoint), viewer, playing);
    }

    public static string Apply(string checkpoint, int seat, HarborsAction action)
    {
        RequireSeat(seat);
        return Save(Apply(Load(checkpoint), seat, action));
    }

    public static string PlayBotsUntil(string checkpoint, int humanSeat, CancellationToken stop = default)
    {
        RequireSeat(humanSeat);
        var state = Load(checkpoint);
        for (var step = 0; TurnSeat(state) is int seat && seat != humanSeat; step++)
        {
            stop.ThrowIfCancellationRequested();
            if (step >= MaxBotSteps) throw new InvalidDataException("Jade Harbors bots did not return play to the human.");
            var view = Project(state, seat, true);
            state = Apply(state, seat, HarborsBot.Choose(view));
        }
        return Save(state);
    }

    private static HarborsState Apply(HarborsState state, int seat, HarborsAction action)
    {
        if (state.Phase == HarborsPhase.Finished) throw new HarborsRejectedException(HarborsRejection.Finished);
        if (TurnSeat(state) != seat) throw new HarborsRejectedException(HarborsRejection.NotYourTurn);

        var legal = LegalActions(state, seat);
        switch (action)
        {
            case { Kind: HarborsActionKind.BuildPier, Target: int node, Give: null, Take: null } when legal.Piers.Contains(node):
                return BuildPier(state, seat, node);
            case { Kind: HarborsActionKind.BuildRoute, Target: int edge, Give: null, Take: null } when legal.Routes.Contains(edge):
                return BuildRoute(state, seat, edge);
            case { Kind: HarborsActionKind.Upgrade, Target: int node, Give: null, Take: null } when legal.Upgrades.Contains(node):
                return Score(Spend(state, seat, UpgradeCost) with { Sites = state.Sites.SetItem(node, seat + 1 + SeatCount) }, seat);
            case { Kind: HarborsActionKind.Trade, Target: null, Give: int give, Take: int take }
                when legal.Trades.Contains(new(give, take, TradeCost(state))):
                var supplies = state.Resources.ToBuilder();
                supplies[ResourceIndex(seat, give)] -= TradeCost(state);
                supplies[ResourceIndex(seat, take)]++;
                return state with { Resources = supplies.MoveToImmutable(), Traded = true };
            case { Kind: HarborsActionKind.Roll, Target: null, Give: null, Take: null } when legal.CanRoll:
                return Roll(state);
            case { Kind: HarborsActionKind.EndTurn, Target: null, Give: null, Take: null } when legal.CanEndTurn:
                return state.Turn == MaxTurns
                    ? Finish(state)
                    : state with { Phase = HarborsPhase.Roll, Turn = state.Turn + 1, Traded = false };
            default:
                throw new HarborsRejectedException(HarborsRejection.IllegalAction);
        }
    }

    private static HarborsState BuildPier(HarborsState state, int seat, int node)
    {
        state = state with { Sites = state.Sites.SetItem(node, seat + 1) };
        if (state.Phase == HarborsPhase.Build) return Score(Spend(state, seat, PierCost), seat);

        var openingPiers = state.OpeningPiers.Add(node);
        if (openingPiers.Length > SeatCount)
        {
            var supplies = state.Resources.ToBuilder();
            CollectOpening(supplies, seat, node);
            state = state with { Resources = supplies.MoveToImmutable() };
        }
        return state with { Phase = HarborsPhase.PlaceRoute, OpeningPiers = openingPiers };
    }

    private static HarborsState BuildRoute(HarborsState state, int seat, int edge)
    {
        state = state with { Routes = state.Routes.SetItem(edge, seat + 1) };
        if (state.Phase == HarborsPhase.Build) return Spend(state, seat, RouteCost);

        var openingRoutes = state.OpeningRoutes.Add(edge);
        return openingRoutes.Length == SetupOrder.Length
            ? state with { Phase = HarborsPhase.Roll, OpeningRoutes = openingRoutes, Turn = 1 }
            : state with { Phase = HarborsPhase.PlacePier, OpeningRoutes = openingRoutes };
    }

    private static HarborsState Roll(HarborsState state)
    {
        var total = RollTotal(state);
        if (total == 7) return state with { Phase = HarborsPhase.Build };

        var supplies = state.Resources.ToBuilder();
        foreach (var tile in Tiles)
        {
            if (tile.Number != total || tile.Resource is not int kind) continue;
            foreach (var node in tile.NodeIds)
            {
                var piece = state.Sites[node];
                if (Owner(piece) is not int seat) continue;
                var index = ResourceIndex(seat, kind);
                supplies[index] = Math.Min(ResourceLimit, supplies[index] + Level(piece));
            }
        }
        return state with { Phase = HarborsPhase.Build, Resources = supplies.MoveToImmutable() };
    }

    private static HarborsPosition Project(HarborsState state, int viewer, bool playing)
    {
        var turnSeat = playing ? TurnSeat(state) : null;
        var lastRolledTurn = state.Phase == HarborsPhase.Roll ? state.Turn - 1 : state.Turn;
        var lastRoll = lastRolledTurn == 0 ? null : state.DiceTape.AsSpan(2 * (lastRolledTurn - 1), 2).ToArray();
        var seats = new HarborsSeat[SeatCount];
        for (var seat = 0; seat < SeatCount; seat++)
        {
            var count = 0;
            foreach (var held in Hand(state, seat)) count += held;
            seats[seat] = new(seat, Points(state, seat), count);
        }
        int? winner = null;
        if (state.Phase == HarborsPhase.Finished)
        {
            var high = seats.Max(seat => seat.Points);
            if (seats.Count(seat => seat.Points == high) == 1) winner = Array.FindIndex(seats, seat => seat.Points == high);
        }
        return new(state.Phase switch
            {
                HarborsPhase.PlacePier => "placePier", HarborsPhase.PlaceRoute => "placeRoute", HarborsPhase.Roll => "roll",
                HarborsPhase.Build => "build", _ => "finished"
            }, turnSeat, state.Turn, winner, lastRoll,
            [.. Tiles.Select(tile => tile with { NodeIds = (int[])tile.NodeIds.Clone() })],
            [.. Sites.Select((site, id) => new HarborsNode(id, site.X, site.Y, Owner(state.Sites[id]), Level(state.Sites[id])))],
            [.. Routes.Select((link, id) => new HarborsRoute(id, link.A, link.B, state.Routes[id] == 0 ? null : state.Routes[id] - 1))],
            seats, Hand(state, viewer).ToArray(), turnSeat == viewer ? LegalActions(state, viewer) : NoActions);
    }

    private static readonly HarborsLegalActions NoActions = new(false, false, [], [], [], []);

    private static HarborsLegalActions LegalActions(HarborsState state, int seat)
    {
        if (state.Phase == HarborsPhase.PlacePier)
            return NoActions with { Piers = [.. Enumerable.Range(0, Sites.Length).Where(node => EmptyWithSpacing(state, node))] };
        if (state.Phase == HarborsPhase.PlaceRoute)
            return NoActions with { Routes = [.. RoutesAtSite[state.OpeningPiers[^1]].Where(edge => state.Routes[edge] == 0)] };
        if (state.Phase == HarborsPhase.Roll) return NoActions with { CanRoll = true };
        if (state.Phase != HarborsPhase.Build) return NoActions;

        var hand = Hand(state, seat);
        int[] piers = Affordable(hand, PierCost)
            ? [.. Enumerable.Range(0, Sites.Length).Where(node => EmptyWithSpacing(state, node) && OwnsRouteAt(state, seat, node))]
            : [];
        int[] routes = Affordable(hand, RouteCost)
            ? [.. Enumerable.Range(0, Routes.Length).Where(edge => state.Routes[edge] == 0
                && (CanExtendAt(state, seat, Routes[edge].A) || CanExtendAt(state, seat, Routes[edge].B)))]
            : [];
        int[] upgrades = Affordable(hand, UpgradeCost)
            ? [.. Enumerable.Range(0, Sites.Length).Where(node => state.Sites[node] == seat + 1)]
            : [];
        var trades = new List<HarborsTrade>();
        if (!state.Traded)
        {
            var cost = TradeCost(state);
            for (var give = 0; give < ResourceKinds; give++)
            {
                if (hand[give] < cost) continue;
                for (var take = 0; take < ResourceKinds; take++)
                    if (give != take && hand[take] < ResourceLimit) trades.Add(new(give, take, cost));
            }
        }
        return new(false, true, piers, routes, upgrades, [.. trades]);
    }

    private static bool EmptyWithSpacing(HarborsState state, int node) =>
        state.Sites[node] == 0 && RoutesAtSite[node].All(edge => state.Sites[Routes[edge].Other(node)] == 0);

    private static bool OwnsRouteAt(HarborsState state, int seat, int node) =>
        RoutesAtSite[node].Any(edge => state.Routes[edge] == seat + 1);

    private static bool CanExtendAt(HarborsState state, int seat, int node) =>
        Owner(state.Sites[node]) is int occupied ? occupied == seat : OwnsRouteAt(state, seat, node);

    private static bool Affordable(ReadOnlySpan<int> hand, ReadOnlySpan<int> cost)
    {
        for (var kind = 0; kind < ResourceKinds; kind++)
            if (hand[kind] < cost[kind]) return false;
        return true;
    }

    private static HarborsState Spend(HarborsState state, int seat, ReadOnlySpan<int> cost)
    {
        var supplies = state.Resources.ToBuilder();
        for (var kind = 0; kind < ResourceKinds; kind++) supplies[ResourceIndex(seat, kind)] -= cost[kind];
        return state with { Resources = supplies.MoveToImmutable() };
    }

    private static void CollectOpening(ImmutableArray<int>.Builder supplies, int seat, int node)
    {
        foreach (var tile in Tiles)
            if (tile.Resource is int kind && tile.NodeIds.Contains(node)) supplies[ResourceIndex(seat, kind)]++;
    }

    private static int Level(int piece) => piece == 0 ? 0 : piece <= SeatCount ? 1 : 2;
    private static int? Owner(int piece) => piece == 0 ? null : (piece - 1) % SeatCount;
    private static int Points(HarborsState state, int seat) => state.Sites.Where(piece => Owner(piece) == seat).Sum(Level);
    private static int ResourceIndex(int seat, int kind) => seat * ResourceKinds + kind;
    private static ReadOnlySpan<int> Hand(HarborsState state, int seat) => state.Resources.AsSpan(ResourceIndex(seat, 0), ResourceKinds);
    private static int RollTotal(HarborsState state) => state.DiceTape[2 * (state.Turn - 1)] + state.DiceTape[2 * state.Turn - 1];
    private static int TradeCost(HarborsState state) => state.Phase == HarborsPhase.Build && RollTotal(state) == 7 ? 2 : 3;
    private static HarborsState Score(HarborsState state, int seat) => Points(state, seat) == TargetPoints ? Finish(state) : state;
    private static HarborsState Finish(HarborsState state) => state with { Phase = HarborsPhase.Finished, Traded = false };

    private static int? TurnSeat(HarborsState state) => state.Phase switch
    {
        HarborsPhase.PlacePier or HarborsPhase.PlaceRoute => SetupOrder[state.OpeningRoutes.Length],
        HarborsPhase.Finished => null,
        _ => RegularSeat(state)
    };

    private static int RegularSeat(HarborsState state) => (state.Turn - 1) % SeatCount;

    private static void RequireSeat(int seat)
    {
        if (seat is < 0 or >= SeatCount) throw new ArgumentOutOfRangeException(nameof(seat));
    }

    private static string Save(HarborsState state) => CheckpointCodec.Save(Game, state, CheckpointLimit);

    private static HarborsState Load(string checkpoint)
    {
        var state = CheckpointCodec.Load<HarborsState>(Game, checkpoint, CheckpointLimit);
        return Valid(state) ? state : throw new InvalidDataException("Invalid Jade Harbors state.");
    }

    private static bool Valid(HarborsState state)
    {
        if (state.Phase is < HarborsPhase.PlacePier or > HarborsPhase.Finished
            || state.Sites.IsDefault || state.Sites.Length != Sites.Length || state.Sites.Any(piece => piece is < 0 or > 2 * SeatCount)
            || state.Routes.IsDefault || state.Routes.Length != Routes.Length || state.Routes.Any(seat => seat is < 0 or > SeatCount)
            || state.Resources.IsDefault || state.Resources.Length != SeatCount * ResourceKinds
            || state.Resources.Any(held => held is < 0 or > ResourceLimit)
            || state.DiceTape.IsDefault || state.DiceTape.Length != 2 * MaxTurns || state.DiceTape.Any(face => face is < 1 or > 6)
            || state.Traded && state.Phase != HarborsPhase.Build)
            return false;

        var setup = state.Phase is HarborsPhase.PlacePier or HarborsPhase.PlaceRoute;
        if (state.OpeningPiers.IsDefault || state.OpeningRoutes.IsDefault || state.OpeningPiers.Length > SetupOrder.Length
            || state.OpeningPiers.Length != state.OpeningRoutes.Length + (state.Phase == HarborsPhase.PlaceRoute ? 1 : 0)
            || setup && (state.Turn != 0 || state.OpeningRoutes.Length >= SetupOrder.Length)
            || !setup && (state.Turn is < 1 or > MaxTurns || state.OpeningRoutes.Length != SetupOrder.Length)
            || state.OpeningPiers.Distinct().Count() != state.OpeningPiers.Length
            || state.OpeningRoutes.Distinct().Count() != state.OpeningRoutes.Length)
            return false;

        for (var i = 0; i < state.OpeningPiers.Length; i++)
        {
            var node = state.OpeningPiers[i];
            if (node < 0 || node >= Sites.Length || Owner(state.Sites[node]) != SetupOrder[i]) return false;
            if (i < state.OpeningRoutes.Length)
            {
                var edge = state.OpeningRoutes[i];
                if (!RoutesAtSite[node].Contains(edge) || state.Routes[edge] != SetupOrder[i] + 1) return false;
            }
        }
        if (Routes.Any(link => state.Sites[link.A] != 0 && state.Sites[link.B] != 0)) return false;

        if (setup)
        {
            if (state.Sites.Count(piece => piece != 0) != state.OpeningPiers.Length || state.Sites.Any(piece => Level(piece) == 2)
                || state.Routes.Count(seat => seat != 0) != state.OpeningRoutes.Length)
                return false;
            var supplies = ImmutableArray.CreateBuilder<int>(SeatCount * ResourceKinds);
            supplies.Count = SeatCount * ResourceKinds;
            for (var i = SeatCount; i < state.OpeningPiers.Length; i++) CollectOpening(supplies, SetupOrder[i], state.OpeningPiers[i]);
            return state.Resources.SequenceEqual(supplies);
        }

        var reachedTarget = false;
        for (var seat = 0; seat < SeatCount; seat++)
        {
            var points = Points(state, seat);
            if (points > TargetPoints) return false;
            if (points == TargetPoints)
            {
                if (state.Phase != HarborsPhase.Finished || seat != RegularSeat(state)) return false;
                reachedTarget = true;
            }
        }
        return (state.Phase != HarborsPhase.Finished || reachedTarget || state.Turn == MaxTurns) && ValidNetworks(state);
    }

    private static bool ValidNetworks(HarborsState state)
    {
        Span<bool> visited = stackalloc bool[Sites.Length];
        Span<int> queue = stackalloc int[Sites.Length];
        for (var seat = 0; seat < SeatCount; seat++)
        {
            visited.Clear();
            queue[0] = state.OpeningPiers[seat];
            queue[1] = state.OpeningPiers[SetupOrder.Length - 1 - seat];
            visited[queue[0]] = visited[queue[1]] = true;
            var tail = 2;
            // A rival pier can cut through traffic, but cannot undo roads that were already built.
            for (var head = 0; head < tail; head++)
            {
                var node = queue[head];
                foreach (var edge in RoutesAtSite[node])
                {
                    if (state.Routes[edge] != seat + 1) continue;
                    var end = Routes[edge].Other(node);
                    if (visited[end]) continue;
                    visited[end] = true;
                    queue[tail++] = end;
                }
            }
            for (var edge = 0; edge < Routes.Length; edge++)
                if (state.Routes[edge] == seat + 1 && !visited[Routes[edge].A]) return false;
            for (var node = 0; node < Sites.Length; node++)
                if (Owner(state.Sites[node]) == seat && !visited[node]) return false;
        }
        return true;
    }
}
