using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class WordTableTests
{
    private const string Api = "/portal/api/words";

    [Fact]
    public async Task PrivateRacksStayScopedAcrossExchangeRestartAndSettlement()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Host");
        var peer = await Guest(server, "Peer");
        var stranger = await Guest(server, "Stranger");
        await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = 3, bots = true }),
            HttpStatusCode.BadRequest, "invalid_request");
        var waiting = await Post(host, Api + "/tables", new { players = 2, bots = false });
        var id = waiting.Text("id");
        var path = Api + "/tables/" + id;
        var invite = waiting.Text("invite");
        Assert.Equal("waiting", waiting.Text("status"));
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("position").ValueKind);
        await Error(await stranger.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        await Error(await peer.Request(HttpMethod.Post, "/portal/api/poker/join", new { invite }),
            HttpStatusCode.NotFound, "room_not_found");
        var joined = await Post(peer, Api + "/join", new { invite });
        var initial = await Read(host, path);
        Assert.Equal("playing", initial.Text("status"));
        Assert.Equal(0, initial.GetProperty("seat").GetInt32());
        Assert.Equal(1, joined.GetProperty("seat").GetInt32());
        Assert.Equal(1, Revision(initial));
        Assert.Equal("Peer", initial.GetProperty("seats")[1].Text("name"));
        Assert.True(JsonElement.DeepEquals(joined, await Post(peer, Api + "/join", new { invite })));
        await Error(await host.Request(HttpMethod.Get, "/portal/api/poker/tables/" + id),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await host.Request(HttpMethod.Post, path + "/next", new { revision = 1 }),
            HttpStatusCode.NotFound, "invalid_request");
        var hostTiles = AssertPrivateView(initial);
        var peerTiles = AssertPrivateView(joined);
        Assert.Empty(hostTiles.Intersect(peerTiles));
        Assert.Empty(Position(initial).GetProperty("board").EnumerateArray());
        Assert.Equal(new[] { 7, 7 }, Position(joined).GetProperty("rackSizes").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(86, Number(Position(initial), "bagCount"));
        Assert.Equal(7, Number(Position(initial), "exchangeLimit"));
        Assert.Equal(0, Number(Position(joined), "exchangeLimit"));

        var actions = path + "/actions";
        await Error(await peer.Request(HttpMethod.Post, actions, Pass(1)), HttpStatusCode.BadRequest, "not_your_turn");
        var passed = await Post(host, actions, Pass(1));
        Assert.Equal("Seat 1 passed.", Position(passed).Text("lastMove"));
        Assert.Equal(2, Revision(passed));
        await Error(await peer.Request(HttpMethod.Post, actions, new
        {
            revision = 2, action = new { kind = "place", placements = new[] { new { row = 15, column = 7, tileId = peerTiles[0] } } }
        }), HttpStatusCode.BadRequest, "invalid_request");
        await Error(await peer.Request(HttpMethod.Post, actions, new
        {
            revision = 2, action = new { kind = "place", placements = new[] { new { row = 14, column = 7, tileId = peerTiles[0] } } }
        }), HttpStatusCode.BadRequest, "illegal_action");
        await Error(await peer.Request(HttpMethod.Post, actions, Exchange(2, hostTiles[0])),
            HttpStatusCode.BadRequest, "illegal_action");
        await Error(await peer.Request(HttpMethod.Post, actions, Exchange(2, [.. peerTiles, peerTiles[0]])),
            HttpStatusCode.BadRequest, "invalid_request");
        var traded = await Post(peer, actions, Exchange(2, peerTiles));
        var after = AssertPrivateView(traded);
        Assert.Empty(peerTiles.Intersect(after));
        Assert.Empty(after.Intersect(hostTiles));
        Assert.Equal("Seat 2 exchanged 7 tiles.", Position(traded).Text("lastMove"));
        Assert.Equal(86, Number(Position(traded), "bagCount"));
        Assert.Equal(0, Number(Position(traded), "exchangeLimit"));
        Assert.Equal(hostTiles, AssertPrivateView(await Read(host, path)));

        await server.StopAsync();
        await server.StartAsync();
        Assert.Equal(id, (await Read(peer, Api + "/resume")).Text("tableId"));
        Assert.True(JsonElement.DeepEquals(traded, await Read(peer, path)));
        await Error(await host.Request(HttpMethod.Post, actions, Pass(2)), HttpStatusCode.Conflict, "stale_room");
        var again = await Post(host, actions, Pass(3));
        Assert.Equal(4, Revision(again));
        Assert.Equal("playing", (await Post(peer, actions, Pass(4))).Text("status"));
        Assert.Equal("playing", (await Post(host, actions, Pass(5))).Text("status"));
        var ended = await Post(peer, actions, Pass(6));
        Assert.Equal("finished", ended.Text("status"));
        Assert.Equal(JsonValueKind.Null, Position(ended).GetProperty("turn").ValueKind);
        Assert.Equal("Six consecutive scoreless turns.", Position(ended).Text("detail"));
        Assert.Equal(0, Number(Position(ended), "exchangeLimit"));
        Assert.All(Position(ended).GetProperty("scores").EnumerateArray(), score => Assert.True(score.GetInt32() < 0));
        Assert.Null((await Read(host, Api + "/resume")).Text("tableId"));
        await Error(await host.Request(HttpMethod.Post, path + "/leave", new { revision = 7 }),
            HttpStatusCode.BadRequest, "game_finished");
    }

    [Fact]
    public async Task AHumanPassCommitsTheBotReplyInTheSameRevision()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Human");
        var initial = await Post(host, Api + "/tables", new { players = 2, bots = true });
        var path = Api + "/tables/" + initial.Text("id");
        Assert.True(initial.GetProperty("seats")[1].GetProperty("bot").GetBoolean());
        Assert.Null(initial.Text("invite"));
        Assert.Equal(0, Position(initial).GetProperty("turn").GetInt32());
        var before = AssertPrivateView(initial);

        var played = await Post(host, path + "/actions", Pass(0));
        Assert.Equal(1, Revision(played));
        Assert.Equal(0, Position(played).GetProperty("turn").GetInt32());
        Assert.Equal(before, AssertPrivateView(played));
        Assert.StartsWith("Seat 2 ", Position(played).Text("lastMove"), StringComparison.Ordinal);
        var cells = Position(played).GetProperty("board").EnumerateArray().ToArray();
        if (cells.Length != 0)
        {
            Assert.All(cells, cell => Assert.Equal(1, cell.GetProperty("seat").GetInt32()));
            Assert.Contains(cells, cell => Number(cell, "row") == 7 && Number(cell, "column") == 7);
            Assert.Equal(86 - cells.Length, Number(Position(played), "bagCount"));
        }
        else
            Assert.Equal("Seat 2 passed.", Position(played).Text("lastMove"));

        var closed = await Post(host, path + "/leave", new { revision = 1 });
        Assert.Equal("closed", closed.Text("status"));
        Assert.Equal("left", closed.Text("endReason"));
        Assert.Equal(2, Revision(closed));
        Assert.Equal(JsonValueKind.Null, Position(closed).GetProperty("turn").ValueKind);
        Assert.Equal(0, Number(Position(closed), "exchangeLimit"));
        Assert.True(JsonElement.DeepEquals(Position(played).GetProperty("board"), Position(closed).GetProperty("board")));
        Assert.Null((await Read(host, Api + "/resume")).Text("tableId"));
    }

    [Fact]
    public async Task ClassicBlankAndSevenTilePremiumPlayPersistAcrossHTTPRestore()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Cabinet");
        var peer = await Guest(server, "Visitor");
        var waiting = await Post(host, Api + "/tables", new { players = 2, bots = false });
        var id = waiting.Text("id")!;
        var path = Api + "/tables/" + id;
        var actions = path + "/actions";
        await Post(peer, Api + "/join", new { invite = waiting.Text("invite") });

        int[][] racks = [[12, 1, 10, 99, 55, 19, 81], [68, 61, 71, 77, 87, 15, 48]];
        await SeedCheckpoint(server, id, JsonSerializer.Serialize(new
        {
            version = 2, turn = 0, racks, bag = Enumerable.Range(1, 100).Except(racks.SelectMany(rack => rack)),
            board = Array.Empty<int>(), scores = new[] { 0, 0 }, dryTurns = 0, last = (object?)null
        }));
        var before = await Read(host, path);
        var opponent = await Read(peer, path);
        var ownIds = AssertPrivateView(before);
        var rivalIds = AssertPrivateView(opponent);
        Assert.Equal("CAB?NET", string.Concat(Position(before).GetProperty("rack").EnumerateArray().Select(tile => tile.Text("letter"))));
        Assert.Empty(ownIds.Intersect(rivalIds));

        foreach (var invalidLetter in new object?[] { null, "i", "II", "É", 3 })
            await Error(await host.Request(HttpMethod.Post, actions, new
            {
                revision = 1, action = new { kind = "place", placements = new[] { new { row = 7, column = 7, tileId = ownIds[3], letter = invalidLetter } } }
            }), HttpStatusCode.BadRequest, "invalid_request");

        object Placement(int offset, bool assign = true) => offset == 3 && assign
            ? new { row = 7, column = 4 + offset, tileId = ownIds[offset], letter = "I" }
            : new { row = 7, column = 4 + offset, tileId = ownIds[offset] };
        object Place(object[] placements) => new { revision = 1, action = new { kind = "place", placements } };
        var seven = Enumerable.Range(0, 7).Select(offset => Placement(offset)).ToArray();
        await Error(await host.Request(HttpMethod.Post, actions, Place([.. seven, seven[0]])), HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, actions, Place([])), HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, actions, Place(Enumerable.Range(0, 7).Select(offset => Placement(offset, false)).ToArray())),
            HttpStatusCode.BadRequest, "illegal_action");
        await Error(await host.Request(HttpMethod.Post, actions, Place(
            [new { row = 7, column = 4, tileId = ownIds[0], letter = "C" }, .. seven[1..]])), HttpStatusCode.BadRequest, "illegal_action");
        await Error(await host.Request(HttpMethod.Post, actions, Place(
            [.. seven[..3], new { row = 7, column = 7, tileId = rivalIds[0], letter = "I" }, .. seven[4..]])), HttpStatusCode.BadRequest, "illegal_action");

        var played = await Post(host, actions, Place(seven));
        var position = Position(played);
        var board = position.GetProperty("board").EnumerateArray().ToArray();
        Assert.Equal(2, Revision(played));
        Assert.Equal("Seat 1 played CABINET (+122).", position.Text("lastMove"));
        Assert.Equal(122, position.GetProperty("scores")[0].GetInt32()); // 9×2×2×2 + 50
        Assert.Equal(79, Number(position, "bagCount"));
        Assert.Equal(0, Number(position, "exchangeLimit"));
        Assert.Equal(ownIds, board.Select(cell => Number(cell.GetProperty("tile"), "id")));
        var blank = Assert.Single(board, cell => cell.GetProperty("tile").GetProperty("blank").GetBoolean());
        Assert.Equal((7, 7, "I", 0), (Number(blank, "row"), Number(blank, "column"), blank.GetProperty("tile").Text("letter"),
            Number(blank.GetProperty("tile"), "points")));
        Assert.Equal("double-word", Assert.Single(position.GetProperty("premiums").EnumerateArray(),
            premium => Number(premium, "row") == 7 && Number(premium, "column") == 7).Text("kind"));
        Assert.Empty(AssertPrivateView(played).Intersect([.. ownIds, .. rivalIds]));

        await server.StopAsync();
        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(played, await Read(host, path)));
        Assert.Equal(id, (await Read(peer, Api + "/resume")).Text("tableId"));
        var peerAfter = await Read(peer, path);
        Assert.Equal(rivalIds, AssertPrivateView(peerAfter));
        Assert.Equal(7, Number(Position(peerAfter), "exchangeLimit"));
        Assert.True(JsonElement.DeepEquals(position.GetProperty("board"), Position(peerAfter).GetProperty("board")));
    }

    [Fact]
    public async Task AnExistingVersionOneTableStillResumesAndScoresQuickInTheSameDatabase()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Quick Host");
        var peer = await Guest(server, "Quick Peer");
        var waiting = await Post(host, Api + "/tables", new { players = 2, bots = false });
        var id = waiting.Text("id")!;
        var path = Api + "/tables/" + id;
        await Post(peer, Api + "/join", new { invite = waiting.Text("invite") });
        const string fixture = """
            {"version":1,"turn":1,"racks":[[12,43,47,18,37,59],[40,22,36,33,10,6]],"bag":[2,3,4,5,7,9,11,13,14,15,16,17,19,20,21,23,24,25,26,27,28,29,30,31,32,34,35,38,39,41,42,44,45,46,48,49,50,52,53,54,55,56,57,58,60,61,62],"board":[{"row":5,"column":4,"tileId":8,"seat":0},{"row":5,"column":5,"tileId":1,"seat":0},{"row":5,"column":6,"tileId":51,"seat":0}],"scores":[4,0],"dryTurns":0,"last":{"kind":"place","count":3,"words":["CAT"],"points":4}}
            """;
        await SeedCheckpoint(server, id, fixture);
        Assert.Equal(id, (await Read(peer, Api + "/resume")).Text("tableId"));
        var seat = await Read(peer, path);
        var position = Position(seat);
        var ids = AssertPrivateView(seat, "quick");
        Assert.Equal(new[] { 40, 22, 36, 33, 10, 6 }, ids);
        Assert.Equal(3, Number(position, "exchangeLimit"));
        Assert.Equal(47, Number(position, "bagCount"));
        Assert.Empty(ids.Intersect(AssertPrivateView(await Read(host, path), "quick")));

        await Error(await peer.Request(HttpMethod.Post, path + "/actions", Exchange(1, ids[..4])),
            HttpStatusCode.BadRequest, "illegal_action");
        var played = await Post(peer, path + "/actions", new
        {
            revision = 1,
            action = new { kind = "place", placements = new[] { new { row = 4, column = 5, tileId = ids[0] }, new { row = 4, column = 6, tileId = ids[1] } } }
        });
        Assert.Equal("Seat 2 played PI, PA, IT (+12).", Position(played).Text("lastMove"));
        Assert.Equal(12, Position(played).GetProperty("scores")[1].GetInt32());
        await server.StopAsync();
        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(played, await Read(peer, path)));
        AssertPrivateView(await Read(peer, path), "quick");
    }

    private static async Task SeedCheckpoint(Kestrel server, string id, string checkpoint)
    {
        await server.StopAsync();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(server.DataDirectory, "mahjong.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        };
        await using (var database = new SqliteConnection(connectionString.ToString()))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "UPDATE SeatedTables SET Checkpoint=$checkpoint WHERE Id=$id AND Game='words'";
            command.Parameters.AddWithValue("$checkpoint", checkpoint);
            command.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await server.StartAsync();
    }

    private static object Pass(long revision) => new { revision, action = new { kind = "pass" } };
    private static object Exchange(long revision, params int[] ids) => new { revision, action = new { kind = "exchange", exchange = ids } };
    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static int Number(JsonElement node, string key) => node.GetProperty(key).GetInt32();
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();
    private static async Task<JsonElement> Post(User guest, string path, object body) => await Ok(await guest.Request(HttpMethod.Post, path, body));
    private static async Task<JsonElement> Read(User guest, string path) => await Ok(await guest.Request(HttpMethod.Get, path));

    private static int[] AssertPrivateView(JsonElement table, string variant = "classic")
    {
        var position = Position(table);
        Assert.Equal(new[] { "bagCount", "board", "columns", "detail", "exchangeLimit", "lastMove", "premiums", "rack",
                "rackSize", "rackSizes", "rows", "scores", "status", "turn", "variant", "winners" },
            position.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        Assert.False(table.TryGetProperty("checkpoint", out _));
        var size = variant == "classic" ? 15 : 11;
        var rackSize = variant == "classic" ? 7 : 6;
        Assert.Equal((variant, size, size, rackSize),
            (position.Text("variant"), Number(position, "rows"), Number(position, "columns"), Number(position, "rackSize")));
        var premiums = position.GetProperty("premiums").EnumerateArray();
        if (variant == "classic")
            Assert.Contains(premiums, cell => Number(cell, "row") == 7 && Number(cell, "column") == 7 && cell.Text("kind") == "double-word");
        else
            Assert.Empty(premiums);
        Assert.All(position.GetProperty("rack").EnumerateArray(), tile =>
        {
            Assert.Equal(new[] { "blank", "id", "letter", "points" }, tile.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
            if (tile.GetProperty("blank").GetBoolean())
                Assert.Equal(("?", 0), (tile.Text("letter"), Number(tile, "points")));
        });
        var ids = position.GetProperty("rack").EnumerateArray().Select(tile => tile.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal(rackSize, ids.Length);
        Assert.Equal(rackSize, ids.Distinct().Count());
        return ids;
    }
}
