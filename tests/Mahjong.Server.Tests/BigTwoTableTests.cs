using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class BigTwoTableTests
{
    private const string Api = "/api/big-two";

    [Fact]
    public async Task BotTableCommitsOneRevisionPerHumanActionAndResumesUnchangedAfterRestart()
    {
        const string mount = "/game-portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var host = await Enter(server, mount, "Human");
        var started = await Post(host, Api + "/tables", new { players = 4, bots = true });
        var path = Api + "/tables/" + started.Text("id");
        Assert.Equal("playing", started.Text("status"));
        Assert.Equal(0, Revision(started));
        AssertView(started, 0, 0);
        Assert.Equal(13, OwnCards(started).Length);
        var seats = started.GetProperty("seats").EnumerateArray().ToArray();
        Assert.Equal(new[] { false, true, true, true }, seats.Select(seat => seat.GetProperty("bot").GetBoolean()));
        Assert.All(seats, seat => Assert.True(seat.GetProperty("occupied").GetBoolean()));

        var after = await TakeTurn(started);
        Assert.Equal("playing", after.Text("status"));
        AssertView(after, 0, 0);
        await server.StopAsync();
        await server.StartAsync();
        Assert.Equal(started.Text("id"), (await Read(host, Api + "/resume")).Text("tableId"));
        Assert.True(JsonElement.DeepEquals(after, await Read(host, path)));

        for (var turn = 0; turn < 52 && after.Text("status") == "playing"; turn++)
            after = await TakeTurn(after);
        Assert.Equal("finished", after.Text("status"));
        var winner = SeatNumber(Position(after), "winnerSeat");
        Assert.NotNull(winner);
        AssertView(after, 0, null, winner);
        Assert.Equal(0, Counts(after)[winner.Value]);
        Assert.Equal(winner.Value, Position(after).At("lastPlay", "seat").GetInt32());
        Assert.Null((await Read(host, Api + "/resume")).Text("tableId"));
        await Error(await host.Request(HttpMethod.Post, path + "/actions", Pass(Revision(after))),
            HttpStatusCode.BadRequest, "game_finished");
        await Error(await host.Request(HttpMethod.Post, path + "/next", new { revision = Revision(after) }),
            HttpStatusCode.NotFound, "invalid_request");

        async Task<JsonElement> TakeTurn(JsonElement before)
        {
            var cards = OwnCards(before);
            var masks = Masks(before);
            var mask = masks.OrderByDescending(mask => BitOperations.PopCount((uint)mask)).FirstOrDefault();
            var selected = Selected(cards, mask);
            if (mask == 0) Assert.True(Position(before).GetProperty("canPass").GetBoolean());
            var action = mask == 0 ? Pass(Revision(before)) : Play(Revision(before), selected);
            var after = await Post(host, path + "/actions", action);
            Assert.Equal(Revision(before) + 1, Revision(after));
            Assert.Equal(cards.Where(card => !selected.Contains(card.Id)), OwnCards(after));
            var beforeCounts = Counts(before);
            var afterCounts = Counts(after);
            for (var seat = 1; seat < 4; seat++)
                Assert.InRange(beforeCounts[seat] - afterCounts[seat], 0, 5);
            return after;
        }
    }

    [Fact]
    public async Task PrivateInviteStartsTheExactTableAndEachHumanGetsOnlyTheirDealsAndTurnOptions()
    {
        const string mount = "/portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var users = new[] { await Enter(server, mount, "Host"), await Enter(server, mount, "East"),
            await Enter(server, mount, "South"), await Enter(server, mount, "North") };
        var host = users[0];
        var recipient = users[3];
        var stranger = await Enter(server, mount, "Stranger");
        await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = 3, bots = false }),
            HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = 4, bots = true, seed = 12 }),
            HttpStatusCode.BadRequest, "invalid_request");

        var waiting = await Post(host, Api + "/tables", new { players = 4, bots = false });
        var id = waiting.Text("id");
        var path = Api + "/tables/" + id;
        var invite = waiting.Text("invite");
        Assert.Equal("waiting", waiting.Text("status"));
        Assert.Equal(0, Revision(waiting));
        Assert.Equal(JsonValueKind.Null, Position(waiting).ValueKind);
        Assert.Matches("^[a-f0-9]{32}$", invite);
        Assert.NotEqual(id, invite);
        await Error(await new User(server, mount).Request(HttpMethod.Get, path), HttpStatusCode.Unauthorized, "not_authenticated");
        await Error(await host.Request(HttpMethod.Post, path + "/actions", Pass(0)), HttpStatusCode.BadRequest, "room_not_started");
        for (var seat = 1; seat < 3; seat++)
        {
            var joined = await Post(users[seat], Api + "/join", new { invite });
            Assert.Equal(seat, joined.GetProperty("seat").GetInt32());
            Assert.Equal(seat, Revision(joined));
            Assert.Equal("waiting", joined.Text("status"));
            Assert.Equal(JsonValueKind.Null, Position(joined).ValueKind);
            Assert.True(JsonElement.DeepEquals(joined, await Post(users[seat], Api + "/join", new { invite })));
        }

        const string social = "/api/social";
        await Post(recipient, social + "/presence", new { tabId = "north", active = true, busy = false, available = true });
        var browsing = await Post(host, social + "/presence", new { tabId = "host", active = true, busy = true, available = true });
        var handle = Assert.Single(browsing.GetProperty("players").EnumerateArray(), player => player.Text("displayName") == "North").Text("id");
        foreach (var board in new[] { "xiangqi", "gomoku" })
            await Error(await host.Request(HttpMethod.Post, social + "/invites", new { playerId = handle, game = board, tableId = id }),
                HttpStatusCode.NotFound, "invite_unavailable");
        var offered = await Post(host, social + "/invites", new { playerId = handle, game = "big-two", tableId = id });
        AssertFields(offered, "id");
        var accept = social + "/invites/" + offered.Text("id") + "/accept";
        var pending = await Post(recipient, social + "/presence", new { tabId = "north", active = true, busy = false });
        var incoming = Assert.Single(pending.GetProperty("incoming").EnumerateArray());
        AssertFields(incoming, "id", "game", "displayName", "expiresAtUtc");
        Assert.Equal(offered.Text("id"), incoming.Text("id"));
        Assert.Equal("big-two", incoming.Text("game"));
        await Error(await stranger.Request(HttpMethod.Post, accept, new { }), HttpStatusCode.NotFound, "invite_unavailable");
        await Error(await recipient.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        var receipt = await Post(recipient, accept, new { });
        AssertFields(receipt, "game", "tableId");
        Assert.Equal("big-two", receipt.Text("game"));
        Assert.Equal(id, receipt.Text("tableId"));

        var views = await Task.WhenAll(users.Select(user => Read(user, path)));
        var first = SeatNumber(Position(views[0]), "turnSeat");
        Assert.NotNull(first);
        for (var seat = 0; seat < 4; seat++)
        {
            var view = views[seat];
            Assert.Equal("playing", view.Text("status"));
            Assert.Equal(3, Revision(view));
            Assert.Null(view.Text("invite"));
            AssertView(view, seat, first);
            Assert.Equal(new[] { 13, 13, 13, 13 }, Counts(view));
            Assert.Null(SeatNumber(Position(view), "winnerSeat"));
            Assert.Equal(JsonValueKind.Null, Position(view).GetProperty("lastPlay").ValueKind);
        }
        var dealt = views.SelectMany(OwnCards).ToArray();
        Assert.Equal(Enumerable.Range(0, 52), dealt.Select(card => card.Id).Order());
        Assert.Equal(from rank in Enumerable.Range(2, 13) from suit in Enumerable.Range(0, 4) select (rank, suit),
            dealt.Select(card => (card.Rank, card.Suit)).Order());
        var opener = users[first.Value];
        var openingHand = OwnCards(views[first.Value]);
        var diamond = Assert.Single(openingHand, card => card is { Rank: 3, Suit: 1 });
        Assert.All(Masks(views[first.Value]), mask => Assert.Contains(diamond.Id, Selected(openingHand, mask)));
        var next = (first.Value + 1) % 4;
        var open = Play(3, diamond.Id);
        await Error(await stranger.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        await Error(await stranger.Request(HttpMethod.Post, path + "/actions", open), HttpStatusCode.NotFound, "room_not_found");
        await Error(await stranger.Request(HttpMethod.Post, Api + "/join", new { invite }), HttpStatusCode.NotFound, "room_not_found");
        await Error(await users[next].Request(HttpMethod.Post, path + "/actions", open), HttpStatusCode.BadRequest, "not_your_turn");
        await Error(await opener.Request(HttpMethod.Post, path + "/actions", open, includeOrigin: false),
            HttpStatusCode.Forbidden, "forbidden");
        await Error(await opener.Request(HttpMethod.Post, path + "/actions", Pass(3)), HttpStatusCode.BadRequest, "illegal_action");
        await Error(await opener.Request(HttpMethod.Post, path + "/actions", Play(3, OwnCards(views[next])[0].Id)),
            HttpStatusCode.BadRequest, "illegal_action");
        await RejectMalformedActions(opener, path, diamond.Id);

        var played = await Post(opener, path + "/actions", open);
        AssertView(played, first.Value, next);
        Assert.Equal(4, Revision(played));
        Assert.Equal(12, Counts(played)[first.Value]);
        Assert.Equal(openingHand.Where(card => card.Id != diamond.Id), OwnCards(played));
        var last = Position(played).GetProperty("lastPlay");
        Assert.Equal(first.Value, last.GetProperty("seat").GetInt32());
        Assert.Equal("single", last.Text("kind"));
        Assert.Equal(diamond, Assert.Single(ReadCards(last, "cards")));
        await Error(await opener.Request(HttpMethod.Post, path + "/actions", open), HttpStatusCode.Conflict, "stale_room");
        Assert.True(JsonElement.DeepEquals(played, await Read(opener, path)));

        var following = await Read(users[next], path);
        AssertView(following, next, next);
        Assert.Equal(OwnCards(views[next]), OwnCards(following));
        Assert.Equal(13, Masks(following).Length);
        Assert.True(Position(following).GetProperty("canPass").GetBoolean());
        var passed = await Post(users[next], path + "/actions", Pass(4));
        AssertView(passed, next, (next + 1) % 4);
        Assert.Equal(5, Revision(passed));
        Assert.True(JsonElement.DeepEquals(last, Position(passed).GetProperty("lastPlay")));

        await server.StopAsync();
        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(receipt, await Post(recipient, accept, new { })));
        Assert.Equal(id, (await Read(recipient, Api + "/resume")).Text("tableId"));
        Assert.True(JsonElement.DeepEquals(passed, await Read(users[next], path)));
        var closed = await Post(recipient, path + "/leave", new { revision = 5 });
        Assert.Equal("closed", closed.Text("status"));
        Assert.Equal("left", closed.Text("endReason"));
        Assert.Equal(6, Revision(closed));
        AssertView(closed, 3, null);
        Assert.Equal(OwnCards(views[3]).Where(card => card.Id != diamond.Id), OwnCards(closed));
        Assert.True(JsonElement.DeepEquals(last, Position(closed).GetProperty("lastPlay")));
        Assert.True(JsonElement.DeepEquals(closed, await Read(recipient, path)));
        Assert.Null((await Read(recipient, Api + "/resume")).Text("tableId"));
    }

    private static async Task RejectMalformedActions(User user, string path, int card)
    {
        object[] actions =
        [
            new { kind = "pass", cards = Array.Empty<int>() },
            new { kind = "play", cards = new[] { card, card } },
            new { kind = "play", cards = new[] { 0, 1, 2, 3 } },
            new { kind = "play", cards = new[] { 52 } },
            new { kind = "play", cards = new[] { "5" } },
            new { kind = "play", cards = new[] { card }, seed = 12 },
            new { kind = "fold" }
        ];
        foreach (var action in actions)
            await Error(await user.Request(HttpMethod.Post, path + "/actions", new { revision = 3, action }),
                HttpStatusCode.BadRequest, "invalid_request");
        using var duplicate = new StringContent($$"""{"revision":3,"action":{"kind":"play","cards":[{{card}}],"cards":[{{card}}]} }""",
            Encoding.UTF8, "application/json");
        await Error(await user.Request(HttpMethod.Post, path + "/actions", duplicate), HttpStatusCode.BadRequest, "invalid_request");
    }

    private static async Task<User> Enter(Kestrel server, string mount, string name)
    {
        var user = new User(server, mount);
        Assert.Equal(name, (await user.Guest(name)).Text("displayName"));
        return user;
    }

    private static Kestrel MountedServer(string mount)
    {
        var server = PortalServer().WithSetting("BASE_URL", mount);
        return server.WithSetting("GamePortal__DataDirectory", Path.Combine(server.DataDirectory, "portal"));
    }

    private static async Task<JsonElement> Post(User guest, string path, object body) =>
        await Ok(await guest.Request(HttpMethod.Post, path, body));

    private static async Task<JsonElement> Read(User guest, string path) => await Ok(await guest.Request(HttpMethod.Get, path));

    private static object Play(long revision, params int[] cards) => new { revision, action = new { kind = "play", cards } };
    private static object Pass(long revision) => new { revision, action = new { kind = "pass" } };
    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();
    private static int? SeatNumber(JsonElement position, string key)
    {
        var seat = position.GetProperty(key);
        return seat.ValueKind == JsonValueKind.Null ? null : seat.GetInt32();
    }
    private static int[] Counts(JsonElement table) =>
        Position(table).GetProperty("seats").EnumerateArray().Select(seat => seat.GetProperty("cardsRemaining").GetInt32()).ToArray();
    private static int[] Masks(JsonElement table) => Position(table).GetProperty("legalMasks").EnumerateArray().Select(mask => mask.GetInt32()).ToArray();
    private static (int Id, int Rank, int Suit)[] OwnCards(JsonElement table) => ReadCards(Position(table), "ownCards");
    private static (int Id, int Rank, int Suit)[] ReadCards(JsonElement position, string key) =>
        position.GetProperty(key).EnumerateArray().Select(card =>
        {
            AssertFields(card, "id", "rank", "suit");
            return (card.GetProperty("id").GetInt32(), card.GetProperty("rank").GetInt32(), card.GetProperty("suit").GetInt32());
        }).ToArray();

    private static int[] Selected((int Id, int Rank, int Suit)[] cards, int mask) =>
        cards.Where((_, index) => (mask & (1 << index)) != 0).Select(card => card.Id).ToArray();

    private static void AssertFields(JsonElement json, params string[] fields) =>
        Assert.Equal(fields.Order(StringComparer.Ordinal), json.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));

    private static void AssertView(JsonElement table, int seat, int? turn, int? winner = null)
    {
        string[] fields = ["id", "revision", "status", "seat", "seats", "invite", "position"];
        AssertFields(table, table.Text("status") == "closed" ? [.. fields, "endReason"] : fields);
        Assert.Null(table.Text("invite"));
        Assert.Equal(seat, table.GetProperty("seat").GetInt32());
        var position = Position(table);
        AssertFields(position, "turnSeat", "winnerSeat", "seats", "ownCards", "lastPlay", "legalMasks", "canPass");
        Assert.Equal(turn, SeatNumber(position, "turnSeat"));
        Assert.Equal(winner, SeatNumber(position, "winnerSeat"));
        var seats = position.GetProperty("seats").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(0, 4), seats.Select(row => row.GetProperty("seat").GetInt32()));
        Assert.All(seats, row => AssertFields(row, "seat", "cardsRemaining"));
        var cards = OwnCards(table);
        Assert.Equal(Counts(table)[seat], cards.Length);
        Assert.All(cards, card =>
        {
            Assert.InRange(card.Id, 0, 51);
            Assert.InRange(card.Rank, 2, 14);
            Assert.InRange(card.Suit, 0, 3);
        });
        var last = position.GetProperty("lastPlay");
        if (last.ValueKind != JsonValueKind.Null)
        {
            AssertFields(last, "seat", "kind", "cards");
            Assert.Empty(ReadCards(last, "cards").Intersect(cards));
        }
        var masks = Masks(table);
        var canPass = position.GetProperty("canPass").GetBoolean();
        if (turn != seat)
        {
            Assert.Empty(masks);
            Assert.False(canPass);
        }
        else
        {
            Assert.True(canPass || masks.Length != 0);
            Assert.Equal(masks.Length, masks.Distinct().Count());
            Assert.All(masks, mask => Assert.InRange(mask, 1, (1 << cards.Length) - 1));
        }
    }
}
