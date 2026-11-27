using System.Net;
using System.Text;
using System.Text.Json;
using JadeCourt.Games.Harbors;
using Microsoft.Data.Sqlite;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class HarborsTableTests
{
    private const string Api = "/api/jade-harbors";

    [Fact]
    public async Task PrivateBotTableCommitsOneRollAndBothBotTurnsAndRestoresAfterRestart()
    {
        const string mount = "/game-portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var host = await Enter(server, mount, "Human");
        var table = await Post(host, Api + "/tables", new { players = 3, bots = true });
        var id = table.Text("id");
        var path = Api + "/tables/" + id;
        Assert.Equal("playing", table.Text("status"));
        Assert.Equal(0, Revision(table));
        Assert.Equal(new[] { false, true, true }, Rows(table, "seats").Select(seat => Flag(seat, "bot")));
        Assert.All(Rows(table, "seats"), seat => Assert.True(Flag(seat, "occupied")));
        var observer = await Enter(server, mount, "Observer");
        await Error(await observer.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");

        for (var step = 0; step < 4; step++)
        {
            var before = AssertPosition(table, 0);
            Assert.Equal(0, Number(before, "turnSeat"));
            Assert.Equal(0, Number(before, "turn"));
            table = await Place(host, path, table);
        }
        var setup = AssertPosition(table, 0);
        Assert.Equal("roll", setup.Text("phase"));
        Assert.Equal(1, Number(setup, "turn"));
        Assert.Equal(JsonValueKind.Null, setup.GetProperty("lastRoll").ValueKind);
        AssertSetup(setup);

        var roll = new { revision = Revision(table), action = new { kind = "roll" } };
        var attempts = await Task.WhenAll(host.Request(HttpMethod.Post, path + "/actions", roll),
            host.Request(HttpMethod.Post, path + "/actions", roll));
        var rolled = await Ok(Assert.Single(attempts, attempt => attempt.StatusCode == HttpStatusCode.OK));
        await Error(Assert.Single(attempts, attempt => attempt.StatusCode != HttpStatusCode.OK),
            HttpStatusCode.Conflict, "stale_room");
        Assert.Equal(Revision(table) + 1, Revision(rolled));
        var position = AssertPosition(rolled, 0);
        Assert.Equal("build", position.Text("phase"));
        AssertRoll(position);
        Assert.True(JsonElement.DeepEquals(rolled, await Read(host, path)));

        await server.StopAsync();
        await server.StartAsync();
        await AssertSave(host, id, fresh: true);
        Assert.True(JsonElement.DeepEquals(rolled, await Read(host, path)));

        var after = await Act(host, path, rolled, new { kind = "endTurn" });
        var next = AssertPosition(after, 0);
        Assert.Equal("roll", next.Text("phase"));
        Assert.Equal(0, Number(next, "turnSeat"));
        Assert.Equal(4, Number(next, "turn"));
        AssertRoll(next);
        Assert.True(JsonElement.DeepEquals(after, await Read(host, path)));
        await Error(await host.Request(HttpMethod.Post, path + "/next", new { revision = Revision(after) }),
            HttpStatusCode.NotFound, "invalid_request");
    }

    [Fact]
    public async Task ThreeGuestTableAdmitsTheExactInviteAndKeepsRecipientProjectionsPrivate()
    {
        const string mount = "/portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var users = new[] { await Enter(server, mount, "Host"), await Enter(server, mount, "Peer"), await Enter(server, mount, "Invitee") };
        var host = users[0];
        var invitee = users[2];
        var stranger = await Enter(server, mount, "Stranger");
        foreach (var players in new[] { 2, 4 })
            await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players, bots = false }),
                HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = 3, bots = true, seed = 7 }),
            HttpStatusCode.BadRequest, "invalid_request");

        var decoy = await Post(host, Api + "/tables", new { players = 3, bots = false });
        var table = await Post(host, Api + "/tables", new { players = 3, bots = false });
        var id = table.Text("id");
        var path = Api + "/tables/" + id;
        var invite = table.Text("invite");
        Assert.Equal("waiting", table.Text("status"));
        Assert.Equal(0, Revision(table));
        Assert.Equal(JsonValueKind.Null, table.GetProperty("position").ValueKind);
        Assert.Matches("^[a-f0-9]{32}$", invite);
        Assert.NotEqual(id, invite);
        await Error(await new User(server, mount).Request(HttpMethod.Get, path), HttpStatusCode.Unauthorized, "not_authenticated");
        await Reject(host, path, 0, new { kind = "roll" }, "room_not_started");
        var joined = await Post(users[1], Api + "/join", new { invite });
        Assert.Equal("waiting", joined.Text("status"));
        Assert.Equal(1, Number(joined, "seat"));
        Assert.Equal(1, Revision(joined));
        Assert.True(JsonElement.DeepEquals(joined, await Post(users[1], Api + "/join", new { invite })));

        const string social = "/api/social";
        await Post(invitee, social + "/presence", new { tabId = "invitee", active = true, busy = false, available = true });
        var presence = await Post(host, social + "/presence", new { tabId = "host", active = true, busy = true, available = true });
        var handle = Assert.Single(Rows(presence, "players"), player => player.Text("displayName") == "Invitee").Text("id");
        var offer = await Post(host, social + "/invites", new { playerId = handle, game = "jade-harbors", tableId = id });
        var accept = social + "/invites/" + offer.Text("id") + "/accept";
        await Error(await stranger.Request(HttpMethod.Post, accept, new { }), HttpStatusCode.NotFound, "invite_unavailable");
        await Error(await invitee.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        var receipt = await Post(invitee, accept, new { });
        AssertFields(receipt, "game", "tableId");
        Assert.Equal("jade-harbors", receipt.Text("game"));
        Assert.Equal(id, receipt.Text("tableId"));
        Assert.True(JsonElement.DeepEquals(decoy, await Read(host, Api + "/tables/" + decoy.Text("id"))));
        await Error(await stranger.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        await Error(await stranger.Request(HttpMethod.Post, Api + "/join", new { invite }), HttpStatusCode.NotFound, "room_not_found");

        table = await Read(host, path);
        Assert.Equal("playing", table.Text("status"));
        Assert.Equal(2, Revision(table));
        Assert.Equal(new[] { "Host", "Peer", "Invitee" }, Rows(table, "seats").Select(seat => seat.Text("name")));
        Assert.All(Rows(table, "seats"), seat => Assert.False(Flag(seat, "bot")));
        var first = Number(table.GetProperty("position"), "turnSeat");
        var firstView = await Read(users[first], path);
        var build = new { kind = "buildPier", target = firstView.At("position", "legalActions").GetProperty("piers")[0].GetInt32() };
        await Error(await stranger.Request(HttpMethod.Post, path + "/actions", new { revision = 2, action = build }),
            HttpStatusCode.NotFound, "room_not_found");
        await Reject(users[(first + 1) % 3], path, 2, build, "not_your_turn");
        await Error(await users[first].Request(HttpMethod.Post, path + "/actions", new { revision = 2, action = build },
            includeOrigin: false), HttpStatusCode.Forbidden, "forbidden");
        await Reject(users[first], path, 2, new { kind = "buildPier", target = 255 }, "illegal_action");

        int[][] holdings = [new int[4], new int[4], new int[4]];
        for (var step = 0; step < 12; step++)
        {
            var actor = Number(table.GetProperty("position"), "turnSeat");
            var before = await Read(users[actor], path);
            var position = AssertPosition(before, actor);
            Assert.Equal(0, Number(position, "turn"));
            int? target = null;
            if (position.Text("phase") == "placePier" && Rows(position, "nodes").Any(node => Seat(node) == actor))
            {
                target = Rows(position.GetProperty("legalActions"), "piers").Select(value => value.GetInt32())
                    .OrderByDescending(node => StarterResources(position, node).Sum()).First();
                holdings[actor] = StarterResources(position, target.Value);
                Assert.True(holdings[actor].Sum() > 0);
            }
            table = await Place(users[actor], path, before, target);
            Assert.Equal(holdings[actor], OwnResources(table.GetProperty("position")));
            if (step == 0)
            {
                await Error(await users[actor].Request(HttpMethod.Post, path + "/actions", new { revision = 2, action = build }),
                    HttpStatusCode.Conflict, "stale_room");
                Assert.True(JsonElement.DeepEquals(table, await Read(users[actor], path)));
            }
            if (target.HasValue)
                for (var seat = 0; seat < users.Length; seat++)
                    if (seat != actor)
                        Assert.Equal(holdings[seat], OwnResources(AssertPosition(await Read(users[seat], path), seat)));
        }

        var read = await Task.WhenAll(users.Select(user => Read(user, path)));
        for (var seat = 0; seat < users.Length; seat++)
        {
            var position = AssertPosition(read[seat], seat);
            Assert.Equal("roll", position.Text("phase"));
            AssertSetup(position);
            Assert.Equal(holdings[seat], OwnResources(position));
        }
        var roller = Number(table.GetProperty("position"), "turnSeat");
        await RejectMalformedActions(users[roller], path, Revision(table));
        var rolled = await Act(users[roller], path, read[roller], new { kind = "roll" });
        var after = await Task.WhenAll(users.Select(user => Read(user, path)));
        Assert.True(JsonElement.DeepEquals(rolled, after[roller]));
        var faces = AssertRoll(AssertPosition(rolled, roller));
        for (var seat = 0; seat < users.Length; seat++)
        {
            var view = AssertPosition(after[seat], seat);
            Assert.Equal(faces, AssertRoll(view));
            Assert.Equal(holdings[seat].Zip(Production(view, seat, faces.Sum()), (held, earned) => held + earned),
                OwnResources(view));
        }

        await server.StopAsync();
        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(receipt, await Post(invitee, accept, new { })));
        for (var seat = 0; seat < users.Length; seat++)
        {
            await AssertSave(users[seat], id, fresh: false);
            Assert.True(JsonElement.DeepEquals(after[seat], await Read(users[seat], path)));
        }
        Assert.Empty(Rows(await Read(stranger, "/api/saves"), "saves"));

        var closed = await Post(invitee, path + "/leave", new { revision = Revision(rolled) });
        Assert.Equal("closed", closed.Text("status"));
        Assert.Equal("left", closed.Text("endReason"));
        Assert.Equal(Revision(rolled) + 1, Revision(closed));
        for (var seat = 0; seat < users.Length; seat++)
        {
            var readClosed = await Read(users[seat], path);
            var position = AssertPosition(readClosed, seat);
            Assert.Equal("closed", readClosed.Text("status"));
            Assert.Equal(faces, AssertRoll(position));
            Assert.Equal(OwnResources(after[seat].GetProperty("position")), OwnResources(position));
        }
        await Error(await stranger.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        await Reject(invitee, path, Revision(closed), new { kind = "endTurn" }, "room_closed");
        Assert.Null((await Read(invitee, Api + "/resume")).Text("tableId"));
        Assert.Empty(Rows(await Read(invitee, "/api/saves"), "saves"));
    }

    [Fact]
    public async Task TiedTableSettlesTheTurnLimitInOneCommitAndOnlyMembersCanReadTheResult()
    {
        const string mount = "/portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var users = new[] { await Enter(server, mount, "Host"), await Enter(server, mount, "Peer"), await Enter(server, mount, "Third") };
        var observer = await Enter(server, mount, "Observer");
        var table = await Post(users[0], Api + "/tables", new { players = 3, bots = false });
        var id = table.Text("id");
        var invite = table.Text("invite");
        var path = Api + "/tables/" + id;
        await Post(users[1], Api + "/join", new { invite });
        await Post(users[2], Api + "/join", new { invite });

        // Fast-forward a real seeded checkpoint offline; settle through the HTTP writer.
        var checkpoint = HarborsGame.Start(seed: 17);
        for (var step = 0; step < 12 + 2 * 120; step++)
        {
            var seat = HarborsGame.View(checkpoint, 0).TurnSeat!.Value;
            var view = HarborsGame.View(checkpoint, seat);
            if (view is { Turn: 120, Phase: "build" }) break;
            var action = view.Phase switch
            {
                "placePier" => new HarborsAction(HarborsActionKind.BuildPier, view.LegalActions.Piers[0]),
                "placeRoute" => new HarborsAction(HarborsActionKind.BuildRoute, view.LegalActions.Routes[0]),
                "roll" => new HarborsAction(HarborsActionKind.Roll),
                _ => new HarborsAction(HarborsActionKind.EndTurn)
            };
            checkpoint = HarborsGame.Apply(checkpoint, seat, action);
        }
        await RestoreCheckpoint(server, id, checkpoint);
        var actor = HarborsGame.View(checkpoint, 0).TurnSeat!.Value;
        var player = users[actor];
        var before = await Read(player, path);
        var ready = AssertPosition(before, actor);
        Assert.Equal(120, Number(ready, "turn"));
        Assert.True(Flag(ready.GetProperty("legalActions"), "canEndTurn"));
        var trade = Rows(ready.GetProperty("legalActions"), "trades").First();
        var give = Number(trade, "give");
        var take = Number(trade, "take");
        var cost = Number(trade, "cost");
        var expected = OwnResources(ready);
        expected[give] -= cost;
        expected[take]++;
        var traded = await Act(player, path, before, new { kind = "trade", give, take });
        Assert.Equal(expected, OwnResources(AssertPosition(traded, actor)));

        var finished = await Act(player, path, traded, new { kind = "endTurn" });
        Assert.Equal("finished", finished.Text("status"));
        Assert.True(JsonElement.DeepEquals(finished, await Read(player, path)));
        for (var seat = 0; seat < users.Length; seat++)
        {
            var read = await Read(users[seat], path);
            Assert.Equal("finished", read.Text("status"));
            var position = AssertPosition(read, seat);
            Assert.Equal("finished", position.Text("phase"));
            Assert.Null(NullableNumber(position, "winnerSeat"));
            Assert.Equal(AssertRoll(ready), AssertRoll(position));
        }
        await Error(await observer.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        await Reject(player, path, Revision(finished), new { kind = "roll" }, "game_finished");
        Assert.Null((await Read(player, Api + "/resume")).Text("tableId"));
        Assert.Empty(Rows(await Read(player, "/api/saves"), "saves"));
    }

    private static async Task<JsonElement> Place(User user, string path, JsonElement table, int? selected = null)
    {
        var before = table.GetProperty("position");
        var pier = before.Text("phase") == "placePier";
        var options = before.GetProperty("legalActions").GetProperty(pier ? "piers" : "routes");
        var target = selected ?? options[0].GetInt32();
        var after = await Act(user, path, table, new { kind = pier ? "buildPier" : "buildRoute", target });
        var structure = Assert.Single(Rows(after.GetProperty("position"), pier ? "nodes" : "routes"),
            row => Number(row, "id") == target);
        Assert.Equal(Number(before, "turnSeat"), Number(structure, "seat"));
        if (pier) Assert.Equal(1, Number(structure, "level"));
        return after;
    }

    private static void AssertSetup(JsonElement position)
    {
        var seats = Rows(position, "seats").ToArray();
        for (var seat = 0; seat < seats.Length; seat++)
        {
            Assert.Equal(2, Rows(position, "nodes").Count(node => Seat(node) == seat));
            Assert.Equal(2, Rows(position, "routes").Count(route => Seat(route) == seat));
            Assert.Equal(2, Number(seats[seat], "points"));
        }
    }

    private static int[] StarterResources(JsonElement position, int node)
    {
        var holdings = new int[4];
        foreach (var tile in Rows(position, "tiles"))
            if (NullableNumber(tile, "resource") is int resource && Rows(tile, "nodeIds").Any(id => id.GetInt32() == node))
                holdings[resource]++;
        return holdings;
    }

    private static int[] Production(JsonElement position, int seat, int sum)
    {
        var earned = new int[4];
        var nodes = Rows(position, "nodes").ToDictionary(node => Number(node, "id"));
        foreach (var tile in Rows(position, "tiles"))
            if (NullableNumber(tile, "number") == sum && NullableNumber(tile, "resource") is int resource)
                foreach (var id in Rows(tile, "nodeIds"))
                    if (Seat(nodes[id.GetInt32()]) == seat) earned[resource] += Number(nodes[id.GetInt32()], "level");
        return earned;
    }

    private static async Task RejectMalformedActions(User user, string path, long revision)
    {
        object[] actions =
        [
            new { kind = "roll", dice = new[] { 6, 6 } }, new { kind = "roll", outcome = 12 }, new { kind = "roll", seed = 1 },
            new { kind = "buildPier", target = -1 }, new { kind = "buildRoute", target = 256 },
            new { kind = "buildRoute", target = 0.5 }, new { kind = "upgrade", target = "0" },
            new { kind = "trade", give = 0, take = 4 }, new { kind = "trade", give = 1 },
            new { kind = "trade", give = 1, take = 0, cost = 1 }, new { kind = "endTurn", target = 0 },
            new { kind = "place" }
        ];
        foreach (var action in actions)
            await Reject(user, path, revision, action, "invalid_request");
        using var duplicate = new StringContent($$"""{"revision":{{revision}},"action":{"kind":"roll","kind":"roll"} }""",
            Encoding.UTF8, "application/json");
        await Error(await user.Request(HttpMethod.Post, path + "/actions", duplicate), HttpStatusCode.BadRequest, "invalid_request");
    }

    private static JsonElement AssertPosition(JsonElement table, int viewer)
    {
        Assert.Equal(viewer, Number(table, "seat"));
        Assert.Null(table.Text("invite"));
        string[] fields = ["id", "revision", "status", "seat", "seats", "invite", "position"];
        AssertFields(table, table.Text("status") == "closed" ? [.. fields, "endReason"] : fields);
        Assert.All(Rows(table, "seats"), seat => AssertFields(seat, "seat", "name", "bot", "occupied"));

        var position = table.GetProperty("position");
        AssertFields(position, "phase", "turnSeat", "turn", "winnerSeat", "lastRoll", "tiles", "nodes", "routes", "seats",
            "ownResources", "legalActions");
        Assert.All(Rows(position, "tiles"), tile => AssertFields(tile, "id", "x", "y", "resource", "number", "nodeIds"));
        Assert.All(Rows(position, "nodes"), node => AssertFields(node, "id", "x", "y", "seat", "level"));
        Assert.All(Rows(position, "routes"), route => AssertFields(route, "id", "a", "b", "seat"));
        var seats = Rows(position, "seats").ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, seats.Select(seat => Number(seat, "seat")));
        Assert.All(seats, seat => AssertFields(seat, "seat", "points", "resourceCount"));
        var own = OwnResources(position);
        Assert.Equal(4, own.Length);
        Assert.All(own, amount => Assert.True(amount >= 0));
        Assert.Equal(Number(seats[viewer], "resourceCount"), own.Sum());

        var legal = position.GetProperty("legalActions");
        AssertFields(legal, "canRoll", "canEndTurn", "piers", "routes", "upgrades", "trades");
        var turnSeat = NullableNumber(position, "turnSeat");
        if (table.Text("status") != "playing") Assert.Null(turnSeat);
        var active = table.Text("status") == "playing" && turnSeat == viewer;
        Assert.Equal(active && position.Text("phase") == "roll", Flag(legal, "canRoll"));
        Assert.Equal(active && position.Text("phase") == "build", Flag(legal, "canEndTurn"));
        if (!active)
            foreach (var key in new[] { "piers", "routes", "upgrades", "trades" }) Assert.Empty(Rows(legal, key));
        Assert.All(Rows(legal, "trades"), trade => AssertFields(trade, "give", "take", "cost"));
        return position;
    }

    private static int[] AssertRoll(JsonElement position)
    {
        var dice = Rows(position, "lastRoll").Select(face => face.GetInt32()).ToArray();
        Assert.Equal(2, dice.Length);
        Assert.All(dice, die => Assert.InRange(die, 1, 6));
        return dice;
    }

    private static async Task RestoreCheckpoint(Kestrel server, string id, string checkpoint)
    {
        await server.StopAsync();
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(server.DataDirectory, "portal", "mahjong.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            await db.OpenAsync();
            await using var update = db.CreateCommand();
            update.CommandText = "UPDATE SeatedTables SET Checkpoint=$checkpoint WHERE Id=$id AND Game='jade-harbors'";
            update.Parameters.AddWithValue("$checkpoint", checkpoint);
            update.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        await server.StartAsync();
    }

    private static async Task AssertSave(User user, string id, bool fresh)
    {
        Assert.Equal(id, (await Read(user, Api + "/resume")).Text("tableId"));
        var save = Assert.Single(Rows(await Read(user, "/api/saves"), "saves"));
        AssertFields(save, "game", "tableId", "variant", "players", "botDifficulty", "canStartFresh");
        Assert.Equal("jade-harbors", save.Text("game"));
        Assert.Equal(id, save.Text("tableId"));
        Assert.Equal(3, Number(save, "players"));
        Assert.Null(save.Text("variant"));
        Assert.Null(save.Text("botDifficulty"));
        Assert.Equal(fresh, Flag(save, "canStartFresh"));
    }

    private static Kestrel MountedServer(string mount)
    {
        var server = PortalServer().WithSetting("BASE_URL", mount);
        return server.WithSetting("GamePortal__DataDirectory", Path.Combine(server.DataDirectory, "portal"));
    }

    private static async Task<User> Enter(Kestrel server, string mount, string name)
    {
        var user = new User(server, mount);
        Assert.Equal(name, (await user.Guest(name)).Text("displayName"));
        return user;
    }

    private static async Task<JsonElement> Act(User user, string path, JsonElement table, object action)
    {
        var result = await Post(user, path + "/actions", new { revision = Revision(table), action });
        Assert.Equal(Revision(table) + 1, Revision(result));
        return result;
    }

    private static async Task Reject(User user, string path, long revision, object action, string error) =>
        await Error(await user.Request(HttpMethod.Post, path + "/actions", new { revision, action }), HttpStatusCode.BadRequest, error);

    private static async Task<JsonElement> Post(User user, string path, object body) => await Ok(await user.Request(HttpMethod.Post, path, body));
    private static async Task<JsonElement> Read(User user, string path) => await Ok(await user.Request(HttpMethod.Get, path));
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();
    private static int Number(JsonElement json, string key) => json.GetProperty(key).GetInt32();
    private static bool Flag(JsonElement json, string key) => json.GetProperty(key).GetBoolean();
    private static int? Seat(JsonElement row) => NullableNumber(row, "seat");
    private static int[] OwnResources(JsonElement position) => Rows(position, "ownResources").Select(value => value.GetInt32()).ToArray();
    private static JsonElement.ArrayEnumerator Rows(JsonElement json, string key) => json.GetProperty(key).EnumerateArray();
    private static int? NullableNumber(JsonElement json, string key) =>
        json.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Number(json, key);

    private static void AssertFields(JsonElement json, params string[] fields) =>
        Assert.Equal(fields.Order(StringComparer.Ordinal), json.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
}
