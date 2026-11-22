using System.Net;
using System.Text.Json;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class PokerTableTests
{
    private const string Api = "/portal/api/poker";

    [Fact]
    public async Task PrivateSeatsReconnectAndEitherMayDealTheNextHandOnce()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Button");
        var peer = await Guest(server, "Blind");
        var stranger = await Guest(server, "Stranger");
        var created = await Post(host, Api + "/tables", new { players = 2, bots = false });
        var id = created.Text("id");
        var path = Api + "/tables/" + id;
        var invite = created.Text("invite");
        Assert.Equal("waiting", created.Text("status"));
        Assert.Equal(0, Revision(created));
        Assert.Equal(JsonValueKind.Null, Position(created).ValueKind);
        Assert.NotEqual(id, invite);
        await Error(await stranger.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        await Error(await new User(server).Request(HttpMethod.Get, path), HttpStatusCode.Unauthorized, "not_authenticated");
        await Error(await host.Request(HttpMethod.Post, path + "/actions", Action(0, "fold")),
            HttpStatusCode.BadRequest, "room_not_started");

        var joins = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Post(peer, Api + "/join", new { invite })));
        var joined = joins[0];
        Assert.True(JsonElement.DeepEquals(joined, joins[1]));
        Assert.Equal("playing", joined.Text("status"));
        Assert.Equal(1, Revision(joined));
        Assert.Equal("Blind", joined.GetProperty("seats")[1].Text("name"));
        await Error(await stranger.Request(HttpMethod.Post, Api + "/join", new { invite }), HttpStatusCode.NotFound, "room_not_found");
        Assert.Equal(id, (await Read(peer, Api + "/resume")).Text("tableId"));
        var opening = await Read(host, path);
        AssertView(opening, 0, true);
        AssertView(joined, 1, false);
        Assert.Empty(Cards(opening).Intersect(Cards(joined)));
        Assert.Equal(0, Position(opening).GetProperty("street").GetInt32());
        Assert.Equal(15, Position(opening).GetProperty("potChips").GetInt32());
        Assert.Equal(5, Position(opening).At("legalActions", "callAmount").GetInt32());

        await Error(await host.Request(HttpMethod.Post, path + "/next", new { revision = 1 }),
            HttpStatusCode.BadRequest, "hand_in_progress");
        await Error(await peer.Request(HttpMethod.Post, path + "/actions", Action(1, "check")),
            HttpStatusCode.BadRequest, "not_your_turn");
        await Error(await host.Request(HttpMethod.Post, path + "/actions", Action(1, "call"), includeOrigin: false),
            HttpStatusCode.Forbidden, "forbidden");
        await Error(await host.Request(HttpMethod.Post, path + "/actions", new { revision = 1, seat = 1, action = new { kind = "call" } }),
            HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, path + "/actions", Action(1, "call", 5)),
            HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, path + "/actions", Action(1, "raise", 11)),
            HttpStatusCode.BadRequest, "illegal_action");

        var called = await Post(host, path + "/actions", Action(1, "call"));
        Assert.Equal(2, Revision(called));
        AssertView(called, 0, false);
        Assert.Equal(20, Position(called).GetProperty("potChips").GetInt32());
        var raised = await Post(peer, path + "/actions", Action(2, "raise", 20));
        Assert.Equal(30, Position(raised).GetProperty("potChips").GetInt32());
        Assert.Equal(20, Position(raised).GetProperty("seats")[1].GetProperty("streetCommitted").GetInt32());
        AssertSameCards(opening, called);
        AssertSameCards(joined, raised);
        var settled = await Post(host, path + "/actions", Action(3, "fold"));
        Assert.Equal("playing", settled.Text("status"));
        Assert.Equal(JsonValueKind.Null, Position(settled).GetProperty("matchWinnerSeat").ValueKind);
        var result = Position(settled).GetProperty("result");
        Assert.Equal(0, result.GetProperty("reason").GetInt32());
        Assert.Equal(20, Assert.Single(result.GetProperty("pots").EnumerateArray()).GetProperty("chips").GetInt32());
        Assert.Empty(result.GetProperty("showdownHands").EnumerateArray());
        await Error(await host.Request(HttpMethod.Post, path + "/actions", Action(4, "fold")),
            HttpStatusCode.BadRequest, "hand_finished");
        await Error(await stranger.Request(HttpMethod.Post, path + "/next", new { revision = 4 }),
            HttpStatusCode.NotFound, "room_not_found");

        await server.StopAsync();
        await server.StartAsync();
        Assert.Equal(id, (await Read(host, Api + "/resume")).Text("tableId"));
        Assert.True(JsonElement.DeepEquals(settled, await Read(host, path)));
        var reopened = await Read(peer, path);
        AssertView(reopened, 1, false);
        AssertSameCards(joined, reopened);
        await Error(await peer.Request(HttpMethod.Post, path + "/next", new { revision = 3 }),
            HttpStatusCode.Conflict, "stale_room");
        var requests = await Task.WhenAll(peer.Request(HttpMethod.Post, path + "/next", new { revision = 4 }),
            host.Request(HttpMethod.Post, path + "/next", new { revision = 4 }));
        var next = await Ok(Assert.Single(requests, r => r.IsSuccessStatusCode));
        await Error(Assert.Single(requests, r => !r.IsSuccessStatusCode), HttpStatusCode.Conflict, "stale_room");
        Assert.Equal(5, Revision(next));
        Assert.Equal(2, Position(next).GetProperty("handNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, Position(next).GetProperty("result").ValueKind);
        var closed = await Post(peer, path + "/leave", new { revision = 5 });
        Assert.Equal("closed", closed.Text("status"));
        Assert.Equal("left", closed.Text("endReason"));
        Assert.Equal(6, Revision(closed));
        AssertView(closed, 1, false);
        Assert.Equal(JsonValueKind.Null, Position(closed).GetProperty("actingSeat").ValueKind);
        Assert.Null((await Read(host, Api + "/resume")).Text("tableId"));
        Assert.Null((await Read(peer, Api + "/resume")).Text("tableId"));
        Assert.Equal("closed", (await Read(host, path)).Text("status"));
        await Error(await peer.Request(HttpMethod.Post, Api + "/join", new { invite }), HttpStatusCode.NotFound, "room_not_found");
    }

    [Fact]
    public async Task SeatsFillWithBoundedBotActionsAndCreateCannotSpecifySecretsOrStakes()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Human");
        foreach (var count in new[] { 1, 7 })
            await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = count, bots = true }),
                HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = 2, bots = true, seed = 25, chips = 100 }),
            HttpStatusCode.BadRequest, "invalid_request");
        Assert.Null((await Read(host, Api + "/resume")).Text("tableId"));

        var table = await Post(host, Api + "/tables", new { players = 6, bots = true });
        Assert.Equal("playing", table.Text("status"));
        Assert.Equal(0, Revision(table));
        Assert.Null(table.Text("invite"));
        var seats = table.GetProperty("seats").EnumerateArray().ToArray();
        Assert.Equal(6, seats.Length);
        Assert.False(seats[0].GetProperty("bot").GetBoolean());
        Assert.All(seats[1..], seat =>
        {
            Assert.True(seat.GetProperty("bot").GetBoolean());
            Assert.True(seat.GetProperty("occupied").GetBoolean());
        });
        AssertView(table, 0, true);
        Assert.Equal(0, Position(table).GetProperty("actingSeat").GetInt32());
        var engineRevision = Position(table).GetProperty("revision").GetInt64();
        Assert.True(engineRevision > 1);
        Assert.Equal(6000, Position(table).GetProperty("seats").EnumerateArray().Sum(s =>
            s.GetProperty("stack").GetInt32() + s.GetProperty("handCommitted").GetInt32()));
        var path = Api + "/tables/" + table.Text("id") + "/actions";
        await Error(await host.Request(HttpMethod.Post, path, Action(engineRevision, "fold")), HttpStatusCode.Conflict, "stale_room");
        var folded = await Post(host, path, Action(Revision(table), "fold"));
        Assert.Equal(1, Revision(folded));
        Assert.Equal(JsonValueKind.Object, Position(folded).GetProperty("result").ValueKind);
        Assert.True(Position(folded).GetProperty("seats")[0].GetProperty("folded").GetBoolean());
    }

    private static object Action(long revision, string kind, int? to = null) =>
        to is int total ? new { revision, action = new { kind, to = total } } : new { revision, action = new { kind } };

    private static async Task<JsonElement> Post(User guest, string path, object body) =>
        await Ok(await guest.Request(HttpMethod.Post, path, body));

    private static async Task<JsonElement> Read(User guest, string path) =>
        await Ok(await guest.Request(HttpMethod.Get, path));

    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();
    private static IEnumerable<(int Rank, int Suit)> Cards(JsonElement table) =>
        Position(table).GetProperty("ownCards").EnumerateArray()
            .Select(card => (card.GetProperty("rank").GetInt32(), card.GetProperty("suit").GetInt32()));

    private static void AssertSameCards(JsonElement before, JsonElement after) => Assert.Equal(Cards(before), Cards(after));

    private static void AssertView(JsonElement table, int seat, bool canAct)
    {
        var position = Position(table);
        Assert.Equal(seat, table.GetProperty("seat").GetInt32());
        Assert.Equal(seat, position.GetProperty("viewerSeat").GetInt32());
        Assert.Equal(canAct ? JsonValueKind.Object : JsonValueKind.Null, position.GetProperty("legalActions").ValueKind);
        Assert.Equal(new[]
        {
            "actingSeat", "bigBlind", "bigBlindSeat", "board", "buttonSeat", "currentBet", "handNumber", "legalActions",
            "matchWinnerSeat", "ownCards", "potChips", "result", "revision", "seats", "smallBlind", "smallBlindSeat",
            "street", "uncalledReturns", "viewerSeat"
        }, position.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, Cards(table).Distinct().Count());
        Assert.All(Cards(table), card =>
        {
            Assert.InRange(card.Rank, 2, 14);
            Assert.InRange(card.Suit, 0, 3);
        });
        Assert.All(position.GetProperty("seats").EnumerateArray(), s => Assert.Equal(new[]
        {
            "dealtIn", "folded", "handCommitted", "isAllIn", "seat", "stack", "streetCommitted"
        }, s.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
        Assert.False(table.TryGetProperty("checkpoint", out _));
    }
}
