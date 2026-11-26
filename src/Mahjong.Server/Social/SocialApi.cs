using System.Text.Json;
using Mahjong.Server.Seated;
using static Mahjong.Server.PortalApi;

namespace Mahjong.Server.Social;

internal sealed record SocialPulse(string TabId, bool Active, bool Busy, bool? Available);
internal sealed record SocialTarget(string Game, string TableId)
{
    public bool Mahjong => Game == "mahjong";
    public bool Board => Game is "chess" or "go" or "reversi" or "checkers" or "chinese-checkers" or "xiangqi" or "gomoku";
    public SeatedRules? Seated => Game switch
    {
        "holdem" => PokerRules.Game,
        "word-tiles" => WordsRules.Game,
        "farkle" => FarkleRules.Game,
        "backgammon" => BackgammonRules.Game,
        "big-two" => BigTwoRules.Game,
        "koi-koi" => KoiKoiRules.Game,
        _ => null
    };
    public bool Supported => Mahjong || Board || Seated is not null;
}

internal static class SocialApi
{
    public const string Rate = "social-write";
    private sealed record Offer(string PlayerId, SocialTarget Target);

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/social").RequireRateLimiting(Rate);
        routes.MapPost("/presence", (HttpContext context, SocialService social) => ForGuestAsync(context, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, ReadPulse) is { } pulse
                ? Json(await social.PulseAsync(guest.Id, pulse, stop)) : Invalid()));
        routes.MapPost("/invites", (HttpContext context, SocialService social) => ForGuestAsync(context, true,
            async (guest, stop) => await HttpApi.ReadJsonAsync(context, ReadOffer) is { } offer
                ? Json(new { id = await social.SendAsync(guest.Id, offer.PlayerId, offer.Target, stop) }) : Invalid()));
        routes.MapPost("/invites/{id}/accept", (string id, HttpContext context, SocialService social) =>
            ForGuestAsync(context, true, async (guest, stop) => await EmptyAsync(context)
                ? Json(await social.AcceptAsync(guest, id, stop)) : Invalid()));
        foreach (var action in new[] { "decline", "revoke", "block" })
            routes.MapPost("/invites/{id}/" + action, (string id, HttpContext context, SocialService social) =>
                ForGuestAsync(context, true, async (guest, stop) =>
                {
                    if (!await EmptyAsync(context)) return Invalid();
                    await social.ResolveAsync(guest.Id, id, action, stop);
                    return Json(new { ok = true });
                }));
    }

    private static SocialPulse? ReadPulse(JsonElement body)
    {
        if (!Wire.Fields(body, "tabId", "active", "busy", "available")
            || body.Get("tabId").AsString() is not { Length: > 0 and <= 128 } tab
            || !tab.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            || !body.Get("active").TryFlag(out var active) || !body.Get("busy").TryFlag(out var busy))
            return null;
        var pref = body.Get("available");
        return pref.ValueKind == JsonValueKind.Undefined ? new(tab, active, busy, null)
            : pref.TryFlag(out var available) ? new(tab, active, busy, available) : null;
    }

    private static Offer? ReadOffer(JsonElement body)
    {
        if (!Wire.Fields(body, "playerId", "game", "tableId")
            || body.Get("playerId").AsString() is not { Length: > 0 and <= 128 } playerId
            || body.Get("game").AsString() is not { } game
            || body.Get("tableId").AsString() is not { Length: > 0 and <= 128 } tableId)
            return null;
        var target = new SocialTarget(game, tableId);
        return target.Supported ? new(playerId, target) : null;
    }

    private static async Task<bool> EmptyAsync(HttpContext context) =>
        await HttpApi.ReadJsonAsync(context, body => Wire.Fields(body) ? "" : null) is not null;
}
