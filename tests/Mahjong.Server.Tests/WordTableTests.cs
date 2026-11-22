using System.Net;
using System.Text.Json;
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
        Assert.Equal(new[] { 6, 6 }, Position(joined).GetProperty("rackSizes").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(50, Position(initial).GetProperty("bagCount").GetInt32());

        var actions = path + "/actions";
        await Error(await peer.Request(HttpMethod.Post, actions, Pass(1)), HttpStatusCode.BadRequest, "not_your_turn");
        var passed = await Post(host, actions, Pass(1));
        Assert.Equal("Seat 1 passed.", Position(passed).Text("lastMove"));
        Assert.Equal(2, Revision(passed));
        await Error(await peer.Request(HttpMethod.Post, actions, new
        {
            revision = 2, action = new { kind = "place", placements = new[] { new { row = 11, column = 5, tileId = peerTiles[0] } } }
        }), HttpStatusCode.BadRequest, "invalid_request");
        await Error(await peer.Request(HttpMethod.Post, actions, new
        {
            revision = 2, action = new { kind = "place", placements = new[] { new { row = 5, column = 5, tileId = peerTiles[0] } } }
        }), HttpStatusCode.BadRequest, "illegal_action");
        await Error(await peer.Request(HttpMethod.Post, actions, Exchange(2, hostTiles[0])),
            HttpStatusCode.BadRequest, "illegal_action");
        var traded = await Post(peer, actions, Exchange(2, peerTiles[0]));
        var after = AssertPrivateView(traded);
        Assert.DoesNotContain(peerTiles[0], after);
        Assert.Empty(after.Intersect(hostTiles));
        Assert.Equal("Seat 2 exchanged 1 tile.", Position(traded).Text("lastMove"));
        Assert.Equal(50, Position(traded).GetProperty("bagCount").GetInt32());
        Assert.Equal(hostTiles, AssertPrivateView(await Read(host, path)));

        await server.StopAsync();
        await server.StartAsync();
        Assert.Equal(id, (await Read(peer, Api + "/resume")).Text("tableId"));
        Assert.True(JsonElement.DeepEquals(traded, await Read(peer, path)));
        await Error(await host.Request(HttpMethod.Post, actions, Pass(2)), HttpStatusCode.Conflict, "stale_room");
        var again = await Post(host, actions, Pass(3));
        Assert.Equal(4, Revision(again));
        var ended = await Post(peer, actions, Pass(4));
        Assert.Equal("finished", ended.Text("status"));
        Assert.Equal(JsonValueKind.Null, Position(ended).GetProperty("turn").ValueKind);
        Assert.Equal("Four turns without a placement.", Position(ended).Text("detail"));
        Assert.All(Position(ended).GetProperty("scores").EnumerateArray(), score => Assert.True(score.GetInt32() < 0));
        Assert.Null((await Read(host, Api + "/resume")).Text("tableId"));
        await Error(await host.Request(HttpMethod.Post, path + "/leave", new { revision = 5 }),
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
            Assert.Contains(cells, cell => cell.GetProperty("row").GetInt32() == 5 && cell.GetProperty("column").GetInt32() == 5);
            Assert.Equal(50 - cells.Length, Position(played).GetProperty("bagCount").GetInt32());
        }
        else
            Assert.Equal("Seat 2 passed.", Position(played).Text("lastMove"));

        var closed = await Post(host, path + "/leave", new { revision = 1 });
        Assert.Equal("closed", closed.Text("status"));
        Assert.Equal("left", closed.Text("endReason"));
        Assert.Equal(2, Revision(closed));
        Assert.Equal(JsonValueKind.Null, Position(closed).GetProperty("turn").ValueKind);
        Assert.True(JsonElement.DeepEquals(Position(played).GetProperty("board"), Position(closed).GetProperty("board")));
        Assert.Null((await Read(host, Api + "/resume")).Text("tableId"));
    }

    private static object Pass(long revision) => new { revision, action = new { kind = "pass" } };
    private static object Exchange(long revision, int id) => new { revision, action = new { kind = "exchange", exchange = new[] { id } } };
    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();
    private static async Task<JsonElement> Post(User guest, string path, object body) => await Ok(await guest.Request(HttpMethod.Post, path, body));
    private static async Task<JsonElement> Read(User guest, string path) => await Ok(await guest.Request(HttpMethod.Get, path));

    private static int[] AssertPrivateView(JsonElement table)
    {
        var position = Position(table);
        Assert.Equal(new[] { "bagCount", "board", "columns", "detail", "lastMove", "rack", "rackSizes", "rows", "scores",
                "status", "turn", "winners" },
            position.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        Assert.False(table.TryGetProperty("checkpoint", out _));
        var ids = position.GetProperty("rack").EnumerateArray().Select(tile => tile.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal(6, ids.Distinct().Count());
        return ids;
    }
}
