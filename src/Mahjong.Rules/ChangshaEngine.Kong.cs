using System.Collections.Immutable;

namespace Mahjong.Rules;

public static partial class ChangshaEngine
{
    private static GameState BeginKongReplacement(GameState state, int seat, DateTimeOffset at)
    {
        var (first, second) = Determinism.RollDice(state.Start, state.WindowNumber);
        var stacks = state.Wall.Reverse().GroupBy(entry => entry.Slot / 2).ToArray();
        var count = first + second;
        if (count > stacks.Length)
            count = Math.Min(first, second);
        if (count > stacks.Length)
            count = 1;

        var target = stacks[count - 1];
        var candidates = target.Select(entry => entry.Tile).ToImmutableArray();
        var wall = state.Wall.Where(entry => entry.Slot / 2 != target.Key).ToImmutableArray();
        var holder = state.Seats[seat] with { PassedWinFaces = [], FrozenAfterGang = true };
        state = ReplaceSeat(state with
        {
            Wall = wall,
            LastTileSeat = wall.IsEmpty ? seat : state.LastTileSeat
        }, seat, holder);
        var options = candidates.Where(tile => Winning.IsWinning(holder, tile.FaceIndex))
            .Select(tile => Option("hu", [tile], [tile]))
            .Append(Option("discardKong", candidates, candidates)).ToImmutableArray();
        return OpenWindow(state, DecisionKind.Turn, seat, at, [new(seat, options, null)],
            drawKind: DrawKind.RolledSupplement, kongTiles: candidates, kongStackCount: count);
    }

    private static GameState OnKongTurn(GameState state, DecisionWindow window, DecisionOption option, DateTimeOffset at)
    {
        var seat = window.Seat;
        var holder = state.Seats[seat];
        var candidates = window.KongTiles!.Value;
        if (option.Kind == "hu")
        {
            var tile = option.OwnTiles[0];
            var unused = candidates.Length == 2 ? candidates.Single(candidate => candidate != tile) : (PhysicalTile?)null;
            state = ReplaceSeat(state with { UnchosenKongTile = unused }, seat,
                holder with { Hand = Tiles.Sort(holder.Hand.Add(tile)), Drawn = tile });
            return Scoring.Settle(state, [seat], tile, null, WinContext.RolledSupplement);
        }

        var thrown = holder with { Discards = holder.Discards.AddRange(candidates) };
        state = ReplaceSeat(state with { DiscardCount = state.DiscardCount + 1 }, seat, thrown);
        var decisions = KongDiscardDecisions(state, seat, candidates);
        return decisions.IsEmpty ? NextDraw(state, Next(seat), at) :
            OpenWindow(state, DecisionKind.KongDiscard, seat, at, decisions, kongTiles: candidates);
    }

    private static ImmutableArray<SeatDecision> KongDiscardDecisions(GameState state, int source,
        ImmutableArray<PhysicalTile> candidates)
    {
        var decisions = ImmutableArray.CreateBuilder<SeatDecision>();
        foreach (var seat in InTurnOrder(Next(source)).Take(3))
        {
            var hu = candidates.Where(tile => CanClaimHu(state.Seats[seat], tile.FaceIndex))
                .Select(tile => Option("hu", preview: [tile])).ToImmutableArray();
            if (!hu.IsEmpty)
                decisions.Add(new(seat, hu.Add(Option("pass")), null));
        }
        return decisions.ToImmutable();
    }

    private static GameState ResolveKongDiscard(GameState state, DecisionWindow window, DateTimeOffset at)
    {
        var claims = ImmutableArray.CreateBuilder<WinClaim>();
        var source = window.Seat;
        foreach (var decision in window.Decisions.OrderBy(entry => Distance(source, entry.Seat)))
        {
            var response = decision.Options.Single(option => option.Id == decision.Response);
            if (response.Kind == "hu")
            {
                claims.Add(new(decision.Seat, response.Preview[0]));
                continue;
            }
            var holder = state.Seats[decision.Seat];
            var missed = decision.Options.Where(option => option.Kind == "hu")
                .Select(option => option.Preview[0].FaceIndex).Distinct();
            state = ReplaceSeat(state, decision.Seat, holder with { PassedWinFaces = holder.PassedWinFaces.AddRange(missed) });
        }
        if (claims.Count == 0)
            return NextDraw(state, Next(source), at);

        var caught = window.KongTiles!.Value.Where(tile => claims.Any(claim => claim.Tile == tile)).ToImmutableArray();
        var thrower = state.Seats[source];
        state = ReplaceSeat(state with { ClaimedKongWinTiles = caught }, source,
            thrower with { Discards = thrower.Discards.RemoveRange(caught) });
        return Scoring.Settle(state, claims.ToImmutable(), source, WinContext.RolledSupplement);
    }
}
