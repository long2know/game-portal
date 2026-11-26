using System.Text.Json;
using JadeCourt.Games.Hanafuda;

namespace Mahjong.Server.Seated;

internal static class KoiKoiRules
{
    public static readonly SeatedRules Game = new("koi-koi", KoiKoiGame.SeatCount, KoiKoiGame.SeatCount, Start, View);

    public static void Map(WebApplication app) => SeatedApi.Map<KoiKoiAction>(app, Game, ReadAction, Act, Next);

    private static SeatedTurn Start(int players, int seat, bool bots, CancellationToken stop) =>
        Project(KoiKoiGame.Start(stop: stop), seat, bots, stop);

    private static object View(string checkpoint, int seat, bool playing) => KoiKoiGame.View(checkpoint, seat, playing);

    private static SeatedTurn Act(string checkpoint, int seat, bool bots, KoiKoiAction action, CancellationToken stop)
    {
        try { checkpoint = KoiKoiGame.Apply(checkpoint, seat, action); }
        catch (KoiKoiRejectedException rejected) { throw Fault(rejected); }
        return Project(checkpoint, seat, bots, stop);
    }

    private static SeatedTurn Next(string checkpoint, int seat, bool bots, CancellationToken stop)
    {
        try { checkpoint = KoiKoiGame.NextRound(checkpoint, seat); }
        catch (KoiKoiRejectedException rejected) { throw Fault(rejected); }
        return Project(checkpoint, seat, bots, stop);
    }

    private static PortalFault Fault(KoiKoiRejectedException rejected) => new(400, rejected.Reason switch
    {
        KoiKoiRejection.NotYourTurn => "not_your_turn",
        KoiKoiRejection.RoundFinished => "round_finished",
        KoiKoiRejection.RoundInProgress => "round_in_progress",
        KoiKoiRejection.Finished => "game_finished",
        _ => "illegal_action"
    });

    private static SeatedTurn Project(string checkpoint, int seat, bool bots, CancellationToken stop)
    {
        if (bots) checkpoint = KoiKoiGame.PlayBotTurn(checkpoint, 1, stop);
        var view = KoiKoiGame.View(checkpoint, seat);
        return new(checkpoint, view, view.Phase == "finished");
    }

    private static bool ReadAction(JsonElement body, out KoiKoiAction action)
    {
        action = default;
        switch (body.Get("kind").AsString())
        {
            case "play" when Wire.Fields(body, "kind", "cardId", "targetId") && Card(body.Get("cardId"), out var id):
                var target = body.Get("targetId");
                if (target.ValueKind == JsonValueKind.Undefined)
                    action = new(KoiKoiActionKind.Play, id);
                else if (Card(target, out var fieldId))
                    action = new(KoiKoiActionKind.Play, id, fieldId);
                else
                    return false;
                return true;
            case "takeDraw" when Wire.Fields(body, "kind", "targetId") && Card(body.Get("targetId"), out var choice):
                action = new(KoiKoiActionKind.TakeDraw, TargetId: choice);
                return true;
            case "score" when Wire.Fields(body, "kind"):
                action = new(KoiKoiActionKind.Score);
                return true;
            case "continue" when Wire.Fields(body, "kind"):
                action = new(KoiKoiActionKind.Continue);
                return true;
            default:
                return false;
        }
    }

    private static bool Card(JsonElement value, out int id) => value.Int(out id) && id is >= 0 and < KoiKoiGame.DeckSize;
}
