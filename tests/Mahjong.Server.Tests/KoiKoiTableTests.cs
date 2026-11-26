using System.Net;
using System.Text.Json;
using JadeCourt.Games.Hanafuda;
using Microsoft.Data.Sqlite;
using static Mahjong.Server.Tests.BoardTableTests;

namespace Mahjong.Server.Tests;

public sealed class KoiKoiTableTests
{
    private const string Api = "/api/koi-koi";

    [Fact]
    public async Task BotRoundCommitsOnceAndNextDealUsesTheRestoredSettlement()
    {
        const string mount = "/game-portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var host = await Enter(server, mount, "West");
        var table = await Post(host, Api + "/tables", new { players = 2, bots = true });
        Assert.Equal(0, Revision(table));
        Assert.Equal("West", table.GetProperty("seats")[0].Text("name"));
        Assert.Equal(new[] { false, true }, table.GetProperty("seats").EnumerateArray().Select(seat => Flag(seat, "bot")));
        var path = Api + "/tables/" + table.Text("id");
        var initial = AssertPosition(table, 0);
        Assert.Equal(8, Cards(initial, "ownCards").Length);
        Assert.InRange(Number(initial, "stockCount"), 23, 24);
        await Error(await host.Request(HttpMethod.Post, path + "/next", new { revision = 0 }),
            HttpStatusCode.BadRequest, "round_in_progress");

        for (var step = 0; step < 18 && table.At("position", "phase").GetString() != "roundEnd"; step++)
        {
            var before = AssertPosition(table, 0);
            Assert.Equal(0, NullableNumber(before, "turnSeat"));
            var revision = Revision(table);
            var action = LegalAction(before);
            table = await Post(host, path + "/actions", new { revision, action });
            var after = AssertPosition(table, 0);
            Assert.Equal(revision + 1, Revision(table));
            var used = Counts(before).Zip(Counts(after), (prior, current) => prior - current).ToArray();
            Assert.Equal(before.Text("phase") == "hand" ? 1 : 0, used[0]);
            Assert.InRange(used[1], 0, 1);
            Assert.Equal(Number(before, "stockCount") - used.Sum(), Number(after, "stockCount"));
        }

        var settled = AssertPosition(table, 0);
        Assert.Equal("roundEnd", settled.Text("phase"));
        Assert.Equal("playing", table.Text("status"));
        var result = settled.GetProperty("result");
        var winner = NullableNumber(result, "winnerSeat");
        var nextDealer = Number(result, "nextDealerSeat");
        if (winner is int seat)
        {
            var basePoints = settled.GetProperty("seats")[seat].GetProperty("yaku").EnumerateArray().Sum(yaku => Number(yaku, "points"));
            Assert.Equal(basePoints, Number(result, "basePoints"));
            Assert.Equal(basePoints >= 7, Flag(result, "sevenPointBonus"));
            Assert.Equal(Flag(settled.GetProperty("seats")[1 - seat], "continued"), Flag(result, "opponentContinued"));
            var award = basePoints * (basePoints >= 7 ? 2 : 1) * (Flag(result, "opponentContinued") ? 2 : 1);
            Assert.Equal(award, Number(result, "points"));
            Assert.Equal(award, Scores(settled)[seat]);
            Assert.Equal(0, Scores(settled)[1 - seat]);
            Assert.Equal(seat, nextDealer);
        }
        else
        {
            Assert.Equal(new[] { 0, 0 }, Scores(settled));
            Assert.Equal(0, Number(result, "points"));
            Assert.Equal(1 - Number(settled, "dealerSeat"), nextDealer);
        }
        var endedRevision = Revision(table);
        await Error(await host.Request(HttpMethod.Post, path + "/actions", new { revision = endedRevision, action = new { kind = "score" } }),
            HttpStatusCode.BadRequest, "round_finished");
        await server.StopAsync();
        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(table, await Read(host, path)));
        await AssertSave(host, table.Text("id"), fresh: true);

        var next = await Post(host, path + "/next", new { revision = endedRevision });
        var reopened = AssertPosition(next, 0);
        Assert.Equal(endedRevision + 1, Revision(next));
        Assert.Equal(2, Number(reopened, "round"));
        Assert.Equal(nextDealer, Number(reopened, "dealerSeat"));
        Assert.Equal(Scores(settled), Scores(reopened));
        Assert.Equal(8, Cards(reopened, "ownCards").Length);
        Assert.InRange(Number(reopened, "stockCount"), 23, 24);
        await Error(await host.Request(HttpMethod.Post, path + "/next", new { revision = endedRevision }),
            HttpStatusCode.Conflict, "stale_room");
        Assert.True(JsonElement.DeepEquals(next, await Read(host, path)));
    }

    [Fact]
    public async Task PrivateInviteStartsTheExactTableAndRestartsWithDistinctPrivateHands()
    {
        const string mount = "/portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var host = await Enter(server, mount, "North");
        var peer = await Enter(server, mount, "South");
        var stranger = await Enter(server, mount, "Observer");
        await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = 3, bots = true }),
            HttpStatusCode.BadRequest, "invalid_request");
        await Error(await host.Request(HttpMethod.Post, Api + "/tables", new { players = 2, bots = true, seed = 12 }),
            HttpStatusCode.BadRequest, "invalid_request");

        var waiting = await Post(host, Api + "/tables", new { players = 2, bots = false });
        var decoy = await Post(host, Api + "/tables", new { players = 2, bots = false });
        var id = waiting.Text("id");
        var path = Api + "/tables/" + id;
        Assert.Equal("waiting", waiting.Text("status"));
        Assert.Equal(0, Revision(waiting));
        Assert.Equal(JsonValueKind.Null, Position(waiting).ValueKind);
        Assert.Matches("^[0-9a-f]{32}$", waiting.Text("invite"));
        Assert.NotEqual(id, waiting.Text("invite"));
        await Error(await new User(server, mount).Request(HttpMethod.Get, path), HttpStatusCode.Unauthorized, "not_authenticated");
        await Error(await host.Request(HttpMethod.Post, path + "/next", new { revision = 0 }),
            HttpStatusCode.BadRequest, "room_not_started");

        const string social = "/api/social";
        await Post(peer, social + "/presence", new { tabId = "south", active = true, busy = false, available = true });
        var presence = await Post(host, social + "/presence", new { tabId = "north", active = true, busy = true, available = true });
        var handle = Assert.Single(presence.GetProperty("players").EnumerateArray(), player => player.Text("displayName") == "South").Text("id");
        await Error(await host.Request(HttpMethod.Post, social + "/invites", new { playerId = handle, game = "big-two", tableId = id }),
            HttpStatusCode.NotFound, "invite_unavailable");
        await Error(await host.Request(HttpMethod.Post, social + "/invites", new { playerId = handle, game = "nonograms", tableId = id }),
            HttpStatusCode.BadRequest, "invalid_request");
        var offer = await Post(host, social + "/invites", new { playerId = handle, game = "koi-koi", tableId = id });
        var accept = social + "/invites/" + offer.Text("id") + "/accept";
        await Error(await stranger.Request(HttpMethod.Post, accept, new { }), HttpStatusCode.NotFound, "invite_unavailable");
        await Error(await peer.Request(HttpMethod.Get, path), HttpStatusCode.NotFound, "room_not_found");
        var receipt = await Post(peer, accept, new { });
        AssertFields(receipt, "game", "tableId");
        Assert.Equal("koi-koi", receipt.Text("game"));
        Assert.Equal(id, receipt.Text("tableId"));
        Assert.Equal("waiting", (await Read(host, Api + "/tables/" + decoy.Text("id"))).Text("status"));
        await AssertSave(peer, id, fresh: false);

        var users = new[] { host, peer };
        var views = await Task.WhenAll(users.Select(user => Read(user, path)));
        var positions = views.Select((table, seat) => AssertPosition(table, seat)).ToArray();
        Assert.All(views, table =>
        {
            Assert.Equal(1, Revision(table));
            Assert.Equal("playing", table.Text("status"));
            Assert.Equal(new[] { "North", "South" }, table.GetProperty("seats").EnumerateArray().Select(seat => seat.Text("name")));
        });
        Assert.Equal(CardIds(positions[0], "field"), CardIds(positions[1], "field"));
        Assert.Equal(24, Number(positions[0], "stockCount"));
        Assert.Equal(new[] { 8, 8 }, Counts(positions[0]));
        Assert.Empty(CardIds(positions[0], "ownCards").Intersect(CardIds(positions[1], "ownCards")));
        var actor = Number(positions[0], "turnSeat");
        Assert.Equal(Number(positions[1], "dealerSeat"), actor);
        var action = LegalAction(positions[actor]);
        var move = new { revision = 1, action };
        await Error(await stranger.Request(HttpMethod.Post, path + "/actions", move), HttpStatusCode.NotFound, "room_not_found");
        await Error(await stranger.Request(HttpMethod.Post, path + "/next", new { revision = 1 }),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await users[1 - actor].Request(HttpMethod.Post, path + "/actions", move), HttpStatusCode.BadRequest, "not_your_turn");
        await Error(await users[actor].Request(HttpMethod.Post, path + "/actions", move, includeOrigin: false),
            HttpStatusCode.Forbidden, "forbidden");
        var foreignCard = CardIds(positions[1 - actor], "ownCards")[0];
        await Error(await users[actor].Request(HttpMethod.Post, path + "/actions", new { revision = 1, action = new { kind = "play", cardId = foreignCard } }),
            HttpStatusCode.BadRequest, "illegal_action");
        object[] malformed = [new { kind = "takeDraw" }, new { kind = "play", cardId = 48 },
            new { kind = "play", cardId = 0, targetId = (int?)null }, new { kind = "continue", targetId = 2 }];
        foreach (var invalid in malformed)
            await Error(await users[actor].Request(HttpMethod.Post, path + "/actions", new { revision = 1, action = invalid }),
                HttpStatusCode.BadRequest, "invalid_request");

        var committed = await Post(users[actor], path + "/actions", move);
        Assert.Equal(2, Revision(committed));
        var after = AssertPosition(committed, actor);
        var other = AssertPosition(await Read(users[1 - actor], path), 1 - actor);
        Assert.True(JsonElement.DeepEquals(after.GetProperty("drawnCard"), other.GetProperty("drawnCard")));
        Assert.Equal(CardIds(positions[1 - actor], "ownCards"), CardIds(other, "ownCards"));
        Assert.Equal(7, Cards(after, "ownCards").Length);
        await Error(await users[actor].Request(HttpMethod.Post, path + "/actions", move), HttpStatusCode.Conflict, "stale_room");

        var linkJoin = await Post(stranger, Api + "/join", new { invite = decoy.Text("invite") });
        Assert.Equal(decoy.Text("id"), linkJoin.Text("id"));
        Assert.Equal(1, Revision(linkJoin));
        AssertPosition(linkJoin, 1);
        Assert.True(JsonElement.DeepEquals(linkJoin, await Post(stranger, Api + "/join", new { invite = decoy.Text("invite") })));

        await server.StopAsync();
        await server.StartAsync();
        Assert.True(JsonElement.DeepEquals(receipt, await Post(peer, accept, new { })));
        Assert.True(JsonElement.DeepEquals(committed, await Read(users[actor], path)));
        Assert.True(JsonElement.DeepEquals(other, Position(await Read(users[1 - actor], path))));
        var closed = await Post(peer, path + "/leave", new { revision = 2 });
        Assert.Equal("closed", closed.Text("status"));
        Assert.Equal("left", closed.Text("endReason"));
        AssertPosition(closed, 1);
        Assert.Null((await Read(peer, Api + "/resume")).Text("tableId"));
    }

    [Fact]
    public async Task RestoredDrawRequiresItsOwnersChoiceBeforeKoiKoiCanPassTheTurn()
    {
        const string mount = "/portal";
        await using var server = MountedServer(mount);
        await server.StartAsync();
        var users = new[] { await Enter(server, mount, "East"), await Enter(server, mount, "West") };
        var waiting = await Post(users[0], Api + "/tables", new { players = 2, bots = false });
        var started = await Post(users[1], Api + "/join", new { invite = waiting.Text("invite") });
        var (checkpoint, seat, target) = PendingDrawWithYaku();
        await server.StopAsync();
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(server.DataDirectory, "portal", "mahjong.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            await db.OpenAsync();
            await using var update = db.CreateCommand();
            update.CommandText = "UPDATE SeatedTables SET Checkpoint=$checkpoint WHERE Id=$id AND Game='koi-koi'";
            update.Parameters.AddWithValue("$checkpoint", checkpoint);
            update.Parameters.AddWithValue("$id", started.Text("id"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await server.StartAsync();
        var path = Api + "/tables/" + started.Text("id");
        var pending = AssertPosition(await Read(users[seat], path), seat);
        var peer = AssertPosition(await Read(users[1 - seat], path), 1 - seat);
        Assert.Equal("draw", pending.Text("phase"));
        Assert.True(JsonElement.DeepEquals(pending.GetProperty("drawnCard"), peer.GetProperty("drawnCard")));
        var take = new { revision = 1, action = new { kind = "takeDraw", targetId = target } };
        await Error(await users[1 - seat].Request(HttpMethod.Post, path + "/actions", take), HttpStatusCode.BadRequest, "not_your_turn");
        await Error(await users[seat].Request(HttpMethod.Post, path + "/actions", new { revision = 1, action = new { kind = "continue" } }),
            HttpStatusCode.BadRequest, "illegal_action");

        var chosen = await Post(users[seat], path + "/actions", take);
        var decision = AssertPosition(chosen, seat);
        Assert.Equal(2, Revision(chosen));
        Assert.Equal("decision", decision.Text("phase"));
        var captured = CardIds(decision.GetProperty("seats")[seat], "captured");
        Assert.Contains(target, captured);
        Assert.Contains(Number(pending.GetProperty("drawnCard"), "id"), captured);
        Assert.DoesNotContain(target, CardIds(decision, "field"));

        var pass = await Post(users[seat], path + "/actions", new { revision = 2, action = new { kind = "continue" } });
        var continued = AssertPosition(pass, seat);
        Assert.Equal(3, Revision(pass));
        Assert.Equal("hand", continued.Text("phase"));
        Assert.Equal(1 - seat, Number(continued, "turnSeat"));
        Assert.True(Flag(continued.GetProperty("seats")[seat], "continued"));
        Assert.Equal(new[] { 0, 0 }, Scores(continued));
        Assert.True(JsonElement.DeepEquals(pass, await Read(users[seat], path)));
    }

    private static (string Checkpoint, int Seat, int Target) PendingDrawWithYaku()
    {
        for (var seed = 0; seed < 64; seed++)
        {
            var checkpoint = KoiKoiGame.Start(seed);
            for (var step = 0; step < 32 && KoiKoiGame.View(checkpoint, 0).TurnSeat is int seat; step++)
            {
                var view = KoiKoiGame.View(checkpoint, seat);
                if (view.Phase == "draw")
                {
                    var targets = view.LegalActions.DrawTargets;
                    foreach (var target in targets)
                    {
                        var selected = KoiKoiGame.Apply(checkpoint, seat, new(KoiKoiActionKind.TakeDraw, TargetId: target));
                        if (KoiKoiGame.View(selected, seat).LegalActions.CanContinue) return (checkpoint, seat, target);
                    }
                    checkpoint = KoiKoiGame.Apply(checkpoint, seat, new(KoiKoiActionKind.TakeDraw, TargetId: targets[0]));
                }
                else if (view.Phase == "hand")
                {
                    var play = view.LegalActions.Plays[0];
                    checkpoint = KoiKoiGame.Apply(checkpoint, seat, new(KoiKoiActionKind.Play, play.CardId,
                        play.TargetIds.Length == 2 ? play.TargetIds[0] : null));
                }
                else break;
            }
        }
        throw new InvalidOperationException("No seeded draw choice leading to an eligible Koi-Koi decision.");
    }

    private static object LegalAction(JsonElement position)
    {
        var actions = position.GetProperty("legalActions");
        if (Flag(actions, "canScore")) return new { kind = "score" };
        if (actions.GetProperty("drawTargets").GetArrayLength() == 2)
            return new { kind = "takeDraw", targetId = actions.GetProperty("drawTargets")[0].GetInt32() };
        var play = actions.GetProperty("plays").EnumerateArray().OrderByDescending(item => item.GetProperty("targetIds").GetArrayLength() == 2).First();
        var card = Number(play, "cardId");
        var targets = play.GetProperty("targetIds");
        return targets.GetArrayLength() == 2 ? new { kind = "play", cardId = card, targetId = targets[0].GetInt32() }
            : new { kind = "play", cardId = card };
    }

    private static JsonElement AssertPosition(JsonElement table, int viewer)
    {
        Assert.Equal(viewer, Number(table, "seat"));
        Assert.Null(table.Text("invite"));
        string[] tableFields = ["id", "revision", "status", "seat", "seats", "invite", "position"];
        AssertFields(table, table.Text("status") == "closed" ? [.. tableFields, "endReason"] : tableFields);
        var position = Position(table);
        AssertFields(position, "phase", "turnSeat", "dealerSeat", "round", "totalRounds", "field", "stockCount",
            "drawnCard", "ownCards", "seats", "result", "matchWinnerSeat", "legalActions");
        var phase = position.Text("phase");
        var turn = NullableNumber(position, "turnSeat");
        var playing = table.Text("status") == "playing";
        if (!playing || phase == "roundEnd") Assert.Null(turn);
        else Assert.Contains(turn, new int?[] { 0, 1 });
        Assert.Equal(6, Number(position, "totalRounds"));
        var seats = position.GetProperty("seats");
        var field = Cards(position, "field");
        var own = Cards(position, "ownCards");
        var legal = position.GetProperty("legalActions");
        AssertFields(legal, "plays", "drawTargets", "canScore", "canContinue", "canNextRound");
        var seen = field.Concat(own).ToList();
        var counts = Counts(position);
        Assert.Equal(2, seats.GetArrayLength());
        for (var index = 0; index < 2; index++)
        {
            var seat = seats[index];
            AssertFields(seat, "seat", "handCount", "captured", "score", "yaku", "continued");
            Assert.Equal(index, Number(seat, "seat"));
            seen.AddRange(Cards(seat, "captured"));
            Assert.All(seat.GetProperty("yaku").EnumerateArray(), yaku =>
            {
                AssertFields(yaku, "name", "points");
                Assert.True(Number(yaku, "points") > 0);
            });
        }
        Assert.Equal(counts[viewer], own.Length);
        var drawn = position.GetProperty("drawnCard");
        if (phase == "draw") seen.Add(drawn);
        else Assert.Equal(JsonValueKind.Null, drawn.ValueKind);
        foreach (var card in seen)
        {
            AssertFields(card, "id", "month", "kind", "name");
            Assert.InRange(Number(card, "id"), 0, 47);
            Assert.Equal(Number(card, "id") / 4 + 1, Number(card, "month"));
            Assert.Contains(card.Text("kind"), new[] { "bright", "animal", "ribbon", "plain" });
            Assert.NotEmpty(card.Text("name"));
        }
        Assert.Equal(seen.Count, seen.Select(card => Number(card, "id")).Distinct().Count());
        Assert.Equal(48, seen.Count + Number(position, "stockCount") + counts[1 - viewer]);
        var active = turn == viewer;
        var plays = legal.GetProperty("plays").EnumerateArray().ToArray();
        Assert.Equal(active && phase == "hand" ? CardIds(position, "ownCards") : [], plays.Select(play => Number(play, "cardId")));
        foreach (var play in plays)
        {
            AssertFields(play, "cardId", "targetIds");
            var month = Number(play, "cardId") / 4 + 1;
            Assert.Equal(field.Where(card => Number(card, "month") == month).Select(card => Number(card, "id")),
                play.GetProperty("targetIds").EnumerateArray().Select(target => target.GetInt32()));
        }
        var drawTargets = legal.GetProperty("drawTargets").EnumerateArray().Select(target => target.GetInt32()).ToArray();
        if (active && phase == "draw")
        {
            Assert.Equal(2, drawTargets.Length);
            Assert.Equal(field.Where(card => Number(card, "month") == Number(drawn, "month")).Select(card => Number(card, "id")), drawTargets);
        }
        else Assert.Empty(drawTargets);
        Assert.Equal(active && phase == "decision", Flag(legal, "canScore"));
        Assert.Equal(active && phase == "decision" && own.Length != 0 && seats.EnumerateArray().All(seat => !Flag(seat, "continued")),
            Flag(legal, "canContinue"));
        Assert.Equal(playing && phase == "roundEnd", Flag(legal, "canNextRound"));
        var result = position.GetProperty("result");
        if (phase is "roundEnd" or "finished")
            AssertFields(result, "winnerSeat", "points", "basePoints", "sevenPointBonus", "opponentContinued", "nextDealerSeat");
        else Assert.Equal(JsonValueKind.Null, result.ValueKind);
        return position;
    }

    private static async Task AssertSave(User user, string id, bool fresh)
    {
        Assert.Equal(id, (await Read(user, Api + "/resume")).Text("tableId"));
        var save = Assert.Single((await Read(user, "/api/saves")).GetProperty("saves").EnumerateArray());
        AssertFields(save, "game", "tableId", "variant", "players", "botDifficulty", "canStartFresh");
        Assert.Equal("koi-koi", save.Text("game"));
        Assert.Equal(id, save.Text("tableId"));
        Assert.Equal(2, Number(save, "players"));
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

    private static async Task<JsonElement> Post(User user, string path, object body) => await Ok(await user.Request(HttpMethod.Post, path, body));
    private static async Task<JsonElement> Read(User user, string path) => await Ok(await user.Request(HttpMethod.Get, path));
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();
    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static int Number(JsonElement json, string key) => json.GetProperty(key).GetInt32();
    private static int? NullableNumber(JsonElement json, string key) =>
        json.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Number(json, key);
    private static bool Flag(JsonElement json, string key) => json.GetProperty(key).GetBoolean();
    private static JsonElement[] Cards(JsonElement json, string key) => json.GetProperty(key).EnumerateArray().ToArray();
    private static int[] CardIds(JsonElement json, string key) => Cards(json, key).Select(card => Number(card, "id")).ToArray();
    private static int[] Counts(JsonElement json) => json.GetProperty("seats").EnumerateArray().Select(seat => Number(seat, "handCount")).ToArray();
    private static int[] Scores(JsonElement json) => json.GetProperty("seats").EnumerateArray().Select(seat => Number(seat, "score")).ToArray();

    private static void AssertFields(JsonElement json, params string[] fields) =>
        Assert.Equal(fields.Order(StringComparer.Ordinal), json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
}
