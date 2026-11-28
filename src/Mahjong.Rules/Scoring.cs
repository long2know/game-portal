using System.Collections.Immutable;

namespace Mahjong.Rules;

internal enum WinContext
{
    Ordinary,
    Initial,
    Supplement,
    RolledSupplement,
    LastTile,
    RobGang,
    RobBu
}

internal readonly record struct WinClaim(int Seat, PhysicalTile Tile);

internal static class Scoring
{
    internal static GameState Settle(GameState state, ImmutableArray<int> winningSeats,
        PhysicalTile winningTile, int? source, WinContext context) =>
        Settle(state, [.. winningSeats.Select(seat => new WinClaim(seat, winningTile))], source, context);

    internal static GameState Settle(GameState state, ImmutableArray<WinClaim> claims, int? source, WinContext context)
    {
        var winners = claims.Select(claim => Classify(state, claim.Seat, claim.Tile, source, context)).ToImmutableArray();
        var revealCount = state.Start.Profile is ChangshaEngine.TwoBirdProfile or ChangshaEngine.TwoTileKongProfile ? 2 : 1;
        ImmutableArray<PhysicalTile> birds = context == WinContext.LastTile ? [claims[0].Tile] :
            [.. state.Wall.Take(revealCount).Select(entry => entry.Tile)];
        if (context != WinContext.LastTile && !birds.IsEmpty)
            state = state with { Wall = state.Wall.RemoveRange(0, birds.Length), Birds = birds };
        var birdSeats = birds.Select(tile => (state.Start.DealerSeat + Tiles.Rank(tile.FaceIndex) - 1) % 4)
            .ToImmutableArray();

        var transfers = OpeningTransfers(state);
        foreach (var winner in winners)
        {
            foreach (var payer in source is { } from ? [from] : Enumerable.Range(0, 4))
            {
                if (payer == winner.Seat)
                    continue;
                var dealer = state.Start.DealerSeat == winner.Seat || state.Start.DealerSeat == payer;
                var hits = birdSeats.Count(seat => seat == winner.Seat || seat == payer);
                var basePayment = winner.Patterns.Sum(pattern => pattern.Points + (dealer ? 1 : 0));
                transfers.Add(new(payer, winner.Seat, checked(basePayment * (1 + hits)), "win"));
            }
        }
        var nextDealer = winners.Length == 1 ? winners[0].Seat : source!.Value;
        return Finish(state, "win", nextDealer, source, winners, [.. birds.Select(Tiles.Face)], transfers);
    }

    internal static GameState SettleDraw(GameState state) =>
        Finish(state, "draw", state.LastTileSeat ?? state.Start.DealerSeat, null, [], [], OpeningTransfers(state));

    private static Winner Classify(GameState state, int seat, PhysicalTile tile, int? source, WinContext context)
    {
        var codes = Winning.Classify(state.Seats[seat], source.HasValue ? tile.FaceIndex : null, out var generalShape);
        var self = !source.HasValue;
        var extra = context switch
        {
            WinContext.Initial when self && generalShape => "heavenly",
            WinContext.LastTile => self ? "lastTileDraw" : "lastTileDiscard",
            WinContext.RolledSupplement => self ? "kongBloom" : "kongCannon",
            WinContext.RobGang => "robKong",
            _ => null
        };
        var earthly = source == state.Start.DealerSeat && state.DiscardCount == 1 &&
            context is not (WinContext.RobGang or WinContext.RobBu);
        if ((earthly || extra is not null) && codes.SequenceEqual(["plain"]))
            codes = [];
        if (earthly)
            codes = codes.Add("earthly");
        if (extra is not null)
            codes = codes.Add(extra);
        var how = context is WinContext.RobGang or WinContext.RobBu ? "robKong" : self ? "selfDraw" : "discard";
        return new(seat, how, [Tiles.Face(tile)], codes.Select(code => Pattern(code, self)).ToImmutableArray());
    }

    private static WinPattern Pattern(string code, bool self)
    {
        var label = code switch
        {
            "plain" => "Ordinary win (平胡)",
            "allPungs" => "All pungs (碰碰胡)",
            "allGenerals" => "All generals (将将胡)",
            "pureSuit" => "Pure suit (清一色)",
            "fullyMelded" => "Fully melded (全求人)",
            "sevenPairs" => "Seven pairs (七小对)",
            "luxurySevenPairs" => "Luxury seven pairs (豪华七小对)",
            "heavenly" => "Heavenly hand (天胡)",
            "earthly" => "Earthly hand (地胡)",
            "lastTileDraw" => "Seabed self-draw (海底捞月)",
            "lastTileDiscard" => "Seabed discard (海底炮)",
            "kongBloom" => "Win on rolled replacement (杠上开花)",
            "kongCannon" => "Win off rolled replacement (杠上炮)",
            "robKong" => "Robbing a kong (抢杠胡)",
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
        return new(code, label, code == "plain" ? 1 : self ? 3 : 6);
    }

    private static ImmutableArray<PayerTransfer>.Builder OpeningTransfers(GameState state)
    {
        var transfers = ImmutableArray.CreateBuilder<PayerTransfer>();
        foreach (var to in Enumerable.Range(0, 4))
        {
            var awards = state.Seats[to].OpeningAwards.Length;
            if (awards == 0)
                continue;
            foreach (var from in Enumerable.Range(0, 4))
            {
                if (from != to)
                    transfers.Add(new(from, to, awards * (state.Start.DealerSeat == to || state.Start.DealerSeat == from ? 2 : 1),
                        "opening"));
            }
        }
        return transfers;
    }

    private static GameState Finish(GameState state, string reason, int nextDealer, int? source,
        ImmutableArray<Winner> winners, ImmutableArray<string> birds, ImmutableArray<PayerTransfer>.Builder transfers)
    {
        var deltas = new int[4];
        foreach (var transfer in transfers)
        {
            deltas[transfer.FromSeat] = checked(deltas[transfer.FromSeat] - transfer.Points);
            deltas[transfer.ToSeat] = checked(deltas[transfer.ToSeat] + transfer.Points);
        }
        return state with
        {
            Window = null,
            Settlement = new(reason, nextDealer, source, winners, birds,
                [.. deltas.Select((delta, seat) => new SeatResult(seat, delta))], transfers.ToImmutable())
        };
    }
}
