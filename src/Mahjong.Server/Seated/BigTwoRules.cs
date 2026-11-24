using System.Text.Json;
using JadeCourt.Games.BigTwo;

namespace Mahjong.Server.Seated;

internal static class BigTwoRules
{
    public static readonly SeatedRules Game = new("big-two", BigTwoGame.SeatCount, BigTwoGame.SeatCount, Start, View);

    public static void Map(WebApplication app) => SeatedApi.Map<BigTwoAction>(app, Game, ReadAction, Act);

    private static SeatedTurn Start(int players, int seat, bool bots, CancellationToken stop) =>
        Finish(BigTwoGame.Start(), seat, bots, stop);

    private static object View(string checkpoint, int seat, bool playing) =>
        BigTwoGame.View(checkpoint, seat, playing);

    private static SeatedTurn Act(string checkpoint, int seat, bool bots, BigTwoAction action, CancellationToken stop)
    {
        try { checkpoint = BigTwoGame.Apply(checkpoint, seat, action); }
        catch (BigTwoRejectedException rejected)
        {
            throw new PortalFault(400, rejected.Reason switch
            {
                BigTwoRejection.NotYourTurn => "not_your_turn",
                BigTwoRejection.Finished => "game_finished",
                _ => "illegal_action"
            });
        }
        return Finish(checkpoint, seat, bots, stop);
    }

    private static SeatedTurn Finish(string checkpoint, int seat, bool bots, CancellationToken stop)
    {
        if (bots) checkpoint = BigTwoGame.PlayBots(checkpoint, seat, stop);
        var view = BigTwoGame.View(checkpoint, seat);
        return new(checkpoint, view, view.WinnerSeat.HasValue);
    }

    private static bool ReadAction(JsonElement body, out BigTwoAction action)
    {
        action = default;
        switch (body.Get("kind").AsString())
        {
            case "pass" when Wire.Fields(body, "kind"):
                action = new(BigTwoActionKind.Pass);
                return true;
            case "play" when Wire.Fields(body, "kind", "cards"):
                var ids = body.Get("cards");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is not (1 or 2 or 3 or 5)) return false;
                var cards = new int[ids.GetArrayLength()];
                var index = 0;
                foreach (var id in ids.EnumerateArray())
                {
                    if (!id.Int(out var card) || card is < 0 or >= BigTwoGame.DeckSize
                        || Array.IndexOf(cards, card, 0, index) >= 0)
                        return false;
                    cards[index++] = card;
                }
                action = new(BigTwoActionKind.Play, cards);
                return true;
            default:
                return false;
        }
    }
}
