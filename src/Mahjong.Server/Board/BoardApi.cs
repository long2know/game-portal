using System.Text.Json;
using System.Threading.RateLimiting;

namespace Mahjong.Server.Board;

internal static class BoardApi
{
    private static readonly object CookieGuestKey = new();

    private sealed record StartRequest(string Game, string Variant, int Players, bool Bots, string? BotDifficulty);
    private sealed record MoveRequest(long Revision, string MoveId);
    private sealed record RevisionRequest(long Revision);

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/board");
        routes.MapGet("/games", (BoardRegistry registry) => Json(new { games = registry.Definitions }))
            .RequireRateLimiting("board-read");
        routes.MapPost("/tables", (HttpContext context, BoardTables tables) => ForGuestAsync(context, tables, true,
            async (guest, stop) =>
                await HttpApi.ReadJsonAsync(context, ReadStart) is { } request
                    ? Json(await tables.CreateAsync(guest, request.Game, request.Variant, request.Players, request.Bots,
                        request.BotDifficulty, stop))
                    : Invalid())).RequireRateLimiting("board-write");
        routes.MapPost("/join", (HttpContext context, BoardTables tables) => ForGuestAsync(context, tables, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, ReadInvite) is { } invite
                ? Json(await tables.JoinAsync(guest, invite, stop)) : Invalid())).RequireRateLimiting("board-write");
        routes.MapGet("/tables/{id}", (string id, HttpContext context, BoardTables tables) => ForGuestAsync(context, tables,
            false, async (guest, stop) => IsKey(id) ? Json(await tables.GetAsync(guest.Id, id, stop)) : Missing()))
            .RequireRateLimiting("board-read");
        routes.MapPost("/tables/{id}/moves", (string id, HttpContext context, BoardTables tables) => ForGuestAsync(context,
            tables, true, async (guest, stop) =>
            {
                if (!IsKey(id)) return Missing();
                return await HttpApi.ReadJsonAsync(context, ReadMove) is { } request
                    ? Json(await tables.MoveAsync(guest.Id, id, request.Revision, request.MoveId, stop)) : Invalid();
            })).RequireRateLimiting("board-write");
        routes.MapPost("/tables/{id}/leave", (string id, HttpContext context, BoardTables tables) =>
            EndAsync(id, context, tables, resign: false)).RequireRateLimiting("board-write");
        routes.MapPost("/tables/{id}/resign", (string id, HttpContext context, BoardTables tables) =>
            EndAsync(id, context, tables, resign: true)).RequireRateLimiting("board-write");
        routes.MapGet("/resume", (HttpContext context, BoardTables tables) => ForGuestAsync(context, tables, false,
            async (guest, stop) => context.Request.Query["game"] is { Count: 1 } game && Wire.IsLocator(game[0])
                ? Json(new { tableId = await tables.ResumeAsync(guest.Id, game[0]!, stop) }) : Invalid()))
            .RequireRateLimiting("board-read");
    }

    private static Task<IResult> EndAsync(string id, HttpContext context, BoardTables tables, bool resign) =>
        ForGuestAsync(context, tables, true, async (guest, stop) =>
        {
            if (!IsKey(id)) return Missing();
            return await HttpApi.ReadJsonAsync(context, ReadRevision) is { } request
                ? Json(await tables.EndAsync(guest.Id, id, request.Revision, resign, stop)) : Invalid();
        });

    private static async Task<IResult> ForGuestAsync(HttpContext context, BoardTables tables, bool write,
        Func<GuestRow, CancellationToken, Task<IResult>> action)
    {
        if (CookieGuest(context) is not { } id) return HttpApi.Error(401, "not_authenticated");
        if (write && !context.RequestServices.GetRequiredService<ServerOptions>().IsSameOrigin(context))
            return HttpApi.Error(403, "forbidden");
        try
        {
            var stop = context.RequestAborted;
            return await tables.GuestAsync(id, stop) is { } guest
                ? await action(guest, stop) : HttpApi.Error(401, "not_authenticated");
        }
        catch (BoardFault fault)
        {
            return HttpApi.Error(fault.Status, fault.Code);
        }
        catch (Exception ex) when (Rooms.Unavailable(ex))
        {
            return HttpApi.Error(429, "busy");
        }
    }

    public static RateLimitPartition<string> PerGuest(HttpContext context, int guestLimit, int addressLimit)
    {
        var guest = CookieGuest(context);
        var key = guest is { } id ? $"guest:{id:N}" : $"address:{context.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = guest.HasValue ? guestLimit : addressLimit,
            Window = TimeSpan.FromMinutes(1), AutoReplenishment = true, QueueLimit = 0
        });
    }

    private static Guid? CookieGuest(HttpContext context)
    {
        if (context.Items.TryGetValue(CookieGuestKey, out var stored)) return (Guid?)stored;
        var guest = context.RequestServices.GetRequiredService<ProtectedIdentity>().ReadGuest(context);
        context.Items[CookieGuestKey] = guest;
        return guest;
    }

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

    private static string? ReadInvite(JsonElement body)
    {
        var invite = body.Get("invite").AsString();
        return Wire.Fields(body, "invite") && invite is { Length: 32 } && invite.All(char.IsAsciiHexDigit)
            ? invite.ToLowerInvariant() : null;
    }

    private static MoveRequest? ReadMove(JsonElement body)
    {
        var moveId = body.Get("moveId").AsString();
        return Wire.Fields(body, "moveId", "revision") && Wire.Revision(body.Get("revision"), out var revision)
            && moveId is { Length: > 0 and <= 1024 } && !moveId.Any(char.IsControl)
            ? new(revision, moveId) : null;
    }

    private static RevisionRequest? ReadRevision(JsonElement body) =>
        Wire.Fields(body, "revision") && Wire.Revision(body.Get("revision"), out var revision) ? new(revision) : null;

    private static bool IsKey(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);
    private static IResult Missing() => HttpApi.Error(404, "room_not_found");
    private static IResult Invalid() => HttpApi.Error(400, "invalid_request");
    private static IResult Json(object value) => Results.Json(value, Wire.Json);
}
