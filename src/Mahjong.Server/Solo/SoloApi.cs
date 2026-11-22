using System.Text.Json;
using JadeCourt.Solo;
using static Mahjong.Server.PortalApi;

namespace Mahjong.Server.Solo;

internal static class SoloApi
{
    private sealed record MoveRequest(long Revision, SoloAction Action);

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/solo/{game}");
        routes.MapGet("", (string game, HttpContext context, SoloTables tables) => ForGuestAsync(context, false,
            async (guest, stop) => Json(new { table = await tables.GetAsync(guest.Id, game, stop) }))).RequireRateLimiting(Read);
        routes.MapPost("", (string game, HttpContext context, SoloTables tables) => ForGuestAsync(context, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, ReadVariant) is { } variant
                ? Json(await tables.DealAsync(guest.Id, game, variant, stop)) : Invalid())).RequireRateLimiting(Write);
        routes.MapPost("/moves", (string game, HttpContext context, SoloTables tables) => ForGuestAsync(context, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, body => ReadMove(game, body)) is { } request
                ? Json(await tables.MoveAsync(guest.Id, game, request.Revision, request.Action, stop)) : Invalid()))
            .RequireRateLimiting(Write);
    }

    private static string? ReadVariant(JsonElement body) =>
        Wire.Fields(body, "variant") && body.Get("variant").AsString() is { } variant && Wire.IsLocator(variant) ? variant : null;

    private static MoveRequest? ReadMove(string game, JsonElement body) =>
        Wire.Fields(body, "revision", "action") && Wire.Revision(body.Get("revision"), out var revision)
        && ReadAction(game, body.Get("action")) is { } action ? new(revision, action) : null;

    private static SoloAction? ReadAction(string game, JsonElement body) => (game, body.Get("kind").AsString()) switch
    {
        ("solitaire", "draw") when Wire.Fields(body, "kind") => new("draw"),
        ("solitaire", "move") when Wire.Fields(body, "kind", "from", "to", "count")
            && body.Get("from").AsString() is { } from && Wire.IsLocator(from)
            && body.Get("to").AsString() is { } to && Wire.IsLocator(to)
            && body.Get("count").Int(out var count) && count is >= 1 and <= 13 => new("move", from, to, count),
        ("sudoku", "set") when Wire.Fields(body, "kind", "index", "value")
            && body.Get("index").Int(out var index) && index is >= 0 and < 81
            && body.Get("value").Int(out var value) && value is >= 0 and <= 9 => new("set", Index: index, Value: value),
        ("sudoku", "hint") when Wire.Fields(body, "kind", "index") => ReadHint(body.Get("index")),
        _ => null
    };

    private static SoloAction? ReadHint(JsonElement index) => index.ValueKind == JsonValueKind.Undefined ? new("hint")
        : index.Int(out var value) && value is >= 0 and < 81 ? new("hint", Index: value) : null;
}
