using System.Text.Json;
using static Mahjong.Server.PortalApi;

namespace Mahjong.Server.Seated;

internal static class SeatedApi
{
    public delegate bool TryReadAction<TAction>(JsonElement body, out TAction action);
    private sealed record StartRequest(int Players, bool Bots);
    private sealed record ActionRequest<TAction>(long Revision, TAction Action);

    public static void Map<TAction>(WebApplication app, SeatedRules rules, TryReadAction<TAction> parse,
        Func<string, int, bool, TAction, CancellationToken, SeatedTurn> apply, SeatedCommand? next = null)
    {
        var routes = app.MapGroup("/api/" + rules.Game);
        routes.MapPost("/tables", (HttpContext context, SeatedTables tables) => ForGuestAsync(context, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, ReadStart) is { } request
                ? Json(await tables.CreateAsync(rules, guest, request.Players, request.Bots, stop)) : Invalid()))
            .RequireRateLimiting(Write);
        routes.MapPost("/join", (HttpContext context, SeatedTables tables) => ForGuestAsync(context, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, ReadInvite) is { } invite
                ? Json(await tables.JoinAsync(rules, guest, invite, stop)) : Invalid())).RequireRateLimiting(Write);
        routes.MapGet("/resume", (HttpContext context, SeatedTables tables) => ForGuestAsync(context, false,
            async (guest, stop) => Json(new { tableId = await tables.ResumeAsync(rules, guest.Id, stop) })))
            .RequireRateLimiting(Read);
        routes.MapGet("/tables/{id}", (string id, HttpContext context, SeatedTables tables) => ForGuestAsync(context, false,
            async (guest, stop) => IsKey(id) ? Json(await tables.GetAsync(rules, guest.Id, id, stop)) : Missing()))
            .RequireRateLimiting(Read);
        routes.MapPost("/tables/{id}/actions", (string id, HttpContext context, SeatedTables tables) =>
            ForGuestAsync(context, true, async (guest, stop) =>
            {
                if (!IsKey(id)) return Missing();
                if (await HttpApi.ReadJsonAsync(context, body => ReadAction(body, parse)) is not { } request)
                    return Invalid();
                return Json(await tables.ChangeAsync(rules, guest.Id, id, request.Revision,
                    (checkpoint, seat, bots, token) => apply(checkpoint, seat, bots, request.Action, token), stop));
            })).RequireRateLimiting(Write);
        routes.MapPost("/tables/{id}/leave", (string id, HttpContext context, SeatedTables tables) =>
            ForGuestAsync(context, true, async (guest, stop) =>
            {
                if (!IsKey(id)) return Missing();
                return await HttpApi.ReadJsonAsync(context, ReadRevision) is { } request
                    ? Json(await tables.LeaveAsync(rules, guest.Id, id, request.Revision, stop)) : Invalid();
            })).RequireRateLimiting(Write);
        if (next is not null)
            routes.MapPost("/tables/{id}/next", (string id, HttpContext context, SeatedTables tables) =>
                ForGuestAsync(context, true, async (guest, stop) =>
                {
                    if (!IsKey(id)) return Missing();
                    return await HttpApi.ReadJsonAsync(context, ReadRevision) is { } request
                        ? Json(await tables.ChangeAsync(rules, guest.Id, id, request.Revision, next, stop)) : Invalid();
                })).RequireRateLimiting(Write);
    }

    private static StartRequest? ReadStart(JsonElement body) => Wire.Fields(body, "players", "bots")
        && body.Get("players").Int(out var players) && body.Get("bots").TryFlag(out var bots) ? new(players, bots) : null;

    private static ActionRequest<TAction>? ReadAction<TAction>(JsonElement body, TryReadAction<TAction> parse) =>
        Wire.Fields(body, "revision", "action") && Wire.Revision(body.Get("revision"), out var revision)
        && parse(body.Get("action"), out var action) ? new(revision, action) : null;
}
