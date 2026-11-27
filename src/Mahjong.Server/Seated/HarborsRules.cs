using System.Text.Json;
using JadeCourt.Games.Harbors;

namespace Mahjong.Server.Seated;

internal static class HarborsRules
{
    private const int MaxTargetId = 255;
    public static readonly SeatedRules Game = new("jade-harbors", HarborsGame.SeatCount, HarborsGame.SeatCount, Start, View);

    public static void Map(WebApplication app) => SeatedApi.Map<HarborsAction>(app, Game, ReadAction, Act);

    private static SeatedTurn Start(int players, int seat, bool bots, CancellationToken stop) =>
        Finish(HarborsGame.Start(stop: stop), seat, bots, stop);

    private static object View(string checkpoint, int seat, bool playing) => HarborsGame.View(checkpoint, seat, playing);

    private static SeatedTurn Act(string checkpoint, int seat, bool bots, HarborsAction action, CancellationToken stop)
    {
        try { checkpoint = HarborsGame.Apply(checkpoint, seat, action); }
        catch (HarborsRejectedException rejected)
        {
            throw new PortalFault(400, rejected.Reason switch
            {
                HarborsRejection.IllegalAction => "illegal_action",
                HarborsRejection.NotYourTurn => "not_your_turn",
                HarborsRejection.Finished => "game_finished",
                _ => throw new InvalidDataException("Unknown Jade Harbors rejection.", rejected)
            });
        }
        return Finish(checkpoint, seat, bots, stop);
    }

    private static SeatedTurn Finish(string checkpoint, int seat, bool bots, CancellationToken stop)
    {
        if (bots) checkpoint = HarborsGame.PlayBotsUntil(checkpoint, seat, stop);
        var view = HarborsGame.View(checkpoint, seat);
        return new(checkpoint, view, view.Phase == "finished");
    }

    private static bool ReadAction(JsonElement body, out HarborsAction action)
    {
        action = default;
        var kind = body.Get("kind").AsString() switch
        {
            "buildPier" => HarborsActionKind.BuildPier,
            "buildRoute" => HarborsActionKind.BuildRoute,
            "upgrade" => HarborsActionKind.Upgrade,
            "trade" => HarborsActionKind.Trade,
            "roll" => HarborsActionKind.Roll,
            "endTurn" => HarborsActionKind.EndTurn,
            _ => (HarborsActionKind)0
        };
        switch (kind)
        {
            case HarborsActionKind.BuildPier or HarborsActionKind.BuildRoute or HarborsActionKind.Upgrade
                when Wire.Fields(body, "kind", "target") && body.Get("target").Int(out var target)
                    && target is >= 0 and <= MaxTargetId:
                action = new(kind, Target: target);
                return true;
            case HarborsActionKind.Trade when Wire.Fields(body, "kind", "give", "take")
                && body.Get("give").Int(out var give) && give is >= 0 and <= 3
                && body.Get("take").Int(out var take) && take is >= 0 and <= 3:
                action = new(kind, Give: give, Take: take);
                return true;
            case HarborsActionKind.Roll or HarborsActionKind.EndTurn when Wire.Fields(body, "kind"):
                action = new(kind);
                return true;
            default:
                return false;
        }
    }
}
