using System.Security.Cryptography;
using System.Text.Json;
using JadeCourt.Games.Farkle;

namespace Mahjong.Server.Seated;

internal static class FarkleRules
{
    private static readonly SeatedRules Game = new("farkle", FarkleGame.MinPlayers, FarkleGame.MaxPlayers, Start, View);

    private sealed record Choice(int Mask, int Points);
    private sealed record TurnResult(int Seat, string Kind, int Points, int[] Dice);
    private sealed record Position(int? Turn, string Phase, int[] Scores, int[] Dice, int[] Kept, int DiceToRoll,
        int TurnPoints, bool CanRoll, bool CanBank, Choice[] Choices, int? FinalStarter, int[] Winners,
        TurnResult[] RecentTurns);

    public static void Map(WebApplication app) => SeatedApi.Map<FarkleAction>(app, Game, ReadAction, Act);

    private static SeatedTurn Start(int players, int seat, bool bots, CancellationToken stop)
    {
        var game = FarkleGame.Start(players);
        return new(game.Save(), Project(game, seat, playing: true));
    }

    private static int RollDie() => RandomNumberGenerator.GetInt32(1, 7);

    private static SeatedTurn Act(string checkpoint, int seat, bool bots, FarkleAction action, CancellationToken stop)
    {
        var game = FarkleGame.Restore(checkpoint);
        if (game.Turn != seat) throw new PortalFault(400, "not_your_turn");
        try { game = game.Apply(seat, action, RollDie); }
        catch (InvalidOperationException) { throw new PortalFault(400, "illegal_action"); }
        if (bots) game = AdvanceBots(game, stop);
        var finished = game.Phase == FarklePhase.Finished;
        return new(game.Save(), Project(game, seat, playing: !finished), finished);
    }

    private static FarkleGame AdvanceBots(FarkleGame game, CancellationToken stop)
    {
        var budget = (game.Scores.Count - 1) * (2 * FarkleGame.MaxRolls + 1);
        while (game.Turn is > 0 and { } bot)
        {
            if (budget-- == 0) throw new InvalidDataException("Farkle bot exceeded its turn budget.");
            stop.ThrowIfCancellationRequested();
            try { game = game.Apply(bot, game.BotAction(), RollDie); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Farkle bot chose an illegal action.", ex); }
        }
        return game;
    }

    private static object View(string checkpoint, int seat, bool playing) =>
        Project(FarkleGame.Restore(checkpoint), seat, playing);

    private static Position Project(FarkleGame game, int seat, bool playing)
    {
        var canAct = playing && game.Turn == seat;
        var phase = game.Phase switch
        {
            FarklePhase.Roll => "roll",
            FarklePhase.Keep => "keep",
            _ => "finished"
        };
        return new(playing ? game.Turn : null, phase, game.Scores.ToArray(), game.Dice.ToArray(),
            Enumerable.Range(0, game.Dice.Count).Where(i => (game.KeptMask & (1 << i)) != 0).ToArray(),
            game.DiceToRoll, game.TurnPoints, canAct && game.CanRoll, canAct && game.CanBank,
            canAct ? game.Choices().Select(choice => new Choice(choice.Mask, choice.Points)).ToArray() : [],
            game.FinalStarter, game.Winners(), game.RecentTurns().Select(result =>
                new TurnResult(result.Seat, result.Banked ? "banked" : "farkle", result.Points, result.Dice)).ToArray());
    }

    private static bool ReadAction(JsonElement body, out FarkleAction action)
    {
        action = default;
        switch (body.Get("kind").AsString())
        {
            case "roll" when Wire.Fields(body, "kind"):
                action = new(FarkleActionKind.Roll);
                return true;
            case "bank" when Wire.Fields(body, "kind"):
                action = new(FarkleActionKind.Bank);
                return true;
            case "keep" when Wire.Fields(body, "kind", "dice"):
                var dice = body.Get("dice");
                if (dice.ValueKind != JsonValueKind.Array || dice.GetArrayLength() is < 1 or > FarkleGame.DiceCount)
                    return false;
                var mask = 0;
                foreach (var die in dice.EnumerateArray())
                {
                    if (!die.Int(out var index) || index is < 0 or >= FarkleGame.DiceCount) return false;
                    var bit = 1 << index;
                    if ((mask & bit) != 0) return false;
                    mask |= bit;
                }
                action = new(FarkleActionKind.Keep, mask);
                return true;
            default:
                return false;
        }
    }
}
