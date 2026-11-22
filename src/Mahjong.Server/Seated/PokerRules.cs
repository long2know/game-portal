using System.Text;
using System.Text.Json;
using JadeCourt.Poker;
using JadeCourt.Poker.Persistence;

namespace Mahjong.Server.Seated;

internal static class PokerRules
{
    private const int StartingChips = 1000;
    private const int SmallBlind = 5;
    private const int BigBlind = 10;
    private const int MaxBotActions = 256;

    public static readonly SeatedRules Game = new("poker", 2, 6, Start, View);

    public static void Map(WebApplication app) => SeatedApi.Map<HoldemAction>(app, Game, ReadAction, Act, Next);

    private static SeatedTurn Start(int players, int seat, bool bots, CancellationToken stop) =>
        Finish(HoldemTable.Create(Enumerable.Repeat(StartingChips, players).ToArray(), SmallBlind, BigBlind), seat, bots, stop);

    private static SeatedTurn Act(string checkpoint, int seat, bool bots, HoldemAction action, CancellationToken stop)
    {
        var table = Load(checkpoint);
        var view = table.View(seat);
        if (view.Result is not null) throw new PortalFault(400, "hand_finished");
        try { table.Act(seat, view.Revision, action); }
        catch (HoldemRejectedException ex) { throw Rejected(ex); }
        return Finish(table, seat, bots, stop);
    }

    private static SeatedTurn Next(string checkpoint, int seat, bool bots, CancellationToken stop)
    {
        var table = Load(checkpoint);
        try { table.StartNextHand(table.View(seat).Revision); }
        catch (HoldemRejectedException ex) { throw Rejected(ex); }
        return Finish(table, seat, bots, stop);
    }

    private static object View(string checkpoint, int seat, bool playing)
    {
        var view = Load(checkpoint).View(seat);
        return playing ? view : view with { ActingSeat = null, LegalActions = null };
    }

    private static SeatedTurn Finish(HoldemTable table, int seat, bool bots, CancellationToken stop)
    {
        var view = table.View(seat);
        if (bots)
        {
            for (var step = 0; view.ActingSeat is > 0 and int bot; step++)
            {
                stop.ThrowIfCancellationRequested();
                if (step == MaxBotActions) throw new InvalidDataException("Poker bots exceeded their action budget.");
                var botView = table.View(bot);
                try { table.Act(bot, botView.Revision, HoldemBot.Decide(botView)); }
                catch (Exception ex) when (ex is HoldemRejectedException or ArgumentException)
                {
                    throw new InvalidDataException("The poker bot did not return a legal action.", ex);
                }
                view = table.View(seat);
            }
        }
        using var buffer = new MemoryStream();
        HoldemServerPersistence.WriteCheckpoint(table, buffer);
        return new(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), view, view.MatchWinnerSeat.HasValue);
    }

    private static HoldemTable Load(string checkpoint)
    {
        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(checkpoint), writable: false);
        return HoldemServerPersistence.RestoreCheckpoint(buffer);
    }

    private static Exception Rejected(HoldemRejectedException ex) => ex.Reason switch
    {
        HoldemRejection.NotYourTurn => new PortalFault(400, "not_your_turn"),
        HoldemRejection.IllegalAction => new PortalFault(400, "illegal_action"),
        HoldemRejection.HandInProgress => new PortalFault(400, "hand_in_progress"),
        HoldemRejection.MatchComplete => new PortalFault(400, "game_finished"),
        _ => new InvalidDataException("The poker engine revision disagreed with its view.", ex)
    };

    private static bool ReadAction(JsonElement body, out HoldemAction action)
    {
        action = default;
        if (!Wire.Fields(body, "kind", "to")) return false;
        var kind = body.Get("kind").AsString() switch
        {
            "fold" => HoldemActionKind.Fold,
            "check" => HoldemActionKind.Check,
            "call" => HoldemActionKind.Call,
            "bet" => HoldemActionKind.Bet,
            "raise" => HoldemActionKind.Raise,
            _ => (HoldemActionKind)0
        };
        var to = body.Get("to");
        var amount = 0;
        if (kind == 0 || (kind is HoldemActionKind.Bet or HoldemActionKind.Raise
                ? !to.Int(out amount) || amount <= 0 : to.ValueKind != JsonValueKind.Undefined))
            return false;
        action = new(kind, amount);
        return true;
    }
}
