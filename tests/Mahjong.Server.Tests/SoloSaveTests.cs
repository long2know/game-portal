using System.Net;
using System.Text.Json;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class SoloSaveTests
{
    private const string Solitaire = "/portal/api/solo/solitaire";
    private const string Sudoku = "/portal/api/solo/sudoku";

    [Fact]
    public async Task SolitaireSaveIsPrivateDurableAndRejectsMovesAgainstARedeal()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Owner");
        var peer = await Guest(server, "Independent");
        Assert.Equal(JsonValueKind.Null, (await Saved(host, Solitaire)).ValueKind);
        await Error(await new User(server).Request(HttpMethod.Get, Solitaire), HttpStatusCode.Unauthorized, "not_authenticated");
        await Error(await host.Request(HttpMethod.Post, Solitaire, new { variant = "standard" }),
            HttpStatusCode.BadRequest, "invalid_request");
        var first = await Post(host, Solitaire, new { variant = "draw-one" });
        AssertTable(first, "solitaire", "draw-one", 0);
        AssertSolitaireView(first, 24, 0);
        Assert.Equal(JsonValueKind.Null, (await Saved(peer, Solitaire)).ValueKind);
        var moves = Solitaire + "/moves";
        await Error(await peer.Request(HttpMethod.Post, moves, Draw(0)), HttpStatusCode.NotFound, "room_not_found");
        await Error(await host.Request(HttpMethod.Post, moves, Draw(0), origin: "https://not-the-game.example"),
            HttpStatusCode.Forbidden, "forbidden");
        await Error(await host.Request(HttpMethod.Post, moves, new { revision = 0, action = new { kind = "draw", count = 1 } }),
            HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, moves, new { revision = 0, action = new { kind = "move", from = "t0", to = "t0", count = 1 } }),
            HttpStatusCode.BadRequest, "illegal_action");
        var replies = await Task.WhenAll(host.Request(HttpMethod.Post, moves, Draw(0)), host.Request(HttpMethod.Post, moves, Draw(0)));
        var moved = await Ok(Assert.Single(replies, r => r.IsSuccessStatusCode));
        await Error(Assert.Single(replies, r => !r.IsSuccessStatusCode), HttpStatusCode.Conflict, "stale_room");
        AssertTable(moved, "solitaire", "draw-one", 1);
        AssertSolitaireView(moved, 23, 1);
        Assert.True(Position(moved).At("waste", "faceUp").GetBoolean());
        Assert.Equal(1, Position(moved).GetProperty("moves").GetInt32());

        await server.StopAsync();
        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(moved, await Saved(host, Solitaire)));
        var fresh = await Post(host, Solitaire, new { variant = "draw-one" });
        AssertTable(fresh, "solitaire", "draw-one", 2);
        AssertSolitaireView(fresh, 24, 0);
        Assert.Equal(0, Position(fresh).GetProperty("moves").GetInt32());
        await Error(await host.Request(HttpMethod.Post, moves, Draw(1)), HttpStatusCode.Conflict, "stale_room");
        Assert.True(JsonElement.DeepEquals(fresh, await Saved(host, Solitaire)));
        var continued = await Post(host, moves, Draw(2));
        AssertTable(continued, "solitaire", "draw-one", 3);
        AssertSolitaireView(continued, 23, 1);
    }

    [Fact]
    public async Task SudokuHintsOnlyOneCellAndCannotRevealOrOverwriteOtherSaves()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Sudoku player");
        var peer = await Guest(server, "Other player");
        var solitaire = await Post(host, Solitaire, new { variant = "draw-one" });
        Assert.Equal(JsonValueKind.Null, (await Saved(host, Sudoku)).ValueKind);
        await Error(await host.Request(HttpMethod.Get, "/portal/api/solo/unsupported"),
            HttpStatusCode.BadRequest, "invalid_request");
        var first = await Post(host, Sudoku, new { variant = "hard" });
        AssertTable(first, "sudoku", "hard", 0);
        Assert.Equal(JsonValueKind.Null, (await Saved(peer, Sudoku)).ValueKind);
        var given = SudokuCells(first, "given");
        Assert.Equal(given, SudokuCells(first, "values"));
        Assert.Equal(31, given.Count(value => value != 0));
        var fixedCell = Array.FindIndex(given, value => value != 0);
        var emptyCell = Array.FindIndex(given, value => value == 0);
        var moves = Sudoku + "/moves";
        await Error(await host.Request(HttpMethod.Post, Sudoku, new { variant = "easy", seed = 123 }),
            HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, moves, Set(0, fixedCell, 0)),
            HttpStatusCode.BadRequest, "illegal_action");
        await Error(await host.Request(HttpMethod.Post, moves, Draw(0)), HttpStatusCode.BadRequest, "invalid_request");
        var set = await Post(host, moves, Set(0, emptyCell, 4));
        AssertTable(set, "sudoku", "hard", 1);
        Assert.Equal(4, SudokuCells(set, "values")[emptyCell]);
        await Error(await host.Request(HttpMethod.Post, moves, new { revision = 1, action = new { kind = "hint", index = fixedCell } }),
            HttpStatusCode.BadRequest, "illegal_action");
        var hint = await Post(host, moves, new { revision = 1, action = new { kind = "hint" } });
        AssertTable(hint, "sudoku", "hard", 2);
        Assert.Equal(new[] { "conflicts", "finished", "given", "moves", "values" },
            Position(hint).EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
        var hinted = SudokuCells(hint, "values");
        var values = SudokuCells(set, "values");
        var changed = Assert.Single(Enumerable.Range(0, 81), i => values[i] != hinted[i]);
        Assert.Equal(0, given[changed]);
        Assert.InRange(hinted[changed], 1, 9);
        Assert.Equal(given, SudokuCells(hint, "given"));
        Assert.True(JsonElement.DeepEquals(solitaire, await Saved(host, Solitaire)));
        Assert.Equal(JsonValueKind.Null, (await Saved(peer, Sudoku)).ValueKind);

        var redealt = await Post(host, Sudoku, new { variant = "easy" });
        AssertTable(redealt, "sudoku", "easy", 3);
        Assert.Equal(45, SudokuCells(redealt, "given").Count(value => value != 0));
        await Error(await host.Request(HttpMethod.Post, moves, Set(2, emptyCell, 1)),
            HttpStatusCode.Conflict, "stale_room");
        Assert.True(JsonElement.DeepEquals(redealt, await Saved(host, Sudoku)));
    }

    private static object Draw(long revision) => new { revision, action = new { kind = "draw" } };
    private static object Set(long revision, int index, int value) => new { revision, action = new { kind = "set", index, value } };
    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static int[] SudokuCells(JsonElement table, string name) =>
        Position(table).GetProperty(name).EnumerateArray().Select(e => e.GetInt32()).ToArray();

    private static async Task<JsonElement> Saved(User guest, string path) =>
        (await Ok(await guest.Request(HttpMethod.Get, path))).GetProperty("table");

    private static async Task<JsonElement> Post(User guest, string path, object body) =>
        await Ok(await guest.Request(HttpMethod.Post, path, body));

    private static void AssertTable(JsonElement table, string game, string variant, long revision)
    {
        Assert.Equal(new[] { "game", "position", "revision", "variant" },
            table.EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(game, table.Text("game"));
        Assert.Equal(variant, table.Text("variant"));
        Assert.Equal(revision, table.GetProperty("revision").GetInt64());
    }

    private static void AssertSolitaireView(JsonElement table, int stockCount, int wasteCount)
    {
        var position = Position(table);
        Assert.Equal(new[] { "finished", "foundations", "legalMoves", "moves", "stockCount", "tableau", "waste", "wasteCount" },
            position.EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(stockCount, position.GetProperty("stockCount").GetInt32());
        Assert.Equal(wasteCount, position.GetProperty("wasteCount").GetInt32());
        if (wasteCount == 0) Assert.Equal(JsonValueKind.Null, position.GetProperty("waste").ValueKind);
        var columns = position.GetProperty("tableau").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 7), columns.Select(column => column.GetArrayLength()));
        foreach (var column in columns)
        {
            var cards = column.EnumerateArray().ToArray();
            Assert.All(cards[..^1], card =>
            {
                Assert.False(card.GetProperty("faceUp").GetBoolean());
                Assert.Equal(JsonValueKind.Null, card.GetProperty("rank").ValueKind);
                Assert.Equal(JsonValueKind.Null, card.GetProperty("suit").ValueKind);
            });
            Assert.True(cards[^1].GetProperty("faceUp").GetBoolean());
            Assert.InRange(cards[^1].GetProperty("rank").GetInt32(), 1, 13);
        }
    }
}
