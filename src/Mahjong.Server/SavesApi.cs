using Mahjong.Server.Board;
using Mahjong.Server.Seated;
using Microsoft.EntityFrameworkCore;
using static Mahjong.Server.PortalApi;

namespace Mahjong.Server;

internal static class SavesApi
{
    private sealed record SaveTarget(string Game, string? TableId, string? Variant, int? Players,
        string? BotDifficulty, bool CanStartFresh);

    private static readonly Dictionary<string, string> SeatedSlugs = new(StringComparer.Ordinal)
    {
        [PokerRules.Game.Game] = "holdem",
        [WordsRules.Game.Game] = "word-tiles",
        [FarkleRules.Game.Game] = "farkle",
        [BackgammonRules.Game.Game] = "backgammon",
        [BigTwoRules.Game.Game] = "big-two"
    };
    private static readonly string[] SeatedGames = [.. SeatedSlugs.Keys];

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/saves", (HttpContext context, IDbContextFactory<MahjongDb> factory, BoardRegistry boards,
            TimeProvider time) => ForOptionalGuestReadAsync(context, Json(new { saves = Array.Empty<SaveTarget>() }),
                async (guest, stop) => Json(new { saves = await ListAsync(factory, boards, time, guest.Id, stop) })))
            .RequireRateLimiting(Read);
    }

    private static async Task<SaveTarget[]> ListAsync(IDbContextFactory<MahjongDb> factory, BoardRegistry registry,
        TimeProvider time, Guid guest, CancellationToken stop)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var now = time.GetUtcNow().UtcTicks;
        var boardGames = registry.Definitions.Select(game => game.Id).ToArray();
        var boards = await BoardPersistence.ResumableMemberships(db, guest, now)
            .Where(m => boardGames.Contains(m.Table.Game))
            .Select(m => new
            {
                m.Table.Game, m.TableId, m.Table.Variant, m.Table.Players, m.Table.BotDifficulty,
                CanStartFresh = m.Seat == 0 && m.Table.Bots
            }).ToArrayAsync(stop);
        var seated = await SeatedPersistence.ResumableMemberships(db, guest, now)
            .Where(m => SeatedGames.Contains(m.Table.Game))
            .Select(m => new { m.Table.Game, m.TableId, m.Table.Players, CanStartFresh = m.Seat == 0 && m.Table.Bots })
            .ToArrayAsync(stop);
        var solo = await db.SoloSaves.AsNoTracking().Where(s => s.GuestId == guest && (s.Game == "solitaire" || s.Game == "sudoku"))
            .Select(s => new { s.Game, s.Variant }).ToArrayAsync(stop);

        var saves = boards.DistinctBy(table => table.Game)
            .Select(table => new SaveTarget(table.Game, table.TableId, table.Variant, table.Players,
                BoardTables.Difficulty(table.Game, table.CanStartFresh, table.BotDifficulty), table.CanStartFresh)).ToList();
        saves.AddRange(seated.DistinctBy(table => table.Game)
            .Select(table => new SaveTarget(SeatedSlugs[table.Game], table.TableId, null, table.Players, null, table.CanStartFresh)));
        saves.AddRange(solo.Select(save => new SaveTarget(save.Game, null, save.Variant, null, null, true)));
        if (await Rooms.ResumeTargetAsync(db, guest, now, stop) is { } room)
            saves.Add(new("mahjong", room.RoomId, null, null, null, room.CanStartFresh));
        return saves.OrderBy(save => save.Game, StringComparer.Ordinal).ToArray();
    }
}
