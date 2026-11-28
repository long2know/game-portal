using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Mahjong.Rules;

namespace Mahjong.Server;

internal static class Projection
{
    // Absolute seat order matches the artwork in web/src/actors.ts.
    private static readonly ImmutableArray<string> PortalBotNames = ["Maple", "Pip", "Tallow", "Nori"];

    private sealed record SeatSnapshot(int Seat, string? InviteCode, HandTile[] Hand, string? DrawnTileId,
        LegalOption[] LegalOptions,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImmutableArray<HandTile>? KongReplacements);

    public static object Snapshot(RoomCheckpoint room, int seat, DateTimeOffset utc, bool portalEnabled, Func<Guid, bool> connected)
    {
        var table = room.Game is null ? null : ChangshaEngine.ViewPublic(room.Game);
        var view = room.Game is null ? null : ChangshaEngine.ViewSeat(room.Game, seat);
        ImmutableArray<HandTile>? replacements = room.RulesVersion == ChangshaEngine.TwoTileKongProfile
            && table is { Phase: "turn", Window: { Reason: "turn", Trigger: null }, KongRoll.Seat: var kongSeat }
            && kongSeat == seat && table.TurnSeat == seat
            && view?.KongReplacements is { IsDefaultOrEmpty: false } candidates ? candidates : null;
        return new
        {
            v = Wire.Version,
            type = "snapshot",
            roomId = room.Id,
            room.RoomRevision,
            room.GameRevision,
            serverTimeUtc = utc,
            room = new
            {
                room.Visibility,
                room.Status,
                room.HostSeat,
                room.MatchHands,
                room.RulesVersion,
                room.TableTotals,
                nextDealerSeat = table is null ? room.NextDealerSeat : table.Settlement?.NextDealerSeat,
                paused = room.PauseStartedUtc.HasValue,
                room.AutoResumeAtUtc,
                seats = room.Seats.Select((chair, index) => new
                {
                    seat = index,
                    kind = chair.IsBot ? "bot" : chair.IsOpen ? "open" : "human",
                    displayName = portalEnabled && chair.IsBot ? PortalBotNames[index] : chair.DisplayName,
                    chair.Ready,
                    connected = chair.GuestId is { } id && connected(id)
                }).ToArray(),
                chat = room.Chat.Select(post => new
                {
                    post.MessageId, post.Seat, post.DisplayName, post.Text, post.SentAtUtc
                }).ToArray()
            },
            game = table is null ? null : new
            {
                table.HandNumber,
                table.DealerSeat,
                table.Phase,
                table.TurnSeat,
                table.WallCount,
                table.KongRoll,
                seats = table.Seats.Select(player => new
                {
                    player.Seat,
                    player.ConcealedCount,
                    player.Discards,
                    melds = player.Melds.Select(meld => new
                    {
                        meld.Kind, meld.Method, meld.FromSeat,
                        tiles = meld.Method == "concealed" ? new string?[4] : meld.Tiles.ToArray()
                    }).ToArray(),
                    player.RevealedTiles,
                    total = room.Totals[player.Seat]
                }).ToArray(),
                window = table.Window is not { } window ? null : new
                {
                    windowId = room.PublicWindowId(window),
                    anchorGameRevision = room.WindowAnchor!.Value,
                    window.Reason,
                    deadlineUtc = room.PauseStartedUtc.HasValue ? (DateTimeOffset?)null : room.ToUtc(window.ClosesAtGameTime),
                    window.Trigger
                },
                settlement = table.Settlement is not { } settlement ? null : new
                {
                    settlement.Reason, settlement.NextDealerSeat, settlement.SourceSeat, settlement.Winners, settlement.Birds,
                    seatResults = settlement.SeatResults.Select(result => new
                    {
                        result.Seat, result.Delta, total = room.Totals[result.Seat]
                    }).ToArray(),
                    settlement.Transfers
                }
            },
            self = new SeatSnapshot(
                seat,
                seat == room.HostSeat ? room.InviteCode : null,
                view?.Hand.ToArray() ?? [],
                view?.DrawnTileId,
                room.PauseStartedUtc.HasValue ? [] : view?.LegalOptions.ToArray() ?? [],
                replacements)
        };
    }
}
