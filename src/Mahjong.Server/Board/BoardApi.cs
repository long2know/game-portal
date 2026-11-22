using System.Text.Json;
using static Mahjong.Server.PortalApi;

namespace Mahjong.Server.Board;

internal static class BoardApi
{
    private sealed record StartRequest(string Game, string Variant, int Players, bool Bots, string? BotDifficulty);
    private sealed record MoveRequest(long Revision, string MoveId);

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/board");
        routes.MapGet("/games", (BoardRegistry registry) => Json(new { games = registry.Definitions }))
            .RequireRateLimiting(Read);
        routes.MapPost("/tables", (HttpContext context, BoardTables tables) => ForGuestAsync(context, true,
            async (guest, stop) =>
                await HttpApi.ReadJsonAsync(context, ReadStart) is { } request
                    ? Json(await tables.CreateAsync(guest, request.Game, request.Variant, request.Players, request.Bots,
                        request.BotDifficulty, stop))
                    : Invalid())).RequireRateLimiting(Write);
        routes.MapPost("/join", (HttpContext context, BoardTables tables) => ForGuestAsync(context, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, ReadInvite) is { } invite
                ? Json(await tables.JoinAsync(guest, invite, stop)) : Invalid())).RequireRateLimiting(Write);
        routes.MapGet("/tables/{id}", (string id, HttpContext context, BoardTables tables) => ForGuestAsync(context, false,
            async (guest, stop) => IsKey(id) ? Json(await tables.GetAsync(guest.Id, id, stop)) : Missing()))
            .RequireRateLimiting(Read);
        routes.MapPost("/tables/{id}/moves", (string id, HttpContext context, BoardTables tables) => ForGuestAsync(context,
            true, async (guest, stop) =>
            {
                if (!IsKey(id)) return Missing();
                return await HttpApi.ReadJsonAsync(context, ReadMove) is { } request
                    ? Json(await tables.MoveAsync(guest.Id, id, request.Revision, request.MoveId, stop)) : Invalid();
            })).RequireRateLimiting(Write);
        routes.MapPost("/tables/{id}/leave", (string id, HttpContext context, BoardTables tables) =>
            EndAsync(id, context, tables, resign: false)).RequireRateLimiting(Write);
        routes.MapPost("/tables/{id}/resign", (string id, HttpContext context, BoardTables tables) =>
            EndAsync(id, context, tables, resign: true)).RequireRateLimiting(Write);
        routes.MapGet("/resume", (HttpContext context, BoardTables tables) => ForGuestAsync(context, false,
            async (guest, stop) => context.Request.Query["game"] is { Count: 1 } game && Wire.IsLocator(game[0])
                ? Json(new { tableId = await tables.ResumeAsync(guest.Id, game[0]!, stop) }) : Invalid()))
            .RequireRateLimiting(Read);
    }

    private static Task<IResult> EndAsync(string id, HttpContext context, BoardTables tables, bool resign) =>
        ForGuestAsync(context, true, async (guest, stop) =>
        {
            if (!IsKey(id)) return Missing();
            return await HttpApi.ReadJsonAsync(context, ReadRevision) is { } request
                ? Json(await tables.EndAsync(guest.Id, id, request.Revision, resign, stop)) : Invalid();
        });

    private static StartRequest? ReadStart(JsonElement body)
    {
        var game = body.Get("game").AsString();
        var variant = body.Get("variant").AsString();
        var field = body.Get("botDifficulty");
        var difficulty = field.AsString();
        return Wire.Fields(body, "game", "variant", "players", "bots", "botDifficulty") && Wire.IsLocator(game)
            && Wire.IsLocator(variant) && (field.ValueKind == JsonValueKind.Undefined || difficulty is not null)
            && body.Get("players").Int(out var players) && body.Get("bots").TryFlag(out var bots)
            ? new(game!, variant!, players, bots, difficulty) : null;
    }

    private static MoveRequest? ReadMove(JsonElement body)
    {
        var moveId = body.Get("moveId").AsString();
        return Wire.Fields(body, "moveId", "revision") && Wire.Revision(body.Get("revision"), out var revision)
            && moveId is { Length: > 0 and <= 1024 } && !moveId.Any(char.IsControl)
            ? new(revision, moveId) : null;
    }
}
