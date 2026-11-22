using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JadeCourt.Chess;

namespace Mahjong.Server.Tests;

public sealed class BoardTableTests
{
    private const string Api = "/portal/api/board";

    [Fact]
    public async Task PrivateInviteAndRevisionCommitSurviveRestartWithoutReplacingOtherTables()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var catalog = await Catalog(new User(server));
        Assert.Equal(new[] { "checkers", "chess", "chinese-checkers", "go", "reversi" },
            catalog.Select(g => g.Text("id")).Order(StringComparer.Ordinal));
        var reversi = Definition(catalog, "reversi");
        var host = await Guest(server, "Host");
        var peer = await Guest(server, "Friend");
        var stranger = await Guest(server, "Stranger");

        var waiting = await Create(host, reversi, 2);
        var id = waiting.Text("id");
        var invite = waiting.Text("invite");
        var table = Api + "/tables/" + id;
        Assert.Equal("waiting", waiting.Text("status"));
        Assert.Equal(0, Revision(waiting));
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("position").ValueKind);
        AssertNoMotion(waiting);
        Assert.Matches("^[a-f0-9]{32}$", invite);
        Assert.NotEqual(id, invite);
        Assert.Equal("Host", waiting.GetProperty("seats")[0].Text("name"));
        Assert.False(waiting.GetProperty("seats")[1].GetProperty("occupied").GetBoolean());

        await Error(await new User(server).Request(HttpMethod.Get, table), HttpStatusCode.Unauthorized, "not_authenticated");
        await Error(await stranger.Request(HttpMethod.Get, table), HttpStatusCode.NotFound, "room_not_found");
        await Error(await stranger.Request(HttpMethod.Post, table + "/moves", new { revision = 0, moveId = "guess" }),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await peer.Request(HttpMethod.Post, Api + "/join", new { invite = id }),
            HttpStatusCode.NotFound, "room_not_found");
        Assert.Null(await Resume(stranger, "reversi"));

        var request = new { invite };
        var joined = await Ok(await peer.Request(HttpMethod.Post, Api + "/join", request));
        Assert.Equal("playing", joined.Text("status"));
        Assert.Equal(1, Revision(joined));
        Assert.Equal(1, joined.GetProperty("seat").GetInt32());
        Assert.Equal("Friend", joined.GetProperty("seats")[1].Text("name"));
        Assert.Null(joined.Text("invite"));
        AssertNoMotion(joined);
        Assert.Equal(joined.GetRawText(), (await Ok(await peer.Request(HttpMethod.Post, Api + "/join", request))).GetRawText());
        await Error(await stranger.Request(HttpMethod.Post, Api + "/join", request), HttpStatusCode.NotFound, "room_not_found");
        Assert.Equal(id, await Resume(peer, "reversi"));

        var moves = Position(joined).GetProperty("moves").EnumerateArray().Take(2).Select(move => move.Text("id")).ToArray();
        Assert.Equal(2, moves.Length);
        await Error(await peer.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId = moves[0] }),
            HttpStatusCode.BadRequest, "not_your_turn");
        await Error(await host.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId = "illegal" }),
            HttpStatusCode.BadRequest, "illegal_move");
        Assert.Equal(Position(joined).GetRawText(), Position(await Ok(await host.Request(HttpMethod.Get, table))).GetRawText());

        var attempts = await Task.WhenAll(moves.Select(moveId =>
            host.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId })));
        var success = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.OK);
        var committed = await Ok(success);
        await Error(Assert.Single(attempts, response => response != success), HttpStatusCode.Conflict, "stale_room");
        var winningMove = moves[Array.IndexOf(attempts, success)];
        Assert.Equal(winningMove, Position(committed).At("lastMove", "id").GetString());
        var step = Assert.Single(AssertTurnMotion(joined, committed));
        Assert.True(JsonElement.DeepEquals(LegalMove(joined, winningMove), step.GetProperty("move")));
        AssertNoMotion(await Ok(await peer.Request(HttpMethod.Post, Api + "/join", request)));
        Assert.Equal(new[] { "bot", "name", "occupied", "seat" },
            committed.GetProperty("seats")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.False(committed.TryGetProperty("checkpoint", out _));

        var newer = await Create(host, reversi, 2);
        Assert.NotEqual(id, newer.Text("id"));
        Assert.Equal(newer.Text("id"), await Resume(host, "reversi"));
        Assert.Equal(id, await Resume(peer, "reversi"));
        await Ok(await host.Request(HttpMethod.Patch, "/portal/api/profile", new { displayName = "Renamed Host" }));

        await server.StopAsync();
        server.SetClockOffset(90_000);
        await server.StartAsync();
        var recovered = await Ok(await host.Request(HttpMethod.Get, table));
        AssertNoMotion(recovered);
        Assert.Equal(Revision(committed), Revision(recovered));
        Assert.Equal(Position(committed).GetRawText(), Position(recovered).GetRawText());
        Assert.Equal("Renamed Host", recovered.GetProperty("seats")[0].Text("name"));
        Assert.Equal(id, await Resume(host, "reversi"));
        await Error(await host.Request(HttpMethod.Get, Api + "/tables/" + newer.Text("id")),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await host.Request(HttpMethod.Post, table + "/moves", new { revision = 1, moveId = moves[0] }),
            HttpStatusCode.Conflict, "stale_room");
    }

    [Fact]
    public async Task ReversiMoveReturnsBothFlipsInOrderWithoutReplayingThemOnRead()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var guest = await Guest(server, "Black");
        var before = await Create(guest, Definition(await Catalog(guest), "reversi"), 2, true);
        AssertNoMotion(before);

        var path = Api + "/tables/" + before.Text("id");
        const string moveId = "r2c3";
        var committed = await Ok(await guest.Request(HttpMethod.Post, path + "/moves",
            new { revision = Revision(before), moveId }));
        var steps = AssertTurnMotion(before, committed);
        Assert.Collection(steps,
            human =>
            {
                Assert.True(JsonElement.DeepEquals(LegalMove(before, moveId), human.GetProperty("move")));
                AssertDiscs(human, ("r2c3", 0), ("r3c3", 0));
            },
            bot =>
            {
                Assert.Equal("r2c2", bot.At("move", "id").GetString());
                Assert.True(JsonElement.DeepEquals(Position(committed).GetProperty("lastMove"), bot.GetProperty("move")));
                AssertDiscs(bot, ("r2c2", 1), ("r3c3", 1));
            });

        Assert.Equal(before.Text("id"), await Resume(guest, "reversi"));
        var read = await Ok(await guest.Request(HttpMethod.Get, path));
        AssertNoMotion(read);
        Assert.Equal(Revision(committed), Revision(read));
        Assert.True(JsonElement.DeepEquals(Position(committed), Position(read)));
    }

    [Fact]
    public async Task LeavingRevokesInvitesAndRacesMovesWithoutLosingTheCommittedBoard()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var host = await Guest(server, "Host");
        var peer = await Guest(server, "Peer");
        var stranger = await Guest(server, "Stranger");
        var reversi = Definition(await Catalog(host), "reversi");
        var waiting = await Create(host, reversi, 2);
        var waitingPath = Api + "/tables/" + waiting.Text("id");
        var exit = waitingPath + "/leave";
        await Error(await stranger.Request(HttpMethod.Post, exit, new { revision = 0 }),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await host.Request(HttpMethod.Post, exit, new { revision = 0 }, origin: "https://another.example"),
            HttpStatusCode.Forbidden, "forbidden");
        await Error(await host.Request(HttpMethod.Post, exit, new { revision = 0, seat = 1 }),
            HttpStatusCode.BadRequest, "invalid_request");
        var closed = await Ok(await host.Request(HttpMethod.Post, exit, new { revision = 0 }));
        AssertClosed(closed, 0, 1);
        Assert.Equal(JsonValueKind.Null, Position(closed).ValueKind);
        Assert.Null(await Resume(host, "reversi"));
        await Error(await peer.Request(HttpMethod.Post, Api + "/join", new { invite = waiting.Text("invite") }),
            HttpStatusCode.NotFound, "room_not_found");
        await Error(await host.Request(HttpMethod.Post, Api + "/join", new { invite = waiting.Text("invite") }),
            HttpStatusCode.NotFound, "room_not_found");

        var created = await Create(host, reversi, 2);
        var path = Api + "/tables/" + created.Text("id");
        var playing = await Ok(await peer.Request(HttpMethod.Post, Api + "/join", new { invite = created.Text("invite") }));
        var revision = Revision(playing);
        var moveId = Position(playing).GetProperty("moves")[0].Text("id");
        var attempts = await Task.WhenAll(
            host.Request(HttpMethod.Post, path + "/moves", new { revision, moveId }),
            peer.Request(HttpMethod.Post, path + "/leave", new { revision }));
        var committed = await Ok(Assert.Single(attempts, response => response.IsSuccessStatusCode));
        await Error(Assert.Single(attempts, response => !response.IsSuccessStatusCode), HttpStatusCode.Conflict, "stale_room");
        Assert.Equal(revision + 1, Revision(committed));
        closed = committed.Text("status") == "closed" ? committed : await Ok(await peer.Request(
            HttpMethod.Post, path + "/leave", new { revision = Revision(committed) }));
        AssertClosed(closed, 1, Revision(committed) + (committed.Text("status") == "closed" ? 0 : 1));
        Assert.True(JsonElement.DeepEquals(Position(committed).GetProperty("cells"), Position(closed).GetProperty("cells")));
        Assert.Equal(JsonValueKind.Null, Position(closed).GetProperty("turn").ValueKind);
        Assert.Empty(Position(closed).GetProperty("moves").EnumerateArray());
        var current = await Ok(await host.Request(HttpMethod.Get, path));
        AssertClosed(current, 1, Revision(closed));
        Assert.Equal(0, current.GetProperty("seat").GetInt32());
        Assert.Null(await Resume(peer, "reversi"));
        Assert.Null(await Resume(host, "reversi"));
        await Error(await host.Request(HttpMethod.Post, path + "/moves", new { revision = Revision(closed), moveId }),
            HttpStatusCode.BadRequest, "room_closed");
    }

    [Fact]
    public async Task ChessResignationOutOfTurnPreservesFinalPositionAndCannotBeReplayedOrReopened()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var white = await Guest(server, "White");
        var black = await Guest(server, "Black");
        var stranger = await Guest(server, "Observer");
        var waiting = await Create(white, Definition(await Catalog(white), "chess"), 2);
        var path = Api + "/tables/" + waiting.Text("id");
        await Error(await white.Request(HttpMethod.Post, path + "/resign", new { revision = 0 }),
            HttpStatusCode.BadRequest, "room_not_started");
        var live = await Ok(await black.Request(HttpMethod.Post, Api + "/join", new { invite = waiting.Text("invite") }));
        await Error(await black.Request(HttpMethod.Post, path + "/leave", new { revision = Revision(live) }),
            HttpStatusCode.BadRequest, "resign_required");
        await Error(await stranger.Request(HttpMethod.Post, path + "/resign", new { revision = Revision(live) }),
            HttpStatusCode.NotFound, "room_not_found");
        var moved = await Ok(await white.Request(HttpMethod.Post, path + "/moves",
            new { revision = Revision(live), moveId = "e2e4" }));
        Assert.Equal(1, Position(moved).GetProperty("turn").GetInt32());
        await Error(await white.Request(HttpMethod.Post, path + "/resign", new { revision = Revision(live) }),
            HttpStatusCode.Conflict, "stale_room");
        var resigned = await Ok(await white.Request(HttpMethod.Post, path + "/resign", new { revision = Revision(moved) }));
        Assert.Equal("finished", resigned.Text("status"));
        Assert.Equal(Revision(moved) + 1, Revision(resigned));
        Assert.Equal("resignation", resigned.At("result", "cause").GetString());
        Assert.Equal(0, resigned.At("result", "loserSeat").GetInt32());
        Assert.Equal(1, resigned.At("result", "winnerSeat").GetInt32());
        Assert.Equal(1, Assert.Single(Position(resigned).GetProperty("winners").EnumerateArray()).GetInt32());
        Assert.True(JsonElement.DeepEquals(Position(moved).GetProperty("cells"), Position(resigned).GetProperty("cells")));
        Assert.Null(resigned.Text("invite"));
        Assert.Equal(JsonValueKind.Null, Position(resigned).GetProperty("turn").ValueKind);
        Assert.Empty(Position(resigned).GetProperty("moves").EnumerateArray());
        AssertNoMotion(resigned);
        Assert.False(resigned.TryGetProperty("checkpoint", out _));
        Assert.Null(await Resume(white, "chess"));
        Assert.Null(await Resume(black, "chess"));
        await Error(await black.Request(HttpMethod.Post, Api + "/join", new { invite = waiting.Text("invite") }),
            HttpStatusCode.NotFound, "room_not_found");

        await server.StopAsync();
        await server.StartAsync();
        var recovered = await Ok(await black.Request(HttpMethod.Get, path));
        AssertNoMotion(recovered);
        Assert.True(JsonElement.DeepEquals(resigned.GetProperty("result"), recovered.GetProperty("result")));
        Assert.True(JsonElement.DeepEquals(Position(resigned), Position(recovered)));
        foreach (var operation in new[] { "leave", "resign", "moves" })
        {
            object body = operation == "moves" ? new { revision = Revision(recovered), moveId = "e7e5" }
                : new { revision = Revision(recovered) };
            await Error(await black.Request(HttpMethod.Post, path + "/" + operation, body),
                HttpStatusCode.BadRequest, "game_finished");
        }
    }

    [Fact]
    public async Task LeavingCannotReplaceAnEngineResult()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var black = await Guest(server, "Black");
        var peer = await Guest(server, "White");
        var waiting = await Create(black, Definition(await Catalog(black), "go"), 2);
        var path = Api + "/tables/" + waiting.Text("id");
        var played = await Ok(await peer.Request(HttpMethod.Post, Api + "/join", new { invite = waiting.Text("invite") }));
        played = await Ok(await black.Request(HttpMethod.Post, path + "/moves", new { revision = Revision(played), moveId = "pass" }));
        played = await Ok(await peer.Request(HttpMethod.Post, path + "/moves", new { revision = Revision(played), moveId = "pass" }));
        Assert.Equal("finished", played.Text("status"));
        await Error(await black.Request(HttpMethod.Post, path + "/leave", new { revision = Revision(played) }),
            HttpStatusCode.BadRequest, "game_finished");
        var read = await Ok(await peer.Request(HttpMethod.Get, path));
        Assert.Equal(Revision(played), Revision(read));
        Assert.True(JsonElement.DeepEquals(Position(played), Position(read)));
        Assert.False(read.TryGetProperty("endReason", out _));
    }

    [Fact]
    public async Task GoPassIsAnAcceptedPlyWithNoChangedCells()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var guest = await Guest(server, "Black");
        var before = await Create(guest, Definition(await Catalog(guest), "go"), 2, true);
        var path = Api + "/tables/" + before.Text("id");
        var committed = await Ok(await guest.Request(HttpMethod.Post, path + "/moves",
            new { revision = Revision(before), moveId = "pass" }));

        Assert.Collection(AssertTurnMotion(before, committed),
            pass =>
            {
                Assert.True(JsonElement.DeepEquals(LegalMove(before, "pass"), pass.GetProperty("move")));
                Assert.Empty(pass.GetProperty("changes").EnumerateArray());
            },
            bot =>
            {
                var placed = Assert.Single(bot.GetProperty("changes").EnumerateArray());
                Assert.Equal(bot.At("move", "to").GetString(), placed.Text("id"));
                Assert.Equal(1, placed.GetProperty("owner").GetInt32());
            });
    }

    [Fact]
    public async Task ChessBotCommitsAReplyWhileMalformedRequestsDoNotCreateTables()
    {
        await using var server = PortalServer()
            .WithSetting("Portal__StockfishPath", Path.Combine(AppContext.BaseDirectory, "absent-stockfish"));
        await server.StartAsync();
        var guest = await Guest(server, "White");
        const string start = """{"game":"chess","variant":"standard","players":2,"bots":true}""";
        using var oversized = new ChunkedJson(start + new string(' ', 4096));
        await Error(await guest.Request(HttpMethod.Post, Api + "/tables", oversized), HttpStatusCode.BadRequest, "invalid_request");
        await Error(await guest.Request(HttpMethod.Post, Api + "/tables", new
        {
            game = "chess", variant = "standard", players = 6, bots = true
        }), HttpStatusCode.BadRequest, "invalid_request");
        using var duplicate = new StringContent(start.Insert(start.Length - 1, ",\"bots\":false"), Encoding.UTF8, "application/json");
        await Error(await guest.Request(HttpMethod.Post, Api + "/tables", duplicate), HttpStatusCode.BadRequest, "invalid_request");
        Assert.Null(await Resume(guest, "chess"));

        var chess = Definition(await Catalog(guest), "chess");
        var solo = await Create(guest, chess, 2, true);
        Assert.Equal("playing", solo.Text("status"));
        Assert.Null(solo.Text("invite"));
        Assert.True(solo.GetProperty("seats")[1].GetProperty("bot").GetBoolean());
        Assert.True(solo.GetProperty("seats")[1].GetProperty("occupied").GetBoolean());
        Assert.Contains(Position(solo).GetProperty("moves").EnumerateArray(), move => move.Text("id") == "e2e4");

        var path = Api + "/tables/" + solo.Text("id");
        var played = await Ok(await guest.Request(HttpMethod.Post, path + "/moves", new { revision = 0, moveId = "e2e4" }));
        var position = Position(played);
        Assert.Equal(0, position.GetProperty("turn").GetInt32());
        var botMove = position.GetProperty("lastMove");
        Assert.NotEqual("e2e4", botMove.Text("id"));
        Assert.Collection(AssertTurnMotion(solo, played),
            human => Assert.True(JsonElement.DeepEquals(LegalMove(solo, "e2e4"), human.GetProperty("move"))),
            bot => Assert.True(JsonElement.DeepEquals(botMove, bot.GetProperty("move"))));
        var pieces = position.GetProperty("cells").EnumerateArray().ToArray();
        Assert.Equal(0, Assert.Single(pieces, cell => cell.Text("id") == "e4").GetProperty("owner").GetInt32());
        Assert.Equal(1, Assert.Single(pieces, cell => cell.Text("id") == botMove.Text("to")).GetProperty("owner").GetInt32());
        Assert.Equal(position.GetRawText(), Position(await Ok(await guest.Request(HttpMethod.Get, path))).GetRawText());
    }

    [Fact]
    public async Task ChessBotPresetSurvivesRestartAndIsUsedForTheCommittedBotMove()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var guest = await Guest(server, "White");
        var catalog = await Catalog(guest);
        foreach (var (game, bots, difficulty) in new (string, bool, object?)[]
        {
            ("chess", true, null), ("chess", true, 2), ("chess", true, "Hard"), ("chess", true, "expert"),
            ("chess", false, "standard"), ("reversi", true, "easy")
        })
            await Error(await guest.Request(HttpMethod.Post, Api + "/tables", new
            {
                game, variant = Definition(catalog, game).GetProperty("variants")[0].Text("id"),
                players = 2, bots, botDifficulty = difficulty
            }), HttpStatusCode.BadRequest, "invalid_request");
        Assert.Null(await Resume(guest, "chess"));
        Assert.Null(await Resume(guest, "reversi"));

        var chess = Definition(catalog, "chess");
        foreach (var table in new[] { await Create(guest, chess, 2), await Create(guest, Definition(catalog, "reversi"), 2, true) })
            Assert.False(table.TryGetProperty("botDifficulty", out _));
        var created = new Dictionary<string, JsonElement>
        {
            ["standard"] = await Create(guest, chess, 2, true)
        };
        foreach (var preset in new[] { "easy", "hard" })
            created[preset] = await Ok(await guest.Request(HttpMethod.Post, Api + "/tables",
                new { game = "chess", variant = "standard", players = 2, bots = true, botDifficulty = preset }));
        foreach (var (preset, table) in created)
            Assert.Equal(preset, table.Text("botDifficulty"));

        await server.StopAsync();
        await server.StartAsync();
        var fallback = new ChessGame();
        var beforeBot = fallback.Apply(fallback.Start("standard", 2), "e2e4");
        Assert.NotEqual(fallback.SelectBotMove(beforeBot, "easy"), fallback.SelectBotMove(beforeBot, "standard"));
        foreach (var (preset, table) in created)
        {
            var path = Api + "/tables/" + table.Text("id");
            var read = await Ok(await guest.Request(HttpMethod.Get, path));
            Assert.Equal(preset, read.Text("botDifficulty"));
            var committed = await Ok(await guest.Request(HttpMethod.Post, path + "/moves",
                new { revision = Revision(read), moveId = "e2e4" }));
            Assert.Equal(preset, committed.Text("botDifficulty"));
            var reply = AssertTurnMotion(read, committed)[1];
            Assert.Equal(fallback.SelectBotMove(beforeBot, preset), reply.At("move", "id").GetString());
        }
    }

    [Fact]
    public async Task SixSeatsCanPollAtBrowserCadenceWithoutSharingTheProxyAddressQuota()
    {
        await using var server = PortalServer();
        await server.StartAsync();
        var guests = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Guest(server, $"Guest {i}")));
        var definition = Definition(await Catalog(guests[0]), "chinese-checkers");
        var created = await Create(guests[0], definition, 6);
        var invite = created.Text("invite");
        var joins = await Task.WhenAll(guests.Skip(1).Select(guest => Ok(guest.Request(HttpMethod.Post, Api + "/join", new { invite }))));
        Assert.Equal(Enumerable.Range(1, 5), joins.Select(t => t.GetProperty("seat").GetInt32()).Order());
        Assert.Single(joins, t => t.Text("status") == "playing");

        var path = Api + "/tables/" + created.Text("id");
        await Task.WhenAll(guests.Select(async (guest, index) =>
        {
            for (var poll = 0; poll < 30; poll++)
            {
                var table = await Ok(await guest.Request(HttpMethod.Get, path));
                Assert.Equal("playing", table.Text("status"));
                Assert.Equal(5, Revision(table));
                Assert.Equal($"Guest {index}", table.GetProperty("seats")[table.GetProperty("seat").GetInt32()].Text("name"));
            }
        }));
    }

    internal static Kestrel PortalServer() => new Kestrel().WithSetting("BASE_URL", "/portal")
        .WithSetting("Portal__Enabled", "true");

    private static async Task<User> Guest(Kestrel server, string name)
    {
        var guest = new User(server);
        var profile = await Ok(await guest.Request(HttpMethod.Post, "/portal/api/guest", new { displayName = name }));
        Assert.Equal(name, profile.Text("displayName"));
        return guest;
    }

    private static async Task<JsonElement[]> Catalog(User guest) =>
        (await Ok(await guest.Request(HttpMethod.Get, Api + "/games"))).GetProperty("games").EnumerateArray().ToArray();

    private static JsonElement Definition(JsonElement[] games, string id) => Assert.Single(games, game => game.Text("id") == id);

    private static Task<JsonElement> Create(User guest, JsonElement definition, int players, bool bots = false) =>
        Ok(guest.Request(HttpMethod.Post, Api + "/tables", new
        {
            game = definition.Text("id"), variant = definition.GetProperty("variants")[0].Text("id"), players, bots
        }));

    private static async Task<string?> Resume(User guest, string game)
    {
        var response = await Ok(await guest.Request(HttpMethod.Get, Api + "/resume?game=" + game));
        AssertNoMotion(response);
        return response.Text("tableId");
    }

    private static JsonElement Position(JsonElement table) => table.GetProperty("position");
    private static long Revision(JsonElement table) => table.GetProperty("revision").GetInt64();
    private static JsonElement.ArrayEnumerator Cells(JsonElement table) => Position(table).GetProperty("cells").EnumerateArray();
    private static JsonElement LegalMove(JsonElement table, string id) =>
        Assert.Single(Position(table).GetProperty("moves").EnumerateArray(), move => move.Text("id") == id);

    private static void AssertNoMotion(JsonElement response) =>
        Assert.DoesNotContain("turnMotion", response.EnumerateObject().Select(property => property.Name));

    private static void AssertClosed(JsonElement table, int seat, long revision)
    {
        Assert.Equal("closed", table.Text("status"));
        Assert.Equal("left", table.Text("endReason"));
        Assert.Equal(seat, table.GetProperty("endedBySeat").GetInt32());
        Assert.Equal(revision, Revision(table));
        Assert.Null(table.Text("invite"));
        AssertNoMotion(table);
    }

    private static JsonElement[] AssertTurnMotion(JsonElement before, JsonElement committed)
    {
        Assert.True(committed.TryGetProperty("turnMotion", out var motion), "The committed move is missing turnMotion.");
        Assert.Equal(Revision(before), motion.GetProperty("fromRevision").GetInt64());
        Assert.Equal(Revision(before) + 1, Revision(committed));
        var steps = motion.GetProperty("steps").EnumerateArray().ToArray();
        var replay = Cells(before).ToDictionary(cell => cell.Text("id"), StringComparer.Ordinal);
        foreach (var step in steps)
        foreach (var cell in step.GetProperty("changes").EnumerateArray())
            replay[cell.Text("id")] = cell;
        var final = Cells(committed).ToArray();
        Assert.Equal(final.Length, replay.Count);
        Assert.All(final, cell =>
        {
            var id = cell.Text("id");
            Assert.True(JsonElement.DeepEquals(cell, replay[id]), $"Replayed cell {id} differs from the committed board.");
        });
        return steps;
    }

    private static void AssertDiscs(JsonElement step, params (string Id, int Owner)[] expected)
    {
        var changes = step.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(expected.OrderBy(cell => cell.Id, StringComparer.Ordinal),
            changes.Select(cell => (Id: cell.Text("id"), Owner: cell.GetProperty("owner").GetInt32()))
                .OrderBy(cell => cell.Id, StringComparer.Ordinal));
        Assert.All(changes, cell => Assert.Equal("disc", cell.Text("piece")));
    }

    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> response) => await Ok(await response);

    internal static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
            Assert.True(response.Headers.CacheControl?.NoStore);
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
    }

    internal static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, body.Error());
            AssertNoMotion(body);
        }
    }

    private sealed class ChunkedJson : HttpContent
    {
        private readonly byte[] _content;

        public ChunkedJson(string json)
        {
            _content = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
