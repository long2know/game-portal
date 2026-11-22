using System.Security.Cryptography;
using System.Text.Json;
using JadeCourt.Games.Backgammon;

namespace Mahjong.Server.Seated;

internal static class BackgammonRules
{
    public static readonly SeatedRules Game = new("backgammon", 2, 2, Start, View);

    public static void Map(WebApplication app) => SeatedApi.Map<BackgammonAction>(app, Game, ReadAction, Act);

    private static int Die() => RandomNumberGenerator.GetInt32(1, 7);

    private static SeatedTurn Start(int players, int seat, bool bots, CancellationToken stop) =>
        Finish(BackgammonGame.Start(Die, stop), seat, bots, stop);

    private static object View(string checkpoint, int seat, bool playing) =>
        BackgammonGame.View(checkpoint, seat, playing);

    private static SeatedTurn Act(string checkpoint, int seat, bool bots, BackgammonAction action, CancellationToken stop)
    {
        try { checkpoint = BackgammonGame.Apply(checkpoint, seat, action, Die); }
        catch (BackgammonRejectedException rejected)
        {
            throw new PortalFault(400, rejected.Reason switch
            {
                BackgammonRejection.NotYourTurn => "not_your_turn",
                BackgammonRejection.Finished => "game_finished",
                _ => "illegal_action"
            });
        }
        return Finish(checkpoint, seat, bots, stop);
    }

    private static SeatedTurn Finish(string checkpoint, int seat, bool bots, CancellationToken stop)
    {
        if (bots) checkpoint = BackgammonGame.PlayBotTurn(checkpoint, 1, Die, stop);
        var view = BackgammonGame.View(checkpoint, seat);
        return new(checkpoint, view, view.Winner.HasValue);
    }

    private static bool ReadAction(JsonElement body, out BackgammonAction action)
    {
        action = default;
        switch (body.Get("kind").AsString())
        {
            case "roll" when Wire.Fields(body, "kind"):
                action = new(BackgammonActionKind.Roll);
                return true;
            case "pass" when Wire.Fields(body, "kind"):
                action = new(BackgammonActionKind.Pass);
                return true;
            case "move" when Wire.Fields(body, "kind", "from", "to", "die")
                && body.Get("from").Int(out var from) && from is >= BackgammonGame.Bar and < BackgammonGame.Off
                && body.Get("to").Int(out var to) && to is > BackgammonGame.Bar and <= BackgammonGame.Off
                && body.Get("die").Int(out var die) && die is >= 1 and <= 6:
                action = new(BackgammonActionKind.Move, new(from, to, die));
                return true;
            default:
                return false;
        }
    }
}
