using System.Text.Json;
using JadeCourt.Words;

namespace Mahjong.Server.Seated;

internal static class WordsRules
{
    private const int MaxTurnTiles = 7;
    private const int MaxCoordinate = 14;
    private static readonly WordGame Engine = new();
    public static readonly SeatedRules Game = new("words", 2, 2, Start, View);

    public static void Map(WebApplication app) => SeatedApi.Map<WordAction>(app, Game, ReadAction, Act);

    private static SeatedTurn Start(int players, int seat, bool bots, CancellationToken stop)
    {
        var checkpoint = Engine.Start();
        return new(checkpoint, Engine.View(checkpoint, seat));
    }

    private static SeatedTurn Act(string checkpoint, int seat, bool bots, WordAction action, CancellationToken stop)
    {
        var before = Engine.View(checkpoint, seat);
        if (before.Turn != seat) throw new PortalFault(400, "not_your_turn");
        try { checkpoint = Engine.Apply(checkpoint, seat, action); }
        catch (InvalidOperationException) { throw new PortalFault(400, "illegal_action"); }

        var after = Engine.View(checkpoint, seat);
        if (bots && after.Turn == 1)
        {
            stop.ThrowIfCancellationRequested();
            try { checkpoint = Engine.Apply(checkpoint, 1, Engine.SelectBotMove(Engine.View(checkpoint, 1))); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("The Words bot rejected its own move.", ex); }
            after = Engine.View(checkpoint, seat);
        }
        return new(checkpoint, after, after.Status == "finished");
    }

    private static object View(string checkpoint, int seat, bool playing)
    {
        var view = Engine.View(checkpoint, seat);
        return !playing && view.Status == "playing"
            ? view with { Turn = null, Status = "finished", Detail = "Table closed.", ExchangeLimit = 0 } : view;
    }

    private static bool ReadAction(JsonElement body, out WordAction action)
    {
        action = null!;
        switch (body.Get("kind").AsString())
        {
            case "pass" when Wire.Fields(body, "kind"):
                action = new("pass");
                return true;
            case "exchange" when Wire.Fields(body, "kind", "exchange"):
                var ids = body.Get("exchange");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > MaxTurnTiles) return false;
                var exchange = new int[ids.GetArrayLength()];
                var number = 0;
                foreach (var id in ids.EnumerateArray())
                {
                    if (!id.Int(out var tile) || tile <= 0) return false;
                    exchange[number++] = tile;
                }
                action = new("exchange", Exchange: exchange);
                return true;
            case "place" when Wire.Fields(body, "kind", "placements"):
                var entries = body.Get("placements");
                if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > MaxTurnTiles) return false;
                var placements = new WordPlacement[entries.GetArrayLength()];
                var index = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!Wire.Fields(entry, "row", "column", "tileId", "letter")
                        || !entry.Get("row").Int(out var row) || row is < 0 or > MaxCoordinate
                        || !entry.Get("column").Int(out var column) || column is < 0 or > MaxCoordinate
                        || !entry.Get("tileId").Int(out var tileId) || tileId <= 0)
                        return false;
                    var designation = entry.Get("letter");
                    var letter = designation.AsString();
                    if (designation.ValueKind != JsonValueKind.Undefined &&
                        (letter is not { Length: 1 } || letter[0] is < 'A' or > 'Z'))
                        return false;
                    placements[index++] = new(row, column, tileId, letter);
                }
                action = new("place", Placements: placements);
                return true;
            default:
                return false;
        }
    }
}
